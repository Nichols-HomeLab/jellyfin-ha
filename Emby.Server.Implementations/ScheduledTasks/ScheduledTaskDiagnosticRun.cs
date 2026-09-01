using System;
using System.Threading;

namespace Emby.Server.Implementations.ScheduledTasks;

/// <summary>
/// Per-admission context and terminal-emission gate.
/// </summary>
internal sealed class ScheduledTaskDiagnosticRun(
    string runId,
    string taskId,
    string taskKey,
    string metricTaskKey,
    string taskName,
    DateTime startedUtc)
{
    private int _terminalRecorded;
    private int _ownershipLossRecorded;

    public string RunId { get; } = runId;

    public string TaskId { get; } = taskId;

    public string TaskKey { get; } = taskKey;

    public string MetricTaskKey { get; } = metricTaskKey;

    public string TaskName { get; } = taskName;

    public DateTime StartedUtc { get; } = startedUtc;

    public bool TryMarkTerminal() => Interlocked.Exchange(ref _terminalRecorded, 1) == 0;

    public bool TryMarkOwnershipLoss() => Interlocked.Exchange(ref _ownershipLossRecorded, 1) == 0;
}
