using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AudioBook = MediaBrowser.Controller.Entities.AudioBook;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class UserDataManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly UserDataManager _userDataManager;
    private readonly User _user;
    private readonly TestInvalidator _invalidator = new();

    public UserDataManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);

        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        _userDataManager = new UserDataManager(config.Object, factory.Object, _invalidator);
        _user = new User("user", "auth-provider", "reset-provider")
        {
            Id = Guid.NewGuid()
        };
    }

    public void Dispose()
    {
        _userDataManager.Dispose();
        _connection.Dispose();
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private AudioBook CreateAudioBook()
    {
        // GetUserDataKeys(): ["Author-Series-0001Book Title", "<item id N>"]
        return new AudioBook
        {
            Id = Guid.NewGuid(),
            Name = "Book Title",
            Album = "Series",
            AlbumArtists = new[] { "Author" },
            IndexNumber = 1
        };
    }

    private UserData CreateUserDataRow(AudioBook item, string key, long positionTicks)
    {
        return new UserData
        {
            ItemId = item.Id,
            Item = null,
            UserId = _user.Id,
            User = null,
            CustomDataKey = key,
            PlaybackPositionTicks = positionTicks
        };
    }

    [Fact]
    public void GetUserData_RowsUnderCurrentAndRetiredKeys_PrefersCurrentKeyRow()
    {
        var item = CreateAudioBook();
        var currentKey = item.GetUserDataKeys()[0];

        // the retired-key row comes first to ensure selection is by key, not row order
        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111),
            CreateUserDataRow(item, currentKey, 222)
        };

        PersistNavigationRows(item);
        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(currentKey, userData.Key);
        Assert.Equal(222, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_NoPrimaryKeyRow_UsesNextCurrentKeyRow()
    {
        var item = CreateAudioBook();
        var idKey = item.GetUserDataKeys()[1];

        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111),
            CreateUserDataRow(item, idKey, 333)
        };

        PersistNavigationRows(item);
        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(idKey, userData.Key);
        Assert.Equal(333, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_OnlyRetiredKeyRows_ReturnsRetiredKeyRow()
    {
        var item = CreateAudioBook();

        item.UserData = new List<UserData>
        {
            CreateUserDataRow(item, "Author-Old Album-0001Old File Name", 111)
        };

        PersistNavigationRows(item);
        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(111, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_NoRows_ReturnsDefaultWithPrimaryKey()
    {
        var item = CreateAudioBook();
        item.UserData = new List<UserData>();

        PersistNavigationRows(item);
        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(item.GetUserDataKeys()[0], userData.Key);
        Assert.Equal(0, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_RowsForOtherUsers_AreIgnored()
    {
        var item = CreateAudioBook();
        var currentKey = item.GetUserDataKeys()[0];

        var otherUserRow = CreateUserDataRow(item, currentKey, 999);
        otherUserRow.UserId = Guid.NewGuid();

        item.UserData = new List<UserData>
        {
            otherUserRow,
            CreateUserDataRow(item, currentKey, 222)
        };

        PersistNavigationRows(item);
        var userData = _userDataManager.GetUserData(_user, item);

        Assert.NotNull(userData);
        Assert.Equal(222, userData.PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserDataBatch_DatabaseFallback_ResolvesRowsByKeyOrder()
    {
        // no preloaded navigation data, so the batch takes the database fallback
        var fossilItem = CreateAudioBook();
        var retiredItem = CreateAudioBook();

        using (var ctx = CreateDbContext())
        {
            ctx.Users.Add(_user);
            ctx.BaseItems.Add(new BaseItemEntity { Id = fossilItem.Id, Type = typeof(AudioBook).FullName! });
            ctx.BaseItems.Add(new BaseItemEntity { Id = retiredItem.Id, Type = typeof(AudioBook).FullName! });

            // the stale id-key row is inserted first so selection by row order would return it
            ctx.UserData.AddRange(
                CreateUserDataRow(fossilItem, fossilItem.GetUserDataKeys()[1], 111),
                CreateUserDataRow(fossilItem, fossilItem.GetUserDataKeys()[0], 222),
                CreateUserDataRow(retiredItem, "Author-Old Album-0001Old File Name", 333));
            ctx.SaveChanges();
        }

        var result = _userDataManager.GetUserDataBatch([fossilItem, retiredItem], _user);

        Assert.Equal(222, result[fossilItem.Id].PlaybackPositionTicks);
        Assert.Equal(333, result[retiredItem.Id].PlaybackPositionTicks);
    }

    private void PersistNavigationRows(AudioBook item)
    {
        using var context = CreateDbContext();
        context.Users.Add(_user);
        context.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = typeof(AudioBook).FullName! });
        foreach (var row in item.UserData!)
        {
            if (!row.UserId.Equals(_user.Id))
            {
                context.Users.Add(new User(row.UserId.ToString(), "auth-provider", "reset-provider") { Id = row.UserId });
            }

            context.UserData.Add(row);
        }

        context.SaveChanges();
    }

    [Fact]
    public void GetUserData_RemoteInvalidationReloadsDatabaseDespiteStaleNavigationRows()
    {
        var item = CreateAudioBook();
        var currentKey = item.GetUserDataKeys()[0];
        item.UserData = [CreateUserDataRow(item, currentKey, 111)];
        PersistNavigationRows(item);
        Assert.Equal(111, _userDataManager.GetUserData(_user, item).PlaybackPositionTicks);

        using (var context = CreateDbContext())
        {
            context.UserData.Where(row => row.ItemId.Equals(item.Id))
                .ExecuteUpdate(update => update.SetProperty(row => row.PlaybackPositionTicks, 222));
        }

        // The loaded item and the manager cache still contain the old playback state.
        Assert.Equal(111, _userDataManager.GetUserData(_user, item).PlaybackPositionTicks);
        _invalidator.Receive(_user.InternalId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + item.Id.ToString("N"));

        Assert.Equal(222, _userDataManager.GetUserData(_user, item).PlaybackPositionTicks);
        Assert.Equal(111, Assert.Single(item.UserData!).PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserDataBatch_VersionsWithIdenticalMetadataKeysKeepSeparatePlaybackState()
    {
        var first = CreateAudioBook();
        var second = CreateAudioBook();
        Assert.Equal(first.GetUserDataKeys()[0], second.GetUserDataKeys()[0]);
        using (var context = CreateDbContext())
        {
            context.Users.Add(_user);
            context.BaseItems.AddRange(
                new BaseItemEntity { Id = first.Id, Type = typeof(AudioBook).FullName! },
                new BaseItemEntity { Id = second.Id, Type = typeof(AudioBook).FullName! });
            context.UserData.AddRange(
                CreateUserDataRow(first, first.GetUserDataKeys()[0], 111),
                CreateUserDataRow(second, second.GetUserDataKeys()[0], 222));
            context.SaveChanges();
        }

        var result = _userDataManager.GetUserDataBatch([first, second], _user);

        Assert.Equal(111, result[first.Id].PlaybackPositionTicks);
        Assert.Equal(222, result[second.Id].PlaybackPositionTicks);
        Assert.Equal(111, _userDataManager.GetUserData(_user, first).PlaybackPositionTicks);
        Assert.Equal(222, _userDataManager.GetUserData(_user, second).PlaybackPositionTicks);
    }

    [Fact]
    public void GetUserData_NullUser_ThrowsArgumentNullException()
    {
        var item = CreateAudioBook();
        Assert.Throws<ArgumentNullException>(() => _userDataManager.GetUserData(null!, item));
    }

    private sealed class TestInvalidator : IUserDataCacheInvalidator
    {
        public event Action<string>? Invalidated;

        public void Publish(string cacheKey)
        {
        }

        public void Receive(string cacheKey) => Invalidated?.Invoke(cacheKey);
    }
}
