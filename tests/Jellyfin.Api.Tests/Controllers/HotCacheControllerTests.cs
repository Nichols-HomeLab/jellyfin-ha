using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public sealed class HotCacheControllerTests
{
    [Fact]
    public async Task UpdateSettings_InvalidWatermarks_ReturnsBadRequest()
    {
        var controller = new HotCacheController(new Store { RejectSettings = true });
        var result = await controller.UpdateSettings(new HotCacheSettings("unraid-temp", false, .5, .7), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Action_BulkEvictionWithoutConfirmation_ReturnsBadRequest()
    {
        var controller = new HotCacheController(new Store { RejectAction = true });
        var result = await controller.Action(new HotCacheAction("evict", null, false), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Action_UsesInventoryIdRatherThanPath()
    {
        var store = new Store();
        var controller = new HotCacheController(store);
        var id = Guid.NewGuid();
        var result = await controller.Action(new HotCacheAction("promote", id, false), CancellationToken.None);
        Assert.IsType<NoContentResult>(result);
        Assert.Equal(id, store.Action!.ItemId);
    }

    [Fact]
    public void Page_IsAnAdministratorDashboardView()
    {
        var page = new HotCacheController(new Store()).Page();
        Assert.Contains("Jellyfin Hot Cache", page.Content, StringComparison.Ordinal);
        Assert.Contains("Unraid /temp", page.Content, StringComparison.Ordinal);
        Assert.Contains("CephFS 300 GiB", page.Content, StringComparison.Ordinal);
        Assert.Contains("Inventory by series", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheHistoryKind", page.Content, StringComparison.Ordinal);
        Assert.Contains("confirmBulkEviction", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page.Content, StringComparison.Ordinal);
    }

    private sealed class Store : IHotCacheAdministration
    {
        public bool RejectAction { get; init; }

        public bool RejectSettings { get; init; }

        public HotCacheAction? Action { get; private set; }

        public Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken) => Task.FromResult(new HotCacheAdministrationSnapshot(new HotCacheSettings("unraid-temp", false, .9, .75), [], [], [], []));

        public Task UpdateSettingsAsync(HotCacheSettings settings, CancellationToken cancellationToken) => RejectSettings ? throw new ArgumentException() : Task.CompletedTask;

        public Task QueueActionAsync(HotCacheAction action, CancellationToken cancellationToken)
        {
            if (RejectAction)
            {
                throw new ArgumentException();
            }

            Action = action;
            return Task.CompletedTask;
        }
    }
}
