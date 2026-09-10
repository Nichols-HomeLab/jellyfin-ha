The PostgreSQL integration suite uses PostgreSQL 18 through Testcontainers by default.

To run against a disposable PostgreSQL server without Docker, set
`JELLYFIN_TEST_POSTGRES_CONNECTION_STRING` to an administrative connection string.
Every test creates a uniquely named `jellyfin_test_<uuid>` database and drops only
that database afterward. The supplied database is never used for test fixtures.
The account needs permission to create and drop databases; ownership failover tests
also terminate their own coordination connections.

```bash
dotnet test tests/Jellyfin.Database.Tests.PostgreSQL -c Release
```

The migration tests cover a fresh database, the original PostgreSQL migration
populated with synthetic legacy data, invariant username normalization and
collision rejection, model snapshot parity, duplicate playlist entries, and the
UUID aggregates used by Jellyfin 12's grouped queries.

The server must call `PostgreSqlDatabaseProvider.PrepareSchemaUpgradeAsync` before
applying pending schema migrations to an existing database. This backfills
normalized usernames with .NET invariant casing before the unique index is
created. The v12 migration intentionally refuses a destructive downgrade; restore
a pre-upgrade database backup when reverting server versions.
