namespace MediaBrowser.Controller.Library;

/// <summary>Versioned PostgreSQL schema shared by the server coordinator and worker.</summary>
public static class HotCacheSchema
{
    /// <summary>The current idempotent schema migration.</summary>
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS hot_cache_schema_migrations (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS hot_cache_jobs (id uuid PRIMARY KEY, item_id uuid, kind text NOT NULL CHECK (kind IN ('promotion','eviction')), state text NOT NULL DEFAULT 'pending' CHECK (state IN ('pending','running','completed','failed')), canonical_path text NOT NULL, hot_path text, source_length bigint NOT NULL DEFAULT 0, source_modified_utc timestamptz NOT NULL DEFAULT now(), priority integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT false, is_pinned boolean NOT NULL DEFAULT false, is_copying boolean NOT NULL DEFAULT false, last_access_utc timestamptz NOT NULL DEFAULT now(), bytes_copied bigint NOT NULL DEFAULT 0, attempts integer NOT NULL DEFAULT 0, max_attempts integer NOT NULL DEFAULT 3, last_error varchar(512), lease_owner text, lease_expires_at timestamptz, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
        ALTER TABLE hot_cache_jobs ADD COLUMN IF NOT EXISTS item_id uuid;
        CREATE UNIQUE INDEX IF NOT EXISTS hot_cache_jobs_item_unique_idx ON hot_cache_jobs(item_id) WHERE item_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS hot_cache_jobs_claim_idx ON hot_cache_jobs (state, priority DESC, created_at) WHERE state IN ('pending', 'running');
        CREATE TABLE IF NOT EXISTS hot_cache_events (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, job_id uuid NOT NULL REFERENCES hot_cache_jobs(id), kind text NOT NULL, detail varchar(512) NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS hot_cache_interests (item_id uuid NOT NULL, user_id uuid NOT NULL, reason text NOT NULL, priority integer NOT NULL, first_observed_utc timestamptz NOT NULL DEFAULT now(), last_observed_utc timestamptz NOT NULL DEFAULT now(), expires_at_utc timestamptz NOT NULL, PRIMARY KEY(item_id,user_id,reason));
        CREATE TABLE IF NOT EXISTS hot_cache_playback_leases (play_session_id text PRIMARY KEY, item_id uuid NOT NULL, expires_at_utc timestamptz NOT NULL, updated_at_utc timestamptz NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS hot_cache_playback_leases_item_expiry_idx ON hot_cache_playback_leases(item_id,expires_at_utc);
        INSERT INTO hot_cache_schema_migrations(version) VALUES (1) ON CONFLICT DO NOTHING;
        """;
}
