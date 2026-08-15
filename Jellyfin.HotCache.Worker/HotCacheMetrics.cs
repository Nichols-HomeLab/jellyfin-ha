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
    public static void Queue(HotCacheQueueSnapshot snapshot) { QueueDepth.Set(snapshot.Depth); QueueOldestAge.Set(snapshot.OldestAge.TotalSeconds); LeaseAge.Set(snapshot.OldestLeaseAge.TotalSeconds); WorkerHeartbeat.SetToCurrentTimeUtc(); }
}
