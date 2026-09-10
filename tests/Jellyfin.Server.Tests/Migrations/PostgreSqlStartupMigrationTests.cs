using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Server.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Runs the complete server migration startup against a disposable PostgreSQL database.
/// Set JELLYFIN_TEST_POSTGRES_CONNECTION_STRING and JELLYFIN_TEST_SERVER_DLL to enable.
/// </summary>
public sealed class PostgreSqlStartupMigrationTests
{
    private static readonly HashSet<string> LegacyMigrationNames =
    [
        "CreateNetworkConfiguration",
        "MigrateEncodingOptions",
        "MigrateMusicBrainzTimeout",
        "MigrateNetworkConfiguration",
        "RenameEnableGroupingIntoCollections",
        "UpdateNormalizedUsername",
        "AddDefaultCastReceivers",
        "AddDefaultPluginRepository",
        "CleanMusicArtist",
        "CreateUserLoggingConfigFile",
        "DisableTranscodingThrottling",
        "FixAudioData",
        "FixDates",
        "FixLibrarySubtitleDownloadLanguages",
        "FixPlaylistOwner",
        "MigrateActivityLogDb",
        "MigrateAuthenticationDb",
        "MigrateDisplayPreferencesDb",
        "MigrateKeyframeData",
        "MigrateLibraryDb",
        "MigrateLibraryDbCompatibilityCheck",
        "MigrateLibraryUserData",
        "MigrateRatingLevels",
        "MigrateUserDb",
        "MoveExtractedFiles",
        "MoveTrickplayFiles",
        "ReaddDefaultPluginRepository",
        "RefreshInternalDateModified",
        "RemoveDownloadImagesInAdvance",
        "RemoveDuplicateExtras",
        "RemoveDuplicatePlaylistChildren",
        "ReseedFolderFlag",
        "UpdateDefaultPluginRepository",
    ];

    public static bool IsConfigured => Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES_CONNECTION_STRING") is not null
        && Environment.GetEnvironmentVariable("JELLYFIN_TEST_SERVER_DLL") is not null;

