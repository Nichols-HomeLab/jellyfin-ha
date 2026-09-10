using System;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class OrderMapperTests
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
        var orderFunc = OrderMapper.MapOrderByField(ItemSortBy.DatePlayed, new InternalItemsQuery(user), null!).Compile();

        Assert.Equal(DateTime.MinValue, orderFunc(withoutPlayback));
        Assert.Equal(playedAt, orderFunc(withPlayback));
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
