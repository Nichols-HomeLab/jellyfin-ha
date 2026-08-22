using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Prometheus;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

/// <summary>
/// Behavioral tests for bounded scheduled-task lifecycle diagnostics.
/// </summary>
public sealed class ScheduledTaskDiagnosticsTests : IDisposable
{
    private const string Secret = "https://admin:password@example.test/private?token=secret Authorization: Bearer abc /media/private/movie.mkv SELECT * FROM users";
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public enum TaskBehavior
    {
        Complete,
        Fail,
        Cancel,
        OwnershipLoss,
        IgnoreCancellation
    }

    /// <summary>
    /// Every admitted outcome emits one correlated start and exactly one terminal marker.
    /// </summary>
    /// <param name="behavior">Task behavior.</param>
    /// <param name="expectedOutcome">Expected diagnostic outcome.</param>
    /// <returns>A task representing the asynchronous test.</returns>
    [Theory]
    [InlineData(TaskBehavior.Complete, "completed")]
    [InlineData(TaskBehavior.Fail, "failed")]
    [InlineData(TaskBehavior.Cancel, "cancelled")]
    [InlineData(TaskBehavior.OwnershipLoss, "ownership_lost")]
    public async Task AdmittedRun_EmitsOneCorrelatedTerminalMarker(TaskBehavior behavior, string expectedOutcome)
    {
        var fixture = CreateFixture(behavior);

        var execution = fixture.Worker.Execute(new TaskOptions());
        await fixture.Task.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (behavior == TaskBehavior.Cancel)
        {
            fixture.Worker.Cancel();
        }
        else if (behavior == TaskBehavior.OwnershipLoss)
        {
            fixture.Ownership.LoseOwnership();
        }

        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        var markers = fixture.Logger.Entries.Where(entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId).ToArray();
        var start = Assert.Single(markers, entry => entry.Property("Phase") == "start");
        var terminal = Assert.Single(markers, entry => entry.Property("Phase") == "terminal");
        Assert.Equal("admitted", start.Property("Outcome"));
        Assert.Equal(expectedOutcome, terminal.Property("Outcome"));
        Assert.Equal(start.Property("RunId"), terminal.Property("RunId"));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", start.Property("BuildIdentity"));
        Assert.Single(fixture.CompletedResults);
    }

    /// <summary>
    /// A follower skip is observable but is not an admitted run and does not execute work.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task Follower_EmitsSkipWithoutAdmissionOrCompletion()
    {
        var fixture = CreateFixture(TaskBehavior.Complete, isOwner: false);

        await fixture.Worker.Execute(new TaskOptions());

        Assert.Equal(0, fixture.Task.ExecutionCount);
        Assert.Empty(fixture.CompletedResults);
        var marker = Assert.Single(fixture.Logger.Entries, entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId);
        Assert.Equal("skip", marker.Property("Phase"));
        Assert.Equal("follower_skipped", marker.Property("Outcome"));
        Assert.Equal("follower", marker.Property("OwnershipState"));
    }

    /// <summary>
    /// A non-cooperative task is reported aborted once even if it exits later.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task DisposeTimeout_EmitsExactlyOneAbortedTerminal()
    {
        var fixture = CreateFixture(TaskBehavior.IgnoreCancellation);
        var execution = fixture.Worker.Execute(new TaskOptions());
        await fixture.Task.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Worker.Dispose();

        Assert.Equal(TaskCompletionStatus.Aborted, Assert.Single(fixture.CompletedResults).Status);
        fixture.Task.Release();
        await execution.WaitAsync(TimeSpan.FromSeconds(2));
        var terminals = fixture.Logger.Entries
            .Where(entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId && entry.Property("Phase") == "terminal")
            .ToArray();
        Assert.Equal("aborted", Assert.Single(terminals).Property("Outcome"));
        Assert.Single(fixture.CompletedResults);
    }

