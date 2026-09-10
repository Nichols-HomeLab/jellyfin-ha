# Jellyfin 12 PostgreSQL upgrade

This port merges the official server `v12.0` release and builds the matching
Jellyfin Web release at `0e83c6a724b31f3e9b5a499244331a288c060a4a`.
The server, in-tree PostgreSQL provider, and hot-cache worker use .NET 10.
The server uses EF Core 10 and Npgsql 10. The legacy PostgreSQL plugin submodule
is not loaded by the supported container builds.

## Database changes

The existing `20260305010333_InitialPostgreSql` migration remains intact.
A new migration updates existing catalogs to the v12 relational model,
including linked children, UUID owner/version references, user constraints,
and the revised query indexes. Startup advances the PostgreSQL schema before
running pending upstream data migrations: several of those routines have dates
older than this fork’s consolidated initial migration and require the new schema.
SQLite retains upstream’s interleaved schema/data migration ordering.

Username normalization uses .NET invariant casing before the unique index is
created. Colliding usernames stop the upgrade with an error; rename the
conflicting accounts on the old server before retrying. Do not delete users to
work around a failed migration.

## Upgrade an existing installation

1. Verify a restorable PostgreSQL backup and a copy of shared configuration,
   metadata, and plugins. The PostgreSQL provider’s automatic backup methods
   do not create or restore database backups; the migration framework’s backup
   messages are not evidence of a PostgreSQL backup.
2. Rehearse with an isolated restored copy of the database and configuration,
   using the exact new server and worker images intended for deployment.
3. Stop old server replicas and the hot-cache worker before starting the new
   server. Do not run v10 and v12 against the same database or shared config.
4. Start one v12 server and let startup migrations finish. Review migration logs
   before starting any additional replicas or the rebuilt hot-cache worker.
5. Verify users, watched/resume state, playlists and collections, alternate
   versions, library queries, direct play, and transcoding. Install only plugins
   compatible with v12, then run a full library scan as upstream requests.

A rollback after schema/data migration restores the old database **and** its
matching configuration/metadata backup with the old images. Replacing only the
image is not a rollback procedure.

This source upgrade does not change the live Flux deployment or migrate the
production database. A deployment must pin the validated image revisions in
`Nichols-HomeLab/k3s-fluxcd` and be reconciled by Flux.

## Port validation

Validated in the isolated upgrade worktree with SDK 10.0.401:

- Full solution Release build and self-contained Linux x64 publish; the executable
  reports `Jellyfin.Server 12.0.0.0` and includes the PostgreSQL provider.
- PostgreSQL 18 provider suite: 13 passed, including concurrency and ownership.
- Real server migration process: fresh and populated legacy databases, followed
  by a second startup in each case; 2 passed. Users, playlist/collection links,
  favorites, play counts, and resume positions were preserved.
- Server implementation suite with real Redis: 922 passed; 4 Windows-only cases
  skipped on Linux. Includes SQLite snapshot parity and new catalog write guards.
- Metadata provider suite: 453 passed; hot-cache worker non-database suite:
  38 passed. General API, controller, networking, naming, encoding, drawing,
  model, extension, and metadata tests passed.
- HTTP integration: 123 distinct tests passed across the suite run and focused
  dashboard rerun; 3 integration tests skipped. Dashboard tests cover the fork’s
  authenticated configuration pages, hot-cache menu, and anonymous-access rejection.
- Core startup/network tests: 14 passed. Hostname-resolution expectations use
  the actual host DNS records; literal IPv4/IPv6 checks remain explicit.
- Matching Jellyfin Web v12.0 production build completed successfully.

The complete container build and hardware QSV playback are separate runtime
checks; source tests and publish validation do not establish production playback
or backup readiness. CI now runs the PostgreSQL provider and real server upgrade
regressions against disposable databases on main pushes.

## References

- [Official Jellyfin 12.0 announcement](https://jellyfin.org/posts/jellyfin-release-12.0/)
- [Server v12.0 source](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)
- [Web v12.0 source](https://github.com/jellyfin/jellyfin-web/releases/tag/v12.0)
