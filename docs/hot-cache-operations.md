# Hot-cache operations

The worker exposes Prometheus metrics on `:9109/metrics` (override with
`Jellyfin__HotCache__MetricsPort`). Metric labels are bounded: job kinds,
outcomes, components, and eviction reasons only. Paths, credentials, display
names, and Jellyfin item identifiers are deliberately excluded from metrics.

## Signals and alerts

| Alert | PromQL test signal | Runbook |
| --- | --- | --- |
| HotCacheBackendUnavailable | `jellyfin_hot_cache_backend_up == 0` for 5m | Check PostgreSQL connectivity and worker logs; Jellyfin stays ready and uses canonical media. |
| HotCacheWorkerHeartbeatStale | `time() - jellyfin_hot_cache_worker_heartbeat_unixtime > 120` for 5m | Check worker process/pod and restart it after collecting logs. |
| HotCacheSustainedFailures | `increase(jellyfin_hot_cache_failures_total[15m]) > 5` for 10m | Inspect structured `JobId` logs, source mount, capacity, and lease expiry. |
| HotCacheJobsStuck | `jellyfin_hot_cache_queue_oldest_age_seconds > 900` for 10m | Identify leased job, allow the lease to expire, then investigate the owning worker. |
| HotCacheStorageUnavailable | `jellyfin_hot_cache_storage_up == 0` for 5m | Check the hot-cache mount; canonical playback remains safe. |
| HotCacheStorageNearCapacity | `jellyfin_hot_cache_storage_bytes{state="used"} / jellyfin_hot_cache_storage_bytes{state="total"} > .95` for 10m | Confirm eviction progresses and free capacity before changing watermarks. |
| HotCacheDatabaseGrowth | `jellyfin_hot_cache_database_bytes > 1073741824` for 30m | Inspect retained event volume and PostgreSQL storage; the worker removes events older than 30 days automatically. |
| HotCacheUnexpectedColdFallback | `jellyfin_hot_cache_fallbacks_recent{class="unhealthy"} > 5` for 10m | Inspect the safe `Reason`/`Purpose` logs, source mount, and cache validation failures. Normal `hot-miss` fallbacks are reported separately and do not alert. |

Queue depth, oldest queue age, oldest lease age, copied bytes, job duration,
evictions, failures, storage bytes, database bytes, and normal versus unhealthy
cold fallbacks provide capacity and database-growth visibility. The worker
automatically retains `hot_cache_events` for 30 days; schedule `VACUUM (ANALYZE)
hot_cache_events` during a low-traffic window if PostgreSQL reports reclaimable
space.

## Failure injection

1. Block the worker from PostgreSQL: backend-up must become zero while a
   Jellyfin readiness probe remains successful and playback resolves cold.
2. Interrupt PostgreSQL: confirm the heartbeat stops advancing and no job is
   marked completed without an owned lease.
3. Kill the worker during a copy: the partial file is removed; after expiry a
   second worker can claim the job.
4. Modify the source during copy: no hot file is published and the job records
   a bounded failure.
5. Corrupt a hot file: resolver validation must select canonical media and
   record a cold fallback rather than returning an unreadable path.