    [Theory(Skip = "Set the disposable PostgreSQL connection and server assembly environment variables.", SkipUnless = nameof(IsConfigured))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullStartup_UpgradesAndIsIdempotent(bool populatedLegacyDatabase)
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES_CONNECTION_STRING")!;
        var databaseName = "jellyfin_startup_test_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(directory);
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE DATABASE " + databaseName, admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ToString();
        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            if (populatedLegacyDatabase)
            {
                await SeedLegacyDatabaseAsync(dataSource);
            }

            var configDirectory = Path.Combine(directory, "config");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(configDirectory, "database.xml"),
                "<DatabaseConfigurationOptions><DatabaseType>Jellyfin-PostgreSQL</DatabaseType><LockingBehavior>NoLock</LockingBehavior></DatabaseConfigurationOptions>");
            if (populatedLegacyDatabase)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(configDirectory, "system.xml"),
                    "<ServerConfiguration><IsStartupWizardCompleted>true</IsStartupWizardCompleted><EnableMetrics>false</EnableMetrics></ServerConfiguration>");
            }

            await RunServerAsync(directory, connectionString);
            string[] applied;
            await using (var context = CreateContext(dataSource))
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync());
                Assert.False(context.Database.HasPendingModelChanges());
                applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
                if (populatedLegacyDatabase)
                {
                    await AssertPreservedDataAsync(context);
                    Assert.Contains(applied, id => id.EndsWith("_MigrateLinkedChildren", StringComparison.Ordinal));
                }
            }

            await RunServerAsync(directory, connectionString);
            await using (var context = CreateContext(dataSource))
            {
                Assert.Equal(applied, (await context.Database.GetAppliedMigrationsAsync()).ToArray());
                if (populatedLegacyDatabase)
                {
                    await AssertPreservedDataAsync(context);
                }
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP DATABASE " + databaseName + " WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(directory, true);
        }
    }

    private static async Task RunServerAsync(string directory, string connectionString)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("JELLYFIN_TEST_DOTNET_HOST") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            Environment.GetEnvironmentVariable("JELLYFIN_TEST_SERVER_DLL")!, "--mode", "MigrateSystem", "--nowebclient", "--nonetchange",
            "--datadir", Path.Combine(directory, "data"), "--configdir", Path.Combine(directory, "config"),
            "--cachedir", Path.Combine(directory, "cache"), "--logdir", Path.Combine(directory, "logs")
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["POSTGRES_CONNECTION_STRING"] = connectionString;
        start.Environment["JELLYFIN_PublishedServerUrl"] = "http://127.0.0.1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            Assert.Fail("Migration startup timed out: " + await output + await errors);
        }

        var log = await output + await errors;
        Assert.True(process.ExitCode == 0, log);
        Assert.DoesNotContain("Error while starting server", log, StringComparison.Ordinal);
    }

    private static JellyfinDbContext CreateContext(NpgsqlDataSource dataSource)
    {
        var provider = new PostgreSqlDatabaseProvider(dataSource);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(options, new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-PostgreSQL" });
        return new JellyfinDbContext(options.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task AssertPreservedDataAsync(JellyfinDbContext context)
    {
        var user = await context.Users.SingleAsync();
        Assert.Equal("ÉLISE", user.NormalizedUsername);
        var watchState = await context.UserData.SingleAsync();
        Assert.Equal(7, watchState.PlayCount);
        Assert.True(watchState.Played);
        Assert.True(watchState.IsFavorite);
        Assert.Equal(1234, watchState.PlaybackPositionTicks);
        Assert.Equal(2, await context.LinkedChildren.CountAsync());
        Assert.Equal(2, await context.LinkedChildren.Select(link => link.ParentId).Distinct().CountAsync());
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), watchState.ItemId);
    }

    private static async Task SeedLegacyDatabaseAsync(NpgsqlDataSource dataSource)
    {
        await using var context = CreateContext(dataSource);
        await context.GetService<IMigrator>().MigrateAsync("20260305010333_InitialPostgreSql");
        var history = context.GetService<IHistoryRepository>();
        foreach (var migration in typeof(JellyfinMigrationService).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<JellyfinMigrationAttribute>())
            .Where(attribute => attribute is not null && LegacyMigrationNames.Contains(attribute.Name)))
        {
            var id = migration!.Order.ToString("yyyyMMddHHmmsss", CultureInfo.InvariantCulture) + "_" + migration.Name;
            await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.11.0")));
        }

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "BaseItems" ("Id", "Type", "IsMovie", "IsLocked", "IsSeries", "IsRepeat", "IsInMixedFolder", "IsFolder", "IsVirtualItem", "Data") VALUES
            ('10000000-0000-0000-0000-000000000001', 'MediaBrowser.Controller.Entities.Movies.Movie', true, false, false, false, false, false, false, NULL),
            ('10000000-0000-0000-0000-000000000002', 'MediaBrowser.Controller.Playlists.Playlist', false, false, false, false, false, true, false, '{{"LinkedChildren":[{{"ItemId":"10000000000000000000000000000001","Type":"Manual"}}]}}'),
            ('10000000-0000-0000-0000-000000000003', 'MediaBrowser.Controller.Entities.Movies.BoxSet', false, false, false, false, false, true, false, '{{"LinkedChildren":[{{"ItemId":"10000000000000000000000000000001","Type":"Manual"}}]}}');
            INSERT INTO "Users" ("Id", "Username", "MustUpdatePassword", "AuthenticationProviderId", "PasswordResetProviderId", "InvalidLoginAttemptCount", "MaxActiveSessions", "SubtitleMode", "PlayDefaultAudioTrack", "DisplayMissingEpisodes", "DisplayCollectionsView", "EnableLocalPassword", "HidePlayedInLatest", "RememberAudioSelections", "RememberSubtitleSelections", "EnableNextEpisodeAutoPlay", "EnableAutoLogin", "EnableUserPreferenceAccess", "InternalId", "SyncPlayAccess", "RowVersion") VALUES ('30000000-0000-0000-0000-000000000001', 'élise', false, 'auth', 'reset', 0, 0, 0, false, false, false, false, false, false, false, false, false, false, 0, 0, 0);
            INSERT INTO "UserData" ("ItemId", "UserId", "CustomDataKey", "PlaybackPositionTicks", "PlayCount", "IsFavorite", "Played") VALUES ('10000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001', 'movie-watch-state', 1234, 7, true, true);
            """);
    }
}
