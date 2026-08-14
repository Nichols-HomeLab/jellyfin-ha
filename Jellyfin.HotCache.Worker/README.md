# Jellyfin hot-cache worker

This independently deployable process owns large tiering I/O. Apply `sql/001_hot_cache.sql` to the coordinator PostgreSQL database before starting it. It uses the coordinator's `hot_cache_jobs` and append-only `hot_cache_events` state: the row lease is the sole authority to progress, publish, retry, or complete a job.

Required environment variables are `ConnectionStrings__HotCache`, `Jellyfin__HotCache__CanonicalRoot`, and `Jellyfin__HotCache__HotRoot`. Optional high/low watermarks default to 90%/75%. The worker never makes Jellyfin unavailable: failed promotions remain cold-playback misses.
