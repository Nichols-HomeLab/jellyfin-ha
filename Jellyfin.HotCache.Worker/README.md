# Jellyfin hot-cache worker

This independently deployable process owns large tiering I/O. At startup it applies its embedded, idempotent `sql/001_hot_cache.sql` migration to the coordinator PostgreSQL database before polling. The worker owns only `hot_cache_jobs` and append-only `hot_cache_events`; coordinator code may create jobs and read state, but never performs cache I/O. The row lease is the sole authority to progress, publish, retry, or complete a job.

Required environment variables are `ConnectionStrings__HotCache`, `Jellyfin__HotCache__CanonicalRoot`, and `Jellyfin__HotCache__HotRoot`. Optional high/low watermarks default to 90%/75%. The worker never makes Jellyfin unavailable: failed promotions remain cold-playback misses.
