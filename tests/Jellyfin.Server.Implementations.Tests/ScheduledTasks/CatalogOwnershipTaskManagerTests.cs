using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

/// <summary>
/// Tests scheduled-task behavior at the cluster catalog ownership seam.
/// </summary>
public sealed class CatalogOwnershipTaskManagerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Followers do not queue or execute scheduled catalog work.
    /// </summary>
    [Fact]
    public async Task Follower_DoesNotExecuteScheduledWork()
    {
        var task = new RecordingScheduledTask();
        using var ownership = new TestCatalogOwnership(isOwner: false);
        using var manager = CreateManager(ownership);
        manager.AddTasks([task]);

        manager.QueueScheduledTask(task, new TaskOptions());
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, task.ExecutionCount);
        Assert.Equal(TaskState.Idle, Assert.Single(manager.ScheduledTasks).State);
    }

    /// <summary>
    /// The owner executes scheduled work through the unchanged task-manager interface.
    /// </summary>
    [Fact]
    public async Task Owner_ExecutesScheduledWork()
    {
        var task = new RecordingScheduledTask();
        using var ownership = new TestCatalogOwnership(isOwner: true);
        using var manager = CreateManager(ownership);
        manager.AddTasks([task]);

        manager.QueueScheduledTask(task, new TaskOptions());
        await task.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, task.ExecutionCount);
    }

    /// <summary>
    /// A running scheduled task is cancelled promptly when its owner loses the lease.
    /// </summary>
    [Fact]
    public async Task RunningWork_IsCancelledWhenOwnershipIsLost()
    {
        using var ownership = new TestCatalogOwnership(isOwner: true);
        var task = new RecordingScheduledTask(waitForCancellation: true);
        using var manager = CreateManager(ownership);
        manager.AddTasks([task]);

        manager.QueueScheduledTask(task, new TaskOptions());
        await task.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ownership.LoseOwnership();

        var cancelled = await task.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancelled);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private TaskManager CreateManager(ICatalogOwnership ownership)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(i => i.ConfigurationDirectoryPath).Returns(_temporaryDirectory);
        paths.SetupGet(i => i.DataPath).Returns(_temporaryDirectory);
        return new TaskManager(paths.Object, ownership, NullLogger<TaskManager>.Instance);
    }

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

    private sealed class RecordingScheduledTask(bool waitForCancellation = false) : IScheduledTask
    {
        public TaskCompletionSource<bool> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public string Name => "Catalog mutation";

        public string Description => "Test catalog mutation";

        public string Category => "Library";

        public string Key => "TestCatalogMutation";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            Executed.TrySetResult(true);
            if (waitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult(cancellationToken.IsCancellationRequested);
                    throw;
                }
            }
        }
    }
}
