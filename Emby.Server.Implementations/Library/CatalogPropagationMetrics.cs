using Prometheus;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Exposes the health of Redis-backed catalog propagation to Prometheus.
/// </summary>
internal static class CatalogPropagationMetrics
{
    public const string SubscriberConnectedMetricName = "jellyfin_catalog_propagation_subscriber_connected";
    public const string LastAppliedSequenceMetricName = "jellyfin_catalog_propagation_last_applied_sequence";
    public const string GapDetectionsMetricName = "jellyfin_catalog_propagation_gap_detections_total";
    public const string ReconnectFailuresMetricName = "jellyfin_catalog_propagation_reconnect_failures_total";
    public const string FullResyncFailuresMetricName = "jellyfin_catalog_propagation_full_resync_failures_total";
    public const string ApplyFailuresMetricName = "jellyfin_catalog_propagation_apply_failures_total";

    private static readonly Gauge SubscriberConnected = Metrics.CreateGauge(
        SubscriberConnectedMetricName,
        "Whether this Jellyfin process has an active Redis catalog propagation subscription.");
    private static readonly Gauge LastAppliedSequence = Metrics.CreateGauge(
        LastAppliedSequenceMetricName,
        "Latest Redis catalog propagation sequence applied or recovered by this Jellyfin process.");
    private static readonly Counter GapDetections = Metrics.CreateCounter(
        GapDetectionsMetricName,
        "Number of Redis catalog propagation sequence gaps detected by this Jellyfin process.");
    private static readonly Counter ReconnectFailures = Metrics.CreateCounter(
        ReconnectFailuresMetricName,
        "Number of failed Redis catalog propagation recovery attempts.");
    private static readonly Counter FullResyncFailures = Metrics.CreateCounter(
        FullResyncFailuresMetricName,
        "Number of failed local catalog full-resynchronization deliveries.");
    private static readonly Counter ApplyFailures = Metrics.CreateCounter(
        ApplyFailuresMetricName,
        "Number of malformed or failed Redis catalog propagation deliveries.");

    public static void SetSubscriberConnected(bool connected)
        => SubscriberConnected.Set(connected ? 1 : 0);

    public static void SetLastAppliedSequence(long sequence)
        => LastAppliedSequence.Set(sequence);

    public static void RecordGapDetection()
        => GapDetections.Inc();

    public static void RecordReconnectFailure()
        => ReconnectFailures.Inc();

    public static void RecordFullResyncFailure()
        => FullResyncFailures.Inc();

    public static void RecordApplyFailure()
        => ApplyFailures.Inc();
}
