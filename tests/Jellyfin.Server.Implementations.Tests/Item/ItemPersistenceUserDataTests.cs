using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class ItemPersistenceUserDataTests : SqliteDbTestFixture
{
    [Fact]
    public async Task DeleteThenReattach_PreservesExistingPlaceholderAndTargetWatchState()
    {
        var itemId = Guid.NewGuid();
        var replacement = new Book { Id = Guid.NewGuid(), Name = "Replacement" };
        var key = replacement.GetUserDataKeys().First();
        using (var context = CreateDbContext())
        {
            var user = new User("watch-state", "auth", "reset");
            context.Users.Add(user);
            context.BaseItems.AddRange(
                new BaseItemEntity { Id = itemId, Type = typeof(Book).FullName! },
                new BaseItemEntity { Id = replacement.Id, Type = typeof(Book).FullName! });
            context.UserData.AddRange(
                new UserData { ItemId = itemId, Item = null, UserId = user.Id, User = user, CustomDataKey = key, PlayCount = 2 },
                new UserData { ItemId = BaseItemRepository.PlaceholderId, Item = null, UserId = user.Id, User = user, CustomDataKey = key, Played = true, IsFavorite = true, PlayCount = 5 },
                new UserData { ItemId = replacement.Id, Item = null, UserId = user.Id, User = user, CustomDataKey = key, PlaybackPositionTicks = 42, LastPlayedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });
            await context.SaveChangesAsync();
        }

        var service = new ItemPersistenceService(CreateDbContextFactory(), new Mock<IServerApplicationHost>().Object, NullLogger<ItemPersistenceService>.Instance);
        service.DeleteItem(itemId);
        await service.ReattachUserDataAsync(replacement, CancellationToken.None);

        using var resultContext = CreateDbContext();
        var result = Assert.Single(resultContext.UserData);
        Assert.Equal(replacement.Id, result.ItemId);
        Assert.True(result.Played);
        Assert.True(result.IsFavorite);
        Assert.Equal(5, result.PlayCount);
        Assert.Equal(42, result.PlaybackPositionTicks);
        Assert.Null(result.RetentionDate);
        Assert.DoesNotContain(resultContext.BaseItems, item => item.Id.Equals(itemId));
    }
}
