using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public sealed class HotCacheControllerTests
{
    [Fact]
    public async Task Get_WithoutHistoryFilter_ForwardsNullFilter()
    {
        var store = new Store();

        var result = await new HotCacheController(store).Get(null, CancellationToken.None);

        Assert.NotNull(result.Result);
        Assert.Null(store.HistoryKind);
    }

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
    public async Task Action_ConfirmedBulkEviction_IsAccepted()
    {
        var store = new Store();
        var controller = new HotCacheController(store);

        var result = await controller.Action(new HotCacheAction("evict", null, true), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(new HotCacheAction("evict", null, true), store.Action);
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
    public async Task Cache_UsesLibraryItemIdAndSeasonScope()
    {
        var store = new Store { Cached = 1 };
        var request = new HotCacheManualCacheRequest(Guid.NewGuid(), true);

        var result = await new HotCacheController(store).Cache(request, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(request, store.CacheRequest);
    }

    [Fact]
    public void Page_IsAnAdministratorDashboardView()
    {
        var page = new HotCacheController(new Store()).Page();
        Assert.Contains("<h1>Hot Cache</h1>", page.Content, StringComparison.Ordinal);
        Assert.Contains("Storage control plane", page.Content, StringComparison.Ordinal);
        Assert.Contains("data-role=\"page\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("pluginConfigurationPage", page.Content, StringComparison.Ordinal);
        Assert.Contains("data-role=\"content\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("content-primary", page.Content, StringComparison.Ordinal);
        Assert.Contains("Unraid /temp", page.Content, StringComparison.Ordinal);
        Assert.Contains("CephFS 300 GiB", page.Content, StringComparison.Ordinal);
        Assert.Contains("Inventory by series", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheHistoryKind", page.Content, StringComparison.Ordinal);
        Assert.Contains("confirmBulkEviction", page.Content, StringComparison.Ordinal);
        Assert.Contains("hc-meter", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheLookahead", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheReserve", page.Content, StringComparison.Ordinal);
        Assert.Contains("maxLookahead", page.Content, StringComparison.Ordinal);
        Assert.Contains("reserveFreeBytes", page.Content, StringComparison.Ordinal);
        Assert.Contains("hc-summary-grid", page.Content, StringComparison.Ordinal);
        Assert.Contains("No cache candidates yet", page.Content, StringComparison.Ordinal);
        Assert.Contains("Unable to load hot-cache state", page.Content, StringComparison.Ordinal);
        Assert.Contains("toLocaleString", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheManualItem", page.Content, StringComparison.Ordinal);
        Assert.Contains("hotCacheSpinner", page.Content, StringComparison.Ordinal);
        Assert.Contains("setInterval(load,2000)", page.Content, StringComparison.Ordinal);
        Assert.Contains("const selectedItem=picker.value", page.Content, StringComparison.Ordinal);
        Assert.Contains("details.dataset.series=series", page.Content, StringComparison.Ordinal);
        Assert.Contains("viewState.openSeries.has(series)", page.Content, StringComparison.Ordinal);
        Assert.Contains("scrollBySeries:new Map()", page.Content, StringComparison.Ordinal);
        Assert.Contains("details.querySelector('.hc-table-wrap')?.scrollLeft||0", page.Content, StringComparison.Ordinal);
        Assert.Contains("tableWrap.scrollLeft=viewState.scrollBySeries.get(details.dataset.series)||0", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_SelectsCamelCaseJsonProfileForPopulatedDashboardData()
    {
        var snapshot = new HotCacheAdministrationSnapshot(
            new HotCacheSettings("unraid-temp", false, .9, .75),
            [new HotCacheBackendStatus("unraid-temp", true, true, false, 1000, 250, 750, DateTime.UtcNow)],
            [new HotCacheQueueSummary("copied", 1, 250)],
            [new HotCacheInventoryItem(Guid.NewGuid(), "Reacher", "Episode 1", "playback", 1, 100, 250, "unraid-temp", DateTime.UtcNow, DateTime.UtcNow, "copied")],
            [new HotCacheHistoryEntry(1, "copied", "Reacher: Episode 1", DateTime.UtcNow)]);
        var camelCaseJson = JsonSerializer.Serialize(snapshot, JsonDefaults.CamelCaseOptions);
        var pascalCaseJson = JsonSerializer.Serialize(snapshot, JsonDefaults.PascalCaseOptions);
        var page = new HotCacheController(new Store()).Page();

        Assert.Contains("\"inventory\"", camelCaseJson, StringComparison.Ordinal);
        Assert.Contains("\"Inventory\"", pascalCaseJson, StringComparison.Ordinal);
        Assert.Contains("Reacher", camelCaseJson, StringComparison.Ordinal);
        Assert.Contains("headers:{Accept:'application/json; profile=\"CamelCase\"'}", page.Content, StringComparison.Ordinal);
    }

    private sealed class Store : IHotCacheAdministration
    {
        public bool RejectAction { get; init; }

        public bool RejectSettings { get; init; }

        public HotCacheAction? Action { get; private set; }

        public string? HistoryKind { get; private set; }

        public int Cached { get; init; }

        public HotCacheManualCacheRequest? CacheRequest { get; private set; }

        public Task<HotCacheAdministrationSnapshot> GetSnapshotAsync(string? historyKind, CancellationToken cancellationToken)
        {
            HistoryKind = historyKind;
            return Task.FromResult(new HotCacheAdministrationSnapshot(new HotCacheSettings("unraid-temp", false, .9, .75), [], [], [], []));
        }

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

        public Task<int> CacheLibraryItemAsync(HotCacheManualCacheRequest request, CancellationToken cancellationToken)
        {
            CacheRequest = request;
            return Task.FromResult(Cached);
        }
    }
}
