using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Tests;

public sealed class StartupMigrationRetryTests
{
    [Fact]
    public async Task TransientPostgresFailure_RetriesAndCompletesMigration()
    {
        var attempts = 0;
        var delays = 0;

        await Program.RetryStartupMigrationAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new NpgsqlException("read timed out", new TimeoutException()))
                    : Task.CompletedTask;
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task TimedOutAttempt_RetriesAndCompletesMigration()
    {
        var attempts = 0;
        var delays = 0;

        await Program.RetryStartupMigrationAsync(
            token =>
            {
                attempts++;
                return attempts == 1
                    ? Task.Delay(Timeout.InfiniteTimeSpan, token)
                    : Task.CompletedTask;
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, attempts);
        Assert.Equal(1, delays);
    }
}
