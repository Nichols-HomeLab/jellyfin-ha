using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Database.Tests.PostgreSQL;

/// <summary>
/// Integration tests that validate PostgreSQL migrations against a real container.
/// </summary>
[Xunit.Trait("Category", "RequiresDocker")]
public sealed class PostgreSqlMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _container = new();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Verifies that the <c>InitialPostgreSql</c> migration applies cleanly to a fresh PostgreSQL 16 container.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    [Fact]
    public async Task MigrateAsync_AppliesInitialMigrationCleanly()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        var context = CreateContext(dataSource);
        await using (context)
        {
            await context.Database.MigrateAsync();

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            Assert.Empty(pendingMigrations);
        }
    }

    /// <summary>
    /// Verifies that no pending model changes exist for the PostgreSQL provider,
    /// acting as a CI gate that fails when model changes are added without a corresponding migration.
    /// </summary>
    [Fact]
    public void CheckForUnappliedMigrations_PostgreSql()
    {
        // Use a dummy connection string; HasPendingModelChanges() is a purely in-memory check
        // that compares the current compiled model with the migration snapshots — no real DB needed.
        const string dummyConnectionString = "Host=localhost;Database=jellyfin;Username=postgres;Password=postgres";
        using var dataSource = new NpgsqlDataSourceBuilder(dummyConnectionString).Build();
        using var context = CreateContext(dataSource);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "There are unapplied changes to the EFCore model for PostgreSQL. Please create a Migration.");
    }

    /// <summary>
    /// Upgrades the actual pre-v12 baseline with malformed identifiers and orphan rows.
    /// </summary>
    /// <returns>The asynchronous operation.</returns>
    [Fact]
    public async Task Upgrade_PreservesCatalogAndConvertsLegacyIdentifiers()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        await using var context = CreateContext(dataSource);
        await context.GetService<IMigrator>().MigrateAsync("20260305010333_InitialPostgreSql");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "BaseItems" ("Id", "Type", "IsMovie", "IsLocked", "IsSeries", "IsRepeat", "IsInMixedFolder", "IsFolder", "IsVirtualItem", "OwnerId", "PrimaryVersionId", "ExtraIds", "Data") VALUES
            ('10000000-0000-0000-0000-000000000001', 'Movie', true, false, false, false, false, false, false, NULL, NULL, 'legacy-extra-list', '{{"LinkedChildren":[]}}'),
            ('10000000-0000-0000-0000-000000000002', 'Video', false, false, false, false, false, false, false, '10000000000000000000000000000001', '10000000-0000-0000-0000-000000000001', NULL, NULL),
            ('10000000-0000-0000-0000-000000000003', 'Video', false, false, false, false, false, false, false, 'missing-owner', 'not-a-guid', NULL, NULL),
            ('10000000-0000-0000-0000-000000000004', 'Video', false, false, false, false, false, false, false, '20000000-0000-0000-0000-000000000001', NULL, NULL, NULL);
            INSERT INTO "Users" ("Id", "Username", "MustUpdatePassword", "AuthenticationProviderId", "PasswordResetProviderId", "InvalidLoginAttemptCount", "MaxActiveSessions", "SubtitleMode", "PlayDefaultAudioTrack", "DisplayMissingEpisodes", "DisplayCollectionsView", "EnableLocalPassword", "HidePlayedInLatest", "RememberAudioSelections", "RememberSubtitleSelections", "EnableNextEpisodeAutoPlay", "EnableAutoLogin", "EnableUserPreferenceAccess", "InternalId", "SyncPlayAccess", "RowVersion") VALUES ('30000000-0000-0000-0000-000000000001', 'élise', false, 'auth', 'reset', 0, 0, 0, false, false, false, false, false, false, false, false, false, false, 0, 0, 0);
            INSERT INTO "Permissions" ("Kind", "Value", "UserId", "RowVersion") VALUES (0, true, NULL, 0);
            INSERT INTO "Preferences" ("Kind", "Value", "UserId", "RowVersion") VALUES (0, '', NULL, 0);
            """);

        await PostgreSqlDatabaseProvider.PrepareSchemaUpgradeAsync(context);
        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
        var items = await context.BaseItems.Where(item => item.Type == "Movie" || item.Type == "Video").OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(4, items.Count);
        Assert.Null(items[0].OriginalLanguage);
        Assert.Equal("{\"LinkedChildren\":[]}", items[0].Data);
        Assert.Equal(items[0].Id, items[1].OwnerId);
        Assert.Equal(items[0].Id, items[1].PrimaryVersionId);
        Assert.Null(items[2].OwnerId);
        Assert.Null(items[2].PrimaryVersionId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), items[3].OwnerId);
        Assert.Equal("ÉLISE", (await context.Users.SingleAsync()).NormalizedUsername);
        Assert.Empty(await context.Permissions.ToListAsync());
        Assert.Empty(await context.Preferences.ToListAsync());

        context.LinkedChildren.AddRange(
            new LinkedChildEntity { ParentId = items[0].Id, ChildId = items[1].Id, ChildType = LinkedChildType.Manual, SortOrder = 0 },
            new LinkedChildEntity { ParentId = items[0].Id, ChildId = items[1].Id, ChildType = LinkedChildType.Manual, SortOrder = 1 });
        await context.SaveChangesAsync();
        Assert.Equal(2, await context.LinkedChildren.CountAsync());

        // The new query generator prefers the primary item with a nullable UUID MIN.
        var minimum = await context.BaseItems.Where(item => item.Type == "Video")
            .GroupBy(item => item.Type)
            .Select(group => group.Where(item => !item.PrimaryVersionId.HasValue).Min(item => (Guid?)item.Id) ?? group.Min(item => item.Id))
            .SingleAsync();
        Assert.Equal(items[2].Id, minimum);
        await PostgreSqlDatabaseProvider.PrepareSchemaUpgradeAsync(context);
        await context.Database.MigrateAsync();
        Assert.Equal(2, await context.LinkedChildren.CountAsync());
    }

    /// <summary>
    /// Normalizes usernames invariantly and refuses collisions without mutating user data.
    /// </summary>
    /// <returns>The asynchronous operation.</returns>
    [Fact]
    public async Task UsernamePreflight_UsesInvariantNormalizationAndRejectsCollisions()
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(_container.GetConnectionString()).Build();
        await using var context = CreateContext(dataSource);
        await context.Database.MigrateAsync();
        context.Users.AddRange(new User("élise", "auth", "reset"), new User("tester", "auth", "reset"));
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"NormalizedUsername\" = \"Username\"");
        await PostgreSqlDatabaseProvider.PrepareSchemaUpgradeAsync(context);
        context.ChangeTracker.Clear();
        var users = await context.Users.ToListAsync();
        Assert.All(users, user => Assert.Equal(user.Username.ToUpperInvariant(), user.NormalizedUsername));

        await context.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"Username\" = 'TESTER' WHERE \"Username\" = 'élise'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlDatabaseProvider.PrepareSchemaUpgradeAsync(context));
        context.ChangeTracker.Clear();
        Assert.Equal("ÉLISE", (await context.Users.SingleAsync(user => user.Username == "TESTER")).NormalizedUsername);
    }

    private static JellyfinDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var provider = new PostgreSqlDatabaseProvider(dataSource);
        provider.Initialise(optionsBuilder, new DatabaseConfigurationOptions { DatabaseType = "PostgreSQL" });
        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
