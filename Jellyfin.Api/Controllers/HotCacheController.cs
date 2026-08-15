using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>Administrator-only API and dashboard page for the PostgreSQL-backed hot cache.</summary>
[Authorize(Policy = Policies.RequiresElevation)]
[Route("HotCache")]
public class HotCacheController(IHotCacheAdministration administration) : BaseJellyfinApiController
{
    internal static string PageHtml => GetPageHtml();

    /// <summary>Gets shared cache settings, observations, inventory, queue totals and history.</summary>
    /// <param name="historyKind">Optional history category filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current hot-cache administration snapshot.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HotCacheAdministrationSnapshot>> Get([FromQuery] string? historyKind, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await administration.GetSnapshotAsync(historyKind, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return BadRequest("Unknown history filter.");
        }
    }

    /// <summary>Updates the selected backend, pause state and validated watermarks.</summary>
    /// <param name="settings">The shared hot-cache settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A no-content response.</returns>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateSettings([FromBody] HotCacheSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await administration.UpdateSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>Queues an administrator command for existing inventory only.</summary>
    /// <param name="action">The requested inventory action.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A no-content response when the action is accepted.</returns>
    [HttpPost("Actions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Action([FromBody] HotCacheAction action, CancellationToken cancellationToken)
    {
        try
        {
            await administration.QueueActionAsync(action, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>Returns the dashboard view, which is intentionally served only to Jellyfin administrators.</summary>
    /// <returns>The administrator dashboard HTML.</returns>
    [HttpGet("Page")]
    [Produces("text/html")]
    public ContentResult Page() => Content(PageHtml, "text/html");

    private static string GetPageHtml() => """
        <div id="hotCachePage" data-role="page" class="page type-interior pluginConfigurationPage" data-require="emby-button,emby-checkbox,emby-input,emby-select">
          <div data-role="content">
            <div class="content-primary">
          <h1>Jellyfin Hot Cache</h1><p id="hotCacheStatus">Loading shared coordinator state…</p>
          <label>Backend <select id="hotCacheBackend"><option value="unraid-temp">Unraid /temp</option><option value="cephfs">CephFS 300 GiB</option></select></label>
          <label><input id="hotCachePaused" type="checkbox"> Pause</label>
          <label>High watermark <input id="hotCacheHigh" type="number" min="0.01" max="0.99" step="0.01"></label>
          <label>Low watermark <input id="hotCacheLow" type="number" min="0.01" max="0.98" step="0.01"></label>
          <label>Maximum lookahead <input id="hotCacheLookahead" type="number" min="0" max="100" step="1"></label>
          <label>Reserve free bytes <input id="hotCacheReserve" type="number" min="0" step="1"></label>
          <button is="emby-button" id="hotCacheSave">Save settings</button>
          <label>Inventory item <select id="hotCacheItem"><option value="">All eligible items</option></select></label>
          <button is="emby-button" data-hot-cache-action="promote">Promote</button><button is="emby-button" data-hot-cache-action="evict">Evict</button><button is="emby-button" data-hot-cache-action="retry">Retry</button><button is="emby-button" data-hot-cache-action="reconcile">Reconcile</button>
          <h2>Backends</h2><div id="hotCacheBackends"></div>
          <h2>Queue</h2><div id="hotCacheQueue"></div>
          <h2>Inventory by series</h2><div id="hotCacheInventory"></div>
          <h2>History</h2><label>Filter <select id="hotCacheHistoryKind"><option value="">All</option><option>copied</option><option>evicted</option><option>failed</option><option>settings</option><option>backend</option><option>promoted</option><option>retry</option><option>reconcile</option></select></label><div id="hotCacheHistory"></div>
          <script>
          (()=>{
          const api=window.ApiClient,root=api.serverAddress()+'/HotCache',byId=id=>document.querySelector('#'+id),formatBytes=v=>new Intl.NumberFormat().format(v)+' bytes',cell=(row,value)=>{const td=document.createElement('td');td.textContent=value;row.append(td);};
          const table=(target,headers,rows)=>{const t=document.createElement('table'),h=t.insertRow();headers.forEach(x=>{const th=document.createElement('th');th.textContent=x;h.append(th);});rows.forEach(values=>{const r=t.insertRow();values.forEach(x=>cell(r,x));});target.replaceChildren(t);};
          const load=async()=>{const kind=byId('hotCacheHistoryKind').value,query=kind?'?historyKind='+encodeURIComponent(kind):'',s=await api.ajax({url:root+query,type:'GET',dataType:'json'});byId('hotCacheBackend').value=s.settings.backend;byId('hotCachePaused').checked=s.settings.paused;byId('hotCacheHigh').value=s.settings.highWatermark;byId('hotCacheLow').value=s.settings.lowWatermark;byId('hotCacheLookahead').value=s.settings.maxLookahead;byId('hotCacheReserve').value=s.settings.reserveFreeBytes;table(byId('hotCacheBackends'),['Backend','Mounted','Healthy','Stale','Used / Total','Used %','Available','Observed'],s.backends.map(x=>[x.name,x.mounted,x.healthy,x.stale,formatBytes(x.usedBytes)+' / '+formatBytes(x.totalBytes),x.totalBytes?Math.round((x.usedBytes/x.totalBytes)*100)+'%':'n/a',formatBytes(x.availableBytes),x.observedAtUtc]));table(byId('hotCacheQueue'),['State','Count','Bytes'],s.queue.map(x=>[x.state,x.count,formatBytes(x.bytes)]));const groups={};s.inventory.forEach(x=>(groups[x.seriesName]??=[]).push(x));const inventory=byId('hotCacheInventory');inventory.replaceChildren();Object.keys(groups).sort().forEach(series=>{const title=document.createElement('h3');title.textContent=series;inventory.append(title);const body=document.createElement('div');inventory.append(body);table(body,['Episode','Reason','Users','Priority','Size','Backend','Created','Updated','State'],groups[series].map(x=>[x.episode,x.reason,x.interestedUsers,x.priority,formatBytes(x.sizeBytes),x.backend,x.createdAtUtc,x.updatedAtUtc,x.state]));});const item=byId('hotCacheItem');item.replaceChildren(new Option('All eligible items',''));s.inventory.forEach(x=>item.add(new Option(x.seriesName+': '+x.episode,x.itemId)));table(byId('hotCacheHistory'),['When','Kind','Detail'],s.history.map(x=>[x.createdAtUtc,x.kind,x.detail]));byId('hotCacheStatus').textContent=s.settings.paused?'Paused: workers hold their queues':'Running on '+s.settings.backend;};
          byId('hotCacheSave').onclick=async()=>{const settings={backend:byId('hotCacheBackend').value,paused:byId('hotCachePaused').checked,highWatermark:Number(byId('hotCacheHigh').value),lowWatermark:Number(byId('hotCacheLow').value),maxLookahead:Number(byId('hotCacheLookahead').value),reserveFreeBytes:Number(byId('hotCacheReserve').value)};await api.ajax({url:root+'/Settings',type:'PUT',contentType:'application/json',data:JSON.stringify(settings)});load();};
          document.querySelectorAll('[data-hot-cache-action]').forEach(button=>button.onclick=async()=>{const kind=button.dataset.hotCacheAction,id=byId('hotCacheItem').value||null,confirmBulkEviction=kind==='evict'&&!id&&window.confirm('Evict every eligible item?');if(kind==='evict'&&!id&&!confirmBulkEviction)return;await api.ajax({url:root+'/Actions',type:'POST',contentType:'application/json',data:JSON.stringify({kind,itemId:id,confirmBulkEviction})});load();});byId('hotCacheHistoryKind').onchange=load;load();})();
          </script>
            </div>
          </div>
        </div>
        """;
}