    /// <summary>
    /// A cooperative task cancelled by disposal reports cancellation, never a second abort.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task CooperativeDispose_EmitsExactlyOneCancelledTerminal()
    {
        var fixture = CreateFixture(TaskBehavior.Cancel);
        var execution = fixture.Worker.Execute(new TaskOptions());
        await fixture.Task.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Worker.Dispose();
        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TaskCompletionStatus.Cancelled, Assert.Single(fixture.CompletedResults).Status);
        var terminal = Assert.Single(
            fixture.Logger.Entries,
            entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId && entry.Property("Phase") == "terminal");
        Assert.Equal("cancelled", terminal.Property("Outcome"));
    }

    /// <summary>
    /// Lease loss remains visible even when task code ignores cancellation and returns normally.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task IgnoredOwnershipLoss_EmitsOwnershipMarkerAndCompletedTerminal()
    {
        var fixture = CreateFixture(TaskBehavior.IgnoreCancellation);
        var execution = fixture.Worker.Execute(new TaskOptions());
        await fixture.Task.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Ownership.LoseOwnership();
        fixture.Task.Release();
        await execution.WaitAsync(TimeSpan.FromSeconds(2));

        var markers = fixture.Logger.Entries.Where(entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId).ToArray();
        var ownership = Assert.Single(markers, entry => entry.Property("Phase") == "ownership");
        var terminal = Assert.Single(markers, entry => entry.Property("Phase") == "terminal");
        Assert.Equal("ownership_lost", ownership.Property("Outcome"));
        Assert.Equal("completed", terminal.Property("Outcome"));
        Assert.Equal("lost", terminal.Property("OwnershipState"));
        Assert.Equal(ownership.Property("RunId"), terminal.Property("RunId"));
    }

    /// <summary>
    /// A failing execution subscriber still leaves the admitted run with one terminal marker.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task ThrowingTaskExecutingSubscriber_EmitsFailedTerminalAndPreservesFault()
    {
        var fixture = CreateFixture(TaskBehavior.Complete);
        fixture.Manager.TaskExecuting += (_, _) => throw new InvalidOperationException(Secret);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker.Execute(new TaskOptions()));

        Assert.Contains(Secret, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Task.ExecutionCount);
        Assert.Equal(TaskCompletionStatus.Failed, Assert.Single(fixture.CompletedResults).Status);
        var terminal = Assert.Single(
            fixture.Logger.Entries,
            entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId && entry.Property("Phase") == "terminal");
        Assert.Equal("failed", terminal.Property("Outcome"));
        Assert.Null(terminal.Exception);
        Assert.DoesNotContain(Secret, terminal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnostic markers never receive or render the task exception.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task FailureMarker_RedactsSecretBearingExceptionAndUsesOnlyAllowlistedFields()
    {
        var fixture = CreateFixture(TaskBehavior.Fail);

        await fixture.Worker.Execute(new TaskOptions());

        var marker = Assert.Single(
            fixture.Logger.Entries,
            entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId && entry.Property("Phase") == "terminal");
        Assert.Null(marker.Exception);
        Assert.DoesNotContain(Secret, marker.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", marker.Message, StringComparison.OrdinalIgnoreCase);
        var restrictedExceptionLog = Assert.Single(fixture.Logger.Entries, entry => entry.Exception is InvalidOperationException);
        Assert.Contains(Secret, restrictedExceptionLog.Exception!.Message, StringComparison.Ordinal);
        Assert.Equal(
            new[]
            {
                "BuildIdentity",
                "DurationMs",
                "EndedUtc",
                "InstanceContext",
                "Outcome",
                "OwnershipState",
                "Phase",
                "RunId",
                "Schema",
                "StartedUtc",
                "TaskId",
                "TaskKey",
                "TaskName"
            },
            marker.Properties.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Marker string fields and total rendered size stay within fixed bounds.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task MarkerFields_AreSanitizedAndBounded()
    {
        var unsafeValue = Secret + "\r\n" + new string('x', 2048);
        var logger = new RecordingLogger();
        var diagnostics = new ScheduledTaskDiagnostics(
            logger,
            new ScheduledTaskMetrics(Metrics.NewCustomRegistry()),
            unsafeValue,
            unsafeValue);
        var fixture = CreateFixture(
            TaskBehavior.Complete,
            key: unsafeValue,
            name: unsafeValue,
            logger: logger,
            diagnostics: diagnostics);

        await fixture.Worker.Execute(new TaskOptions());

        foreach (var marker in fixture.Logger.Entries.Where(entry => entry.EventId.Id == ScheduledTaskDiagnostics.MarkerEventId))
        {
            Assert.InRange(marker.Property("TaskKey").Length, 1, ScheduledTaskDiagnostics.MaxTaskKeyLength);
            Assert.InRange(marker.Property("TaskName").Length, 1, ScheduledTaskDiagnostics.MaxTaskNameLength);
            Assert.InRange(marker.Property("InstanceContext").Length, 1, ScheduledTaskDiagnostics.MaxInstanceContextLength);
            Assert.InRange(marker.Property("BuildIdentity").Length, 1, ScheduledTaskDiagnostics.MaxBuildIdentityLength);
            Assert.DoesNotContain('\r', marker.Message);
            Assert.DoesNotContain('\n', marker.Message);
            Assert.DoesNotContain(Secret, marker.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("password", marker.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("other", marker.Property("TaskKey"));
            Assert.Equal("Other scheduled task", marker.Property("TaskName"));
            Assert.Equal("unknown", marker.Property("InstanceContext"));
            Assert.Equal("unknown", marker.Property("BuildIdentity"));
            Assert.InRange(Encoding.UTF8.GetByteCount(marker.Message), 1, ScheduledTaskDiagnostics.MaxRenderedMarkerBytes);
        }
    }

    /// <summary>
    /// Prometheus series use only fixed outcome values and allowlisted task-key values.
    /// </summary>
    /// <returns>A task representing the asynchronous test.</returns>
    [Fact]
    public async Task Metrics_HaveStableLabelsAndDoNotExposeUnsafeContext()
    {
        var registry = Metrics.NewCustomRegistry();
        var logger = new RecordingLogger();
        var metrics = new ScheduledTaskMetrics(registry);
        var diagnostics = new ScheduledTaskDiagnostics(logger, metrics, "pod-test", "0123456789abcdef0123456789abcdef01234567");

        for (var index = 0; index < 20; index++)
        {
            var fixture = CreateFixture(
                TaskBehavior.Complete,
                key: $"unknown-{index}-{Secret}",
                name: $"unsafe-{index}-{Secret}",
                logger: logger,
                diagnostics: diagnostics);
            await fixture.Worker.Execute(new TaskOptions());
        }

        var scrape = await ScrapeAsync(registry);
        var series = scrape.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(ScheduledTaskMetrics.LifecycleMetricName + "{", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, series.Length);
        Assert.All(series, line => Assert.Contains("task_key=\"other\"", line, StringComparison.Ordinal));
        Assert.Contains(series, line => line.Contains("outcome=\"admitted\"", StringComparison.Ordinal));
        Assert.Contains(series, line => line.Contains("outcome=\"completed\"", StringComparison.Ordinal));
        Assert.DoesNotContain("run_id", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain("task_name", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, scrape, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private Fixture CreateFixture(
        TaskBehavior behavior,
        bool isOwner = true,
        string key = "RefreshLibrary",
        string name = "Scan Media Library",
        RecordingLogger? logger = null,
        ScheduledTaskDiagnostics? diagnostics = null)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        logger ??= new RecordingLogger();
        var registry = Metrics.NewCustomRegistry();
        diagnostics ??= new ScheduledTaskDiagnostics(logger, new ScheduledTaskMetrics(registry), "jellyfin-test-0", "0123456789abcdef0123456789abcdef01234567");
        var ownership = new TestCatalogOwnership(isOwner);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(item => item.ConfigurationDirectoryPath).Returns(Path.Combine(_temporaryDirectory, "config"));
        paths.SetupGet(item => item.DataPath).Returns(Path.Combine(_temporaryDirectory, "data"));
        var manager = new TaskManager(paths.Object, ownership, new ForwardingLogger<TaskManager>(logger));
        var task = new ControllableScheduledTask(behavior, key, name);
        var worker = new ScheduledTaskWorker(task, paths.Object, manager, ownership, logger, diagnostics);
        var completed = new List<TaskResult>();
        manager.TaskCompleted += (_, eventArgs) => completed.Add(eventArgs.Result);
        return new Fixture(worker, task, ownership, logger, manager, completed);
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record Fixture(
        ScheduledTaskWorker Worker,
        ControllableScheduledTask Task,
        TestCatalogOwnership Ownership,
        RecordingLogger Logger,
        TaskManager Manager,
        List<TaskResult> CompletedResults);

    private sealed class TestCatalogOwnership(bool isOwner) : ICatalogOwnership, IDisposable
    {
        private readonly CancellationTokenSource _ownershipLost = new();
        private bool _isOwner = isOwner;

        public bool TryGetCatalogWriteToken(out CancellationToken ownershipLost)
        {
            ownershipLost = _ownershipLost.Token;
            return _isOwner;
        }

        public void LoseOwnership()
        {
            _isOwner = false;
            _ownershipLost.Cancel();
        }

        public void Dispose() => _ownershipLost.Dispose();
    }

    private sealed class ControllableScheduledTask(TaskBehavior behavior, string key, string name) : IScheduledTask
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public string Name { get; } = name;

        public string Key { get; } = key;

        public string Description => "Diagnostic test task";

        public string Category => "Test";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            Started.TrySetResult();
            switch (behavior)
            {
                case TaskBehavior.Fail:
                    throw new InvalidOperationException(Secret + new string('z', 4096));
                case TaskBehavior.Cancel:
                case TaskBehavior.OwnershipLoss:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    break;
                case TaskBehavior.IgnoreCancellation:
                    await _release.Task;
                    break;
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(item => item.Key != "{OriginalFormat}").ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<string, object?>();
            lock (_sync)
            {
                _entries.Add(new LogEntry(eventId, exception, formatter(state, exception), properties));
            }
        }
    }

    private sealed class ForwardingLogger<T>(RecordingLogger logger) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => logger.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed record LogEntry(EventId EventId, Exception? Exception, string Message, IReadOnlyDictionary<string, object?> Properties)
    {
        public string Property(string key) => Assert.IsType<string>(Properties[key]);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
