using Prometheus;

namespace Jellyfin.HotCache.Worker;

/// <summary>Stable, low-cardinality Prometheus signals for the hot-cache worker.</summary>
public static class HotCacheMetrics
{
    private static readonly Counter Jobs = Metrics.CreateCounter("jellyfin_hot_cache_jobs_total", "Hot-cache jobs by outcome.", new CounterConfiguration { LabelNames = ["kind", "outcome"] });
    private static readonly Counter Bytes = Metrics.CreateCounter("jellyfin_hot_cache_bytes_total", "Bytes copied into the hot cache.");
    private static readonly Counter Evictions = Metrics.CreateCounter("jellyfin_hot_cache_evictions_total", "Hot-cache evictions by reason.", new CounterConfiguration { LabelNames = ["reason"] });
    private static readonly Counter Failures = Metrics.CreateCounter("jellyfin_hot_cache_failures_total", "Hot-cache failures by component.", new CounterConfiguration { LabelNames = ["component"] });
    private static readonly Gauge BackendUp = Metrics.CreateGauge("jellyfin_hot_cache_backend_up", "One when PostgreSQL is reachable by the worker.");
    private static readonly Gauge StorageUp = Metrics.CreateGauge("jellyfin_hot_cache_storage_up", "One when the configured hot-cache mount can be inspected.");
    private static readonly Gauge StorageBytes = Metrics.CreateGauge("jellyfin_hot_cache_storage_bytes", "Observed hot-cache filesystem capacity by state.", new GaugeConfiguration { LabelNames = ["state"] });
    private static readonly Gauge DatabaseBytes = Metrics.CreateGauge("jellyfin_hot_cache_database_bytes", "Total bytes occupied by hot-cache PostgreSQL tables.");
    private static readonly Gauge EventRows = Metrics.CreateGauge("jellyfin_hot_cache_events_rows", "Retained hot-cache event rows.");
    private static readonly Gauge Fallbacks = Metrics.CreateGauge("jellyfin_hot_cache_fallbacks_recent", "Playback fallback observations in the trailing fifteen minutes by health class.", new GaugeConfiguration { LabelNames = ["class"] });
    private static readonly Gauge QueueDepth = Metrics.CreateGauge("jellyfin_hot_cache_queue_depth", "Pending or running hot-cache jobs.");
    private static readonly Gauge QueueOldestAge = Metrics.CreateGauge("jellyfin_hot_cache_queue_oldest_age_seconds", "Age of the oldest queued hot-cache job.");
    private static readonly Gauge LeaseAge = Metrics.CreateGauge("jellyfin_hot_cache_lease_oldest_age_seconds", "Age of the oldest active worker lease.");
    private static readonly Gauge WorkerHeartbeat = Metrics.CreateGauge("jellyfin_hot_cache_worker_heartbeat_unixtime", "Unix time of the most recent successful worker loop.");
    private static readonly Histogram Duration = Metrics.CreateHistogram("jellyfin_hot_cache_job_duration_seconds", "Hot-cache job duration.", new HistogramConfiguration { LabelNames = ["kind"] });

    public static void JobCompleted(HotCacheJob job, TimeSpan duration) { Jobs.WithLabels(job.Kind.ToString().ToLowerInvariant(), "completed").Inc(); Duration.WithLabels(job.Kind.ToString().ToLowerInvariant()).Observe(duration.TotalSeconds); }
    public static void JobFailed(HotCacheJob job, TimeSpan duration) { Jobs.WithLabels(job.Kind.ToString().ToLowerInvariant(), "failed").Inc(); Failures.WithLabels("worker").Inc(); Duration.WithLabels(job.Kind.ToString().ToLowerInvariant()).Observe(duration.TotalSeconds); }
    public static void BytesCopied(long bytes) => Bytes.Inc(bytes);
    public static void Evicted(string reason) => Evictions.WithLabels(reason).Inc();
    public static void Backend(bool up) => BackendUp.Set(up ? 1 : 0);
    public static void Storage(bool up, long totalBytes = 0, long availableBytes = 0)
    {
        StorageUp.Set(up ? 1 : 0);
        if (up)
        {
            StorageBytes.WithLabels("total").Set(totalBytes);
            StorageBytes.WithLabels("used").Set(Math.Max(0, totalBytes - availableBytes));
            StorageBytes.WithLabels("available").Set(availableBytes);
        }
    }
    public static void Database(long bytes, long eventRows, long normalFallbacks, long unhealthyFallbacks) { DatabaseBytes.Set(bytes); EventRows.Set(eventRows); Fallbacks.WithLabels("normal").Set(normalFallbacks); Fallbacks.WithLabels("unhealthy").Set(unhealthyFallbacks); }
    public static void Queue(HotCacheQueueSnapshot snapshot) { QueueDepth.Set(snapshot.Depth); QueueOldestAge.Set(snapshot.OldestAge.TotalSeconds); LeaseAge.Set(snapshot.OldestLeaseAge.TotalSeconds); WorkerHeartbeat.SetToCurrentTimeUtc(); }
}
