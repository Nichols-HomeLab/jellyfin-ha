using System;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Emby.Server.Implementations.ScheduledTasks;

/// <summary>
/// Emits bounded, correlation-safe lifecycle markers for scheduled tasks.
/// </summary>
internal sealed class ScheduledTaskDiagnostics
{
    internal const int MarkerEventId = 8600;
    internal const int MaxTaskKeyLength = 64;
    internal const int MaxTaskNameLength = 128;
    internal const int MaxInstanceContextLength = 63;
    internal const int MaxBuildIdentityLength = 40;
    internal const int MaxRenderedMarkerBytes = 1024;

    private const string Schema = "scheduled_task_lifecycle_v1";
    private const string OtherTaskName = "Other scheduled task";
    private const string MarkerTemplate = "scheduled_task_diagnostic schema={Schema} run_id={RunId} task_id={TaskId} task_key={TaskKey} task_name={TaskName} phase={Phase} outcome={Outcome} started_utc={StartedUtc} ended_utc={EndedUtc} duration_ms={DurationMs} ownership_state={OwnershipState} instance_context={InstanceContext} build_identity={BuildIdentity}";
    private static readonly ScheduledTaskMetrics DefaultMetrics = new(Metrics.DefaultRegistry);

    private readonly ILogger _logger;
    private readonly ScheduledTaskMetrics _metrics;
    private readonly string _instanceContext;
    private readonly string _buildIdentity;

    public ScheduledTaskDiagnostics(ILogger logger)
        : this(
            logger,
            DefaultMetrics,
            Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName,
            Environment.GetEnvironmentVariable("JELLYFIN_BUILD_IDENTITY")
                ?? string.Create(CultureInfo.InvariantCulture, $"development-{typeof(ScheduledTaskDiagnostics).Assembly.GetName().Version}"))
    {
    }

    internal ScheduledTaskDiagnostics(
        ILogger logger,
        ScheduledTaskMetrics metrics,
        string instanceContext,
        string buildIdentity)
    {
        _logger = logger;
        _metrics = metrics;
        _instanceContext = NormalizeInstanceContext(instanceContext);
        _buildIdentity = NormalizeBuildIdentity(buildIdentity);
    }

    public ScheduledTaskDiagnosticRun Admit(string taskId, string taskKey)
    {
        var startedUtc = DateTime.UtcNow;
        var run = CreateRun(taskId, taskKey, startedUtc);
        Emit(run, "start", "admitted", startedUtc, 0, "owner");
        _metrics.Record(run.MetricTaskKey, "admitted");
        return run;
    }

    public void RecordFollowerSkip(string taskId, string taskKey)
    {
        var timestampUtc = DateTime.UtcNow;
        var run = CreateRun(taskId, taskKey, timestampUtc);
        Emit(run, "skip", "follower_skipped", timestampUtc, 0, "follower");
        _metrics.Record(run.MetricTaskKey, "follower_skipped");
    }

    public void RecordOwnershipLoss(ScheduledTaskDiagnosticRun run)
    {
        if (!run.TryMarkOwnershipLoss())
        {
            return;
        }

        var timestampUtc = DateTime.UtcNow;
        var durationMilliseconds = Math.Max(0, (long)(timestampUtc - run.StartedUtc).TotalMilliseconds);
        Emit(run, "ownership", "ownership_lost", timestampUtc, durationMilliseconds, "lost");
        _metrics.Record(run.MetricTaskKey, "ownership_lost");
    }

    public bool TryRecordTerminal(
        ScheduledTaskDiagnosticRun run,
        string outcome,
        DateTime endedUtc,
        string ownershipState)
    {
        if (!run.TryMarkTerminal())
        {
            return false;
        }

        var duration = endedUtc - run.StartedUtc;
        var durationMilliseconds = Math.Max(0, (long)duration.TotalMilliseconds);
        Emit(run, "terminal", outcome, endedUtc, durationMilliseconds, ownershipState);
        _metrics.Record(run.MetricTaskKey, outcome);
        return true;
    }

    private static ScheduledTaskDiagnosticRun CreateRun(string taskId, string taskKey, DateTime startedUtc)
    {
        var metricTaskKey = ScheduledTaskMetrics.NormalizeTaskKey(taskKey);
        return new ScheduledTaskDiagnosticRun(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            NormalizeTaskId(taskId),
            metricTaskKey,
            metricTaskKey,
            metricTaskKey == ScheduledTaskMetrics.OtherTaskKey ? OtherTaskName : metricTaskKey,
            startedUtc);
    }

    private void Emit(
        ScheduledTaskDiagnosticRun run,
        string phase,
        string outcome,
        DateTime endedUtc,
        long durationMilliseconds,
        string ownershipState)
    {
        _logger.LogInformation(
            new EventId(MarkerEventId, "ScheduledTaskDiagnostic"),
            MarkerTemplate,
            Schema,
            run.RunId,
            run.TaskId,
            run.TaskKey,
            run.TaskName,
            NormalizePhase(phase),
            ScheduledTaskMetrics.NormalizeOutcome(outcome),
            run.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            endedUtc.ToString("O", CultureInfo.InvariantCulture),
            durationMilliseconds,
            NormalizeOwnershipState(ownershipState),
            _instanceContext,
            _buildIdentity);
    }

    private static string NormalizeTaskId(string? taskId)
        => IsHexValue(taskId, 32) ? taskId!.ToLowerInvariant() : "unknown";

    private static string NormalizeBuildIdentity(string? buildIdentity)
        => IsHexValue(buildIdentity, MaxBuildIdentityLength) ? buildIdentity!.ToLowerInvariant() : "unknown";

    private static string NormalizeInstanceContext(string? instanceContext)
    {
        if (string.IsNullOrEmpty(instanceContext)
            || instanceContext.Length > MaxInstanceContextLength
            || !instanceContext.All(character => (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-'))
        {
            return "unknown";
        }

        return instanceContext;
    }

    private static bool IsHexValue(string? value, int requiredLength)
        => value is not null && value.Length == requiredLength && value.All(Uri.IsHexDigit);

    private static string NormalizePhase(string? phase)
        => phase is "start" or "terminal" or "skip" or "ownership" ? phase : "terminal";

    private static string NormalizeOwnershipState(string? ownershipState)
        => ownershipState is "owner" or "lost" or "follower" ? ownershipState : "lost";
}
