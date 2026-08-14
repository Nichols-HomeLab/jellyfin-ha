-- Durable queue and audit trail for the independently deployed hot-cache worker.
CREATE TABLE IF NOT EXISTS hot_cache_jobs (
    id uuid PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('promotion', 'eviction')),
    state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'running', 'completed', 'failed')),
    canonical_path text NOT NULL,
    hot_path text,
    source_length bigint NOT NULL DEFAULT 0,
    source_modified_utc timestamptz NOT NULL DEFAULT now(),
    priority integer NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT false,
    is_pinned boolean NOT NULL DEFAULT false,
    is_copying boolean NOT NULL DEFAULT false,
    last_access_utc timestamptz NOT NULL DEFAULT now(),
    bytes_copied bigint NOT NULL DEFAULT 0,
    attempts integer NOT NULL DEFAULT 0,
    max_attempts integer NOT NULL DEFAULT 3,
    last_error varchar(512),
    lease_owner text,
    lease_expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS hot_cache_jobs_claim_idx ON hot_cache_jobs (state, priority DESC, created_at) WHERE state IN ('pending', 'running');
CREATE TABLE IF NOT EXISTS hot_cache_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id uuid NOT NULL REFERENCES hot_cache_jobs(id),
    kind text NOT NULL,
    detail varchar(512) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
