using System;
using System.Net;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class RedisCatalogChangeNotifierTests
{
    [Fact]
    public void WireMessage_DeserializesCatalogChange()
    {
        var itemId = Guid.NewGuid();
        var message = $"external|1|1|{itemId:N}|00000000000000000000000000000000";

        var parsed = RedisCatalogChangeNotifier.TryDeserialize(message, out var source, out var change);

        Assert.True(parsed);
        Assert.Equal("external", source);
        Assert.Equal(itemId, change.ItemId);
    }

    [Fact]
    public void SameMultiplexerConnectionRestore_RaisesRecoverySignal()
    {
        var connection = new Mock<IConnectionMultiplexer>();
        using var manager = new RedisConnectionManager(
            () => connection.Object,
            NullLogger<RedisConnectionManager>.Instance);
        IConnectionMultiplexer? restored = null;
        manager.ConnectionRestored += value => restored = value;

        connection.Raise(
            c => c.ConnectionRestored += null,
            new ConnectionFailedEventArgs(
                connection.Object,
                new DnsEndPoint("redis", 6379),
                ConnectionType.Interactive,
                ConnectionFailureType.None,
                new InvalidOperationException("test restore"),
                "test restore"));

        Assert.Same(connection.Object, restored);
    }
}
