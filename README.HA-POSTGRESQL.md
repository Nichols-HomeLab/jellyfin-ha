# Jellyfin HA with PostgreSQL

This fork combines these components on the Jellyfin 12.0 release:

- the Redis-backed transcode coordination code from `ZoltyMat/jellyfin-ha`;
- the PostgreSQL query optimizations from `Nichols-HomeLab/jellyfin`;
- the in-tree PostgreSQL database provider, ported to the Jellyfin 12 schema and Entity Framework Core 10;
- catalog writer election and replica cache propagation;
- the independently deployed PostgreSQL-backed hot-cache worker.

The PostgreSQL provider remains experimental. Back up the Jellyfin config and
database before migrating an existing server.

## Build

Build the server with the .NET 10 SDK:

```bash
dotnet publish Jellyfin.Server/Jellyfin.Server.csproj --configuration Release
```

The supported container build uses `Dockerfile`; `Dockerfile.pgsql` remains a
compatibility entry point for the same in-tree provider image. Both bundle the
matching Jellyfin Web v12.0 release. The production workflow publishes the server
and hot-cache worker separately to `git.nicholstech.org/nichols-homelab`.

```bash
docker build -t jellyfin-ha-pgsql:12.0 .
docker build -f Jellyfin.HotCache.Worker/Dockerfile -t jellyfin-hot-cache-worker:12.0 .
```

The legacy `plugins/Jellyfin.Pgsql` submodule is not required by these builds.

## Required configuration

Configure both PostgreSQL and Redis:

```yaml
services:
  jellyfin:
    image: jellyfin-ha-pgsql:12.0
    environment:
      POSTGRES_CONNECTION_STRING: >-
        Host=postgres;Port=5432;Database=jellyfin;Username=jellyfin;Password=change-me
      Jellyfin__TranscodeStore__RedisConnectionString: redis:6379,abortConnect=false
      Jellyfin__TranscodeStore__LeaseDurationSeconds: "30"
      Jellyfin__TranscodeStore__RecoveryRetentionSeconds: "300"
      JELLYFIN_Jellyfin__CatalogOwnership__ProbeIntervalSeconds: "2"
      JELLYFIN_INSTANCE_ID: jellyfin-1
    volumes:
      - jellyfin-config:/config
      - jellyfin-cache:/cache
      - jellyfin-transcode:/transcode
      - /path/to/media:/media:ro
```

`POSTGRES_CONNECTION_STRING` takes precedence over the connection string in
`database.xml`. The in-tree provider requires a complete Npgsql connection
string; the legacy plugin’s individual `POSTGRES_*` variables are not used.

For multiple replicas, every instance needs:

- the same PostgreSQL database;
- the same Redis deployment;
- a unique `JELLYFIN_INSTANCE_ID` (a pod or task name is suitable);
- shared read-write `/config` and `/transcode` storage;
- the same read-only media paths on every node.

Use a load balancer with session affinity for normal playback traffic. Redis
leases coordinate recovery; they are not a replacement for stable request
routing while the owning instance is healthy.

PostgreSQL-backed replicas also elect exactly one catalog writer with a
session-level advisory lock. Only that replica runs scheduled tasks or dispatches
filesystem-monitor refreshes; every replica continues serving HTTP and playback.
The owner health-checks its lock session at the configured interval (between
0.05 and 30 seconds), releases it during graceful shutdown, and cancels running
scheduled work if PostgreSQL coordination is lost. Until coordination succeeds,
the server fails closed for catalog work. Ownership acquisition and loss are
logged with `JELLYFIN_INSTANCE_ID`. Normal SQLite deployments retain their
single-instance behavior and require no additional configuration.

## First start

Use an empty PostgreSQL database and an empty Jellyfin config directory for the
first validation start. The PostgreSQL provider is included in the server. Configure the database
provider as `Jellyfin-PostgreSQL` in `database.xml` and supply the connection
string through `POSTGRES_CONNECTION_STRING`.

For an existing PostgreSQL installation, follow [the v12 upgrade notes](docs/jellyfin-v12-upgrade.md).
The legacy plugin’s SQLite conversion instructions target its older schema;
they are not a v12 migration procedure.

## Validation

The server build and HA-focused tests can be run without installing the .NET
SDK on the host:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet build Jellyfin.Server/Jellyfin.Server.csproj --configuration Release
```

Build the final image with `Dockerfile` to validate the in-tree provider,
server, matching web client, and QSV runtime together.
