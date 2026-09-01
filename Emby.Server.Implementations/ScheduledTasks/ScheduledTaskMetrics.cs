using System;
using System.Collections.Generic;
using Prometheus;

namespace Emby.Server.Implementations.ScheduledTasks;

/// <summary>
/// Low-cardinality scheduled-task lifecycle counters.
/// </summary>
internal sealed class ScheduledTaskMetrics
{
    internal const string LifecycleMetricName = "jellyfin_scheduled_task_lifecycle_total";
    internal const string DiagnosticsInfoMetricName = "jellyfin_scheduled_task_diagnostics_info";
    internal const string OtherTaskKey = "other";

    private static readonly HashSet<string> AllowedTaskKeys = new(StringComparer.Ordinal)
    {
        "AudioNormalization",
        "CleanActivityLog",
        "CleanCollectionsAndPlaylists",
        "CleanupUserDataTask",
        "CleanLogFiles",
        "DeleteCacheFiles",
        "DeleteTranscodeFiles",
        "DownloadLyrics",
        "DownloadSubtitles",
        "KeyframeExtraction",
        "MoveTrickplayImages",
        "OptimizeDatabaseTask",
        "PluginUpdates",
        "RefreshChapterImages",
        "RefreshGuide",
        "RefreshInternetChannels",
        "RefreshLibrary",
        "RefreshPeople",
        "RefreshTrickplayImages",
        "TaskExtractMediaSegments"
    };

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "admitted",
        "completed",
        "failed",
        "cancelled",
        "ownership_lost",
        "aborted",
        "follower_skipped"
    };

    private readonly Counter _lifecycle;

    public ScheduledTaskMetrics(CollectorRegistry registry)
    {
        var factory = Metrics.WithCustomRegistry(registry);
        _lifecycle = factory.CreateCounter(
            LifecycleMetricName,
            "Bounded Jellyfin scheduled-task lifecycle events by task key and outcome.",
            new CounterConfiguration
            {
                LabelNames = ["task_key", "outcome"],
                SuppressInitialValue = true
            });
        factory.CreateGauge(
            DiagnosticsInfoMetricName,
            "Whether bounded Jellyfin scheduled-task diagnostics are active.").Set(1);
    }

    public void Record(string taskKey, string outcome)
    {
        _lifecycle.WithLabels(NormalizeTaskKey(taskKey), NormalizeOutcome(outcome)).Inc();
    }

    public static string NormalizeTaskKey(string? taskKey)
        => taskKey is not null && AllowedTaskKeys.Contains(taskKey) ? taskKey : OtherTaskKey;

    public static string NormalizeOutcome(string? outcome)
        => outcome is not null && AllowedOutcomes.Contains(outcome) ? outcome : "failed";
}
