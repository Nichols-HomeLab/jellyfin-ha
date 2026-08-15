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
    /// <summary>Gets shared cache settings, observations, inventory, queue totals and history.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HotCacheAdministrationSnapshot>> Get([FromQuery] string? historyKind, CancellationToken cancellationToken)
    {
        try { return Ok(await administration.GetSnapshotAsync(historyKind, cancellationToken).ConfigureAwait(false)); }
        catch (ArgumentException) { return BadRequest("Unknown history filter."); }
    }

    /// <summary>Updates the selected backend, pause state and validated watermarks.</summary>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> UpdateSettings([FromBody] HotCacheSettings settings, CancellationToken cancellationToken)
    {
        try { await administration.UpdateSettingsAsync(settings, cancellationToken).ConfigureAwait(false); return NoContent(); }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
    }

    /// <summary>Queues an administrator command for existing inventory only.</summary>
    [HttpPost("Actions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Action([FromBody] HotCacheAction action, CancellationToken cancellationToken)
    {
        try { await administration.QueueActionAsync(action, cancellationToken).ConfigureAwait(false); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
    }

    /// <summary>Returns the dashboard view, which is intentionally served only to Jellyfin administrators.</summary>
    [HttpGet("Page")]
    [Produces("text/html")]
    public ContentResult Page() => Content(PageHtml, "text/html");

    internal const string PageHtml = """
        <div id="hotCachePage" class="page type-interior">
          <h1>Jellyfin Hot Cache</h1><p id="hotCacheStatus">Loading shared coordinator state…</p>
          <label>Backend <select id="hotCacheBackend"><option value="unraid-temp">Unraid /temp</option><option value="cephfs">CephFS 300 GiB</option></select></label>
          <label><input id="hotCachePaused" type="checkbox"> Pause</label>
          <button is="emby-button" id="hotCacheSave">Save settings</button>
          <label>Inventory item <select id="hotCacheItem"><option value="">All eligible items</option></select></label>
          <button is="emby-button" data-hot-cache-action="promote">Promote</button><button is="emby-button" data-hot-cache-action="evict">Evict</button><button is="emby-button" data-hot-cache-action="retry">Retry</button><button is="emby-button" data-hot-cache-action="reconcile">Reconcile</button>
          <pre id="hotCacheBackends"></pre><pre id="hotCacheQueue"></pre><div id="hotCacheInventory"></div><pre id="hotCacheHistory"></pre>
          <script>
          (async()=>{const api=window.ApiClient, root=api.serverAddress()+'/HotCache'; const load=async()=>{const s=await api.ajax({url:root,type:'GET',dataType:'json'});document.querySelector('#hotCacheBackend').value=s.settings.backend;document.querySelector('#hotCachePaused').checked=s.settings.paused;document.querySelector('#hotCacheBackends').textContent=JSON.stringify(s.backends,null,2);document.querySelector('#hotCacheQueue').textContent=JSON.stringify(s.queue,null,2);document.querySelector('#hotCacheInventory').textContent=s.inventory.map(x=>`${x.seriesName}: ${x.episode} — ${x.state}`).join('\n');document.querySelector('#hotCacheItem').innerHTML='<option value="">All eligible items</option>'+s.inventory.map(x=>`<option value="${x.itemId}">${x.seriesName}: ${x.episode}</option>`).join('');document.querySelector('#hotCacheHistory').textContent=JSON.stringify(s.history,null,2);document.querySelector('#hotCacheStatus').textContent=s.settings.paused?'Paused':'Running';};document.querySelector('#hotCacheSave').onclick=async()=>{const s=await api.ajax({url:root,type:'GET',dataType:'json'});s.settings.backend=document.querySelector('#hotCacheBackend').value;s.settings.paused=document.querySelector('#hotCachePaused').checked;await api.ajax({url:root+'/Settings',type:'PUT',contentType:'application/json',data:JSON.stringify(s.settings)});load();};document.querySelectorAll('[data-hot-cache-action]').forEach(b=>b.onclick=async()=>{const kind=b.dataset.hotCacheAction,id=document.querySelector('#hotCacheItem').value||null,confirmBulkEviction=kind==='evict'&&!id&&window.confirm('Evict every eligible item?');if(kind==='evict'&&!id&&!confirmBulkEviction)return;await api.ajax({url:root+'/Actions',type:'POST',contentType:'application/json',data:JSON.stringify({kind,itemId:id,confirmBulkEviction})});load();});load();})();
          </script>
        </div>
        """;
}
