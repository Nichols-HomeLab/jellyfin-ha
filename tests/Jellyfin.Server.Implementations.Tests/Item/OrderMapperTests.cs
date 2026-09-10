using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class OrderMapperTests : SqliteDbTestFixture
{
    [Fact]
    public void DatePlayedTreatsMissingUserDataAsOldest()
    {
        var user = new User("tester", "authentication", "password-reset");
        var playedAt = DateTime.UtcNow.AddDays(-1);
        var withoutPlayback = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", UserData = [] };
        var withPlayback = new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            Type = "Test",
            UserData =
            [
                new UserData
                {
                    CustomDataKey = "played",
                    ItemId = Guid.NewGuid(),
                    Item = null,
                    UserId = user.Id,
                    User = user,
                    LastPlayedDate = playedAt
                }
            ]
        };
        withPlayback.UserData!.Single().ItemId = withPlayback.Id;
        using var context = CreateDbContext();
        context.Users.Add(user);
        context.BaseItems.AddRange(withoutPlayback, withPlayback);
        context.SaveChanges();
        var orderExpression = OrderMapper.MapOrderByField(ItemSortBy.DatePlayed, new InternalItemsQuery(user), context);
        var itemIds = new[] { withoutPlayback.Id, withPlayback.Id };
        var ordered = context.BaseItems.Where(item => itemIds.Contains(item.Id))
            .OrderByDescending(orderExpression).Select(item => item.Id).ToArray();

        Assert.Equal([withPlayback.Id, withoutPlayback.Id], ordered);
    }

    [Fact]
    public void ShouldReturnMappedOrderForSortingByPremierDate()
    {
        var orderFunc = OrderMapper.MapOrderByField(ItemSortBy.PremiereDate, new InternalItemsQuery(), null!).Compile();

        var expectedDate = new DateTime(1, 2, 3);
        var expectedProductionYearDate = new DateTime(4, 1, 1);

        var entityWithOnlyProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", ProductionYear = expectedProductionYearDate.Year };
        var entityWithOnlyPremierDate = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", PremiereDate = expectedDate };
        var entityWithBothPremierDateAndProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test", PremiereDate = expectedDate, ProductionYear = expectedProductionYearDate.Year };
        var entityWithoutEitherPremierDateOrProductionYear = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Test" };

        var resultWithOnlyProductionYear = orderFunc(entityWithOnlyProductionYear);
        var resultWithOnlyPremierDate = orderFunc(entityWithOnlyPremierDate);
        var resultWithBothPremierDateAndProductionYear = orderFunc(entityWithBothPremierDateAndProductionYear);
        var resultWithoutEitherPremierDateOrProductionYear = orderFunc(entityWithoutEitherPremierDateOrProductionYear);

        Assert.Equal(resultWithOnlyProductionYear, expectedProductionYearDate);
        Assert.Equal(resultWithOnlyPremierDate, expectedDate);
        Assert.Equal(resultWithBothPremierDateAndProductionYear, expectedDate);
        Assert.Null(resultWithoutEitherPremierDateOrProductionYear);
    }
}
