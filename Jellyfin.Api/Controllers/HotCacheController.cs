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

    /// <summary>Queues a movie, an episode, or all episodes in a season selected from the Jellyfin library.</summary>
    /// <param name="request">The Jellyfin library item and whether to expand a season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A no-content response when items were queued, or an error response otherwise.</returns>
    [HttpPost("Cache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Cache([FromBody] HotCacheManualCacheRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await administration.CacheLibraryItemAsync(request, cancellationToken).ConfigureAwait(false);
            return accepted == 0 ? NotFound() : NoContent();
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
          <style>
            #hotCachePage { --hc-accent: #00a4dc; --hc-good: #39b86b; --hc-warn: #e9a23b; --hc-bad: #e45c63; --hc-surface: rgba(255,255,255,.055); --hc-border: rgba(255,255,255,.12); }
            #hotCachePage .hc-shell { max-width: 88rem; padding-bottom: 5rem; }
            #hotCachePage .hc-hero { align-items: flex-end; display: flex; gap: 2rem; justify-content: space-between; margin: 1.5rem 0 1.25rem; }
            #hotCachePage .hc-eyebrow { color: var(--hc-accent); font-size: .76rem; font-weight: 700; letter-spacing: .13em; margin: 0 0 .35rem; text-transform: uppercase; }
            #hotCachePage h1 { font-size: clamp(2rem, 5vw, 3.4rem); letter-spacing: -.04em; line-height: 1; margin: 0; }
            #hotCachePage .hc-subtitle { color: rgba(255,255,255,.68); margin: .7rem 0 0; max-width: 42rem; }
            #hotCachePage .hc-live { align-items: center; display: flex; gap: .55rem; margin-top: .85rem; }
            #hotCachePage .hc-live-dot { background: var(--hc-warn); border-radius: 50%; box-shadow: 0 0 0 .25rem rgba(233,162,59,.16); height: .58rem; width: .58rem; }
            #hotCachePage .hc-live-dot.good { background: var(--hc-good); box-shadow: 0 0 0 .25rem rgba(57,184,107,.16); }
            #hotCachePage .hc-live-dot.bad { background: var(--hc-bad); box-shadow: 0 0 0 .25rem rgba(228,92,99,.16); }
            #hotCachePage .hc-spinner { display: none; font-size: 1.1rem; line-height: 1; }
            #hotCachePage .hc-spinner.active { animation: hotCacheSpin 1s linear infinite; display: inline-block; }
            @keyframes hotCacheSpin { to { transform: rotate(360deg); } }
            #hotCachePage #hotCacheStatus { font-size: .9rem; margin: 0; }
            #hotCachePage .hc-refresh { min-width: 7.25rem; }
            #hotCachePage .hc-alert { background: rgba(228,92,99,.14); border: 1px solid rgba(228,92,99,.5); border-radius: .6rem; color: #ffd7d9; margin: 0 0 1.25rem; padding: .85rem 1rem; }
            #hotCachePage .hc-summary-grid { display: grid; gap: .75rem; grid-template-columns: repeat(5,minmax(0,1fr)); margin-bottom: 1.25rem; }
            #hotCachePage .hc-stat { background: linear-gradient(145deg,rgba(255,255,255,.075),rgba(255,255,255,.025)); border: 1px solid var(--hc-border); border-radius: .75rem; min-width: 0; padding: 1rem; }
            #hotCachePage .hc-stat-label { color: rgba(255,255,255,.58); font-size: .72rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
            #hotCachePage .hc-stat-value { display: block; font-size: 1.55rem; font-weight: 700; margin-top: .32rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            #hotCachePage .hc-stat-note { color: rgba(255,255,255,.55); display: block; font-size: .78rem; margin-top: .2rem; }
            #hotCachePage .hc-control-grid { display: grid; gap: 1rem; grid-template-columns: minmax(0,1.45fr) minmax(18rem,.8fr); margin-bottom: 1.25rem; }
            #hotCachePage .hc-panel { background: var(--hc-surface); border: 1px solid var(--hc-border); border-radius: .75rem; padding: 1.15rem; }
            #hotCachePage .hc-panel-head { align-items: center; display: flex; gap: 1rem; justify-content: space-between; margin-bottom: 1rem; }
            #hotCachePage .hc-panel h2 { font-size: 1.12rem; margin: 0; }
            #hotCachePage .hc-panel-copy { color: rgba(255,255,255,.58); font-size: .83rem; margin: .25rem 0 0; }
            #hotCachePage .hc-form-grid { display: grid; gap: .85rem; grid-template-columns: repeat(3,minmax(0,1fr)); }
            #hotCachePage .hc-field { display: flex; flex-direction: column; gap: .35rem; min-width: 0; }
            #hotCachePage .hc-field > span { color: rgba(255,255,255,.67); font-size: .78rem; font-weight: 600; }
            #hotCachePage .hc-field input, #hotCachePage .hc-field select { background: rgba(0,0,0,.22); border: 1px solid rgba(255,255,255,.18); border-radius: .35rem; box-sizing: border-box; color: inherit; min-height: 2.65rem; padding: .55rem .65rem; width: 100%; }
            #hotCachePage .hc-check { align-items: center; display: flex; gap: .55rem; margin: .95rem 0; }
            #hotCachePage .hc-actions { display: flex; flex-wrap: wrap; gap: .55rem; }
            #hotCachePage .hc-actions button { margin: 0; }
            #hotCachePage .hc-section { margin-top: 1.25rem; }
            #hotCachePage .hc-backend-grid, #hotCachePage .hc-queue-grid { display: grid; gap: .75rem; grid-template-columns: repeat(auto-fit,minmax(13rem,1fr)); }
            #hotCachePage .hc-backend, #hotCachePage .hc-queue-card { background: rgba(0,0,0,.14); border: 1px solid rgba(255,255,255,.09); border-radius: .6rem; padding: .9rem; }
            #hotCachePage .hc-backend-title { align-items: center; display: flex; gap: .6rem; justify-content: space-between; }
            #hotCachePage .hc-backend-title strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            #hotCachePage .hc-badge { background: rgba(255,255,255,.1); border-radius: 999px; display: inline-block; font-size: .7rem; font-weight: 700; letter-spacing: .04em; padding: .22rem .5rem; text-transform: uppercase; }
            #hotCachePage .hc-badge.good { background: rgba(57,184,107,.18); color: #8ce5ad; }
            #hotCachePage .hc-badge.warn { background: rgba(233,162,59,.18); color: #ffd28b; }
            #hotCachePage .hc-badge.bad { background: rgba(228,92,99,.18); color: #ffabb0; }
            #hotCachePage .hc-meter { background: rgba(255,255,255,.09); border-radius: 999px; height: .42rem; margin: .9rem 0 .55rem; overflow: hidden; }
            #hotCachePage .hc-meter > span { background: var(--hc-accent); border-radius: inherit; display: block; height: 100%; }
            #hotCachePage .hc-meta { color: rgba(255,255,255,.58); display: flex; font-size: .76rem; gap: .6rem; justify-content: space-between; }
            #hotCachePage .hc-queue-card strong { display: block; font-size: 1.35rem; margin-top: .3rem; }
            #hotCachePage .hc-empty { border: 1px dashed rgba(255,255,255,.17); border-radius: .55rem; color: rgba(255,255,255,.56); padding: 1.6rem 1rem; text-align: center; }
            #hotCachePage .hc-series { background: rgba(0,0,0,.13); border: 1px solid rgba(255,255,255,.09); border-radius: .6rem; margin-top: .65rem; overflow: hidden; }
            #hotCachePage .hc-series summary { align-items: center; cursor: pointer; display: flex; gap: 1rem; justify-content: space-between; list-style: none; padding: .9rem 1rem; }
            #hotCachePage .hc-series summary::-webkit-details-marker { display: none; }
            #hotCachePage .hc-series-count { color: rgba(255,255,255,.55); font-size: .78rem; white-space: nowrap; }
            #hotCachePage .hc-table-wrap { overflow-x: auto; }
            #hotCachePage table { border-collapse: collapse; font-size: .82rem; width: 100%; }
            #hotCachePage th { color: rgba(255,255,255,.56); font-size: .68rem; font-weight: 700; letter-spacing: .07em; text-align: left; text-transform: uppercase; }
            #hotCachePage th, #hotCachePage td { border-bottom: 1px solid rgba(255,255,255,.08); padding: .68rem .72rem; vertical-align: middle; }
            #hotCachePage tbody tr:last-child td { border-bottom: 0; }
            #hotCachePage .hc-history-tools { align-items: flex-end; display: flex; gap: .7rem; }
            #hotCachePage .hc-history-tools .hc-field { min-width: 10rem; }
            #hotCachePage button:disabled { cursor: wait; opacity: .55; }
            @media (max-width: 900px) { #hotCachePage .hc-summary-grid { grid-template-columns: repeat(2,minmax(0,1fr)); } #hotCachePage .hc-control-grid { grid-template-columns: 1fr; } }
            @media (max-width: 620px) { #hotCachePage .hc-hero { align-items: stretch; flex-direction: column; gap: 1rem; } #hotCachePage .hc-form-grid { grid-template-columns: 1fr; } #hotCachePage .hc-summary-grid { grid-template-columns: 1fr 1fr; } #hotCachePage .hc-stat:last-child { grid-column: 1 / -1; } }
          </style>
          <div data-role="content">
            <div class="content-primary hc-shell">
              <header class="hc-hero">
                <div>
                  <p class="hc-eyebrow">Storage control plane</p>
                  <h1>Hot Cache</h1>
                  <p class="hc-subtitle">Shared playback-aware cache state across every Jellyfin replica.</p>
                  <div class="hc-live"><span id="hotCacheStatusDot" class="hc-live-dot"></span><span id="hotCacheSpinner" class="hc-spinner" aria-label="Caching">⟳</span><p id="hotCacheStatus" aria-live="polite">Loading coordinator state…</p></div>
                </div>
                <button is="emby-button" type="button" class="raised hc-refresh" id="hotCacheRefresh">Refresh</button>
              </header>

              <div id="hotCacheError" class="hc-alert" role="alert" hidden></div>

              <section class="hc-summary-grid" aria-label="Hot-cache summary">
                <article class="hc-stat"><span class="hc-stat-label">Backend</span><strong class="hc-stat-value" id="hotCacheSummaryBackend">—</strong><span class="hc-stat-note" id="hotCacheSummaryHealth">Waiting for data</span></article>
                <article class="hc-stat"><span class="hc-stat-label">Active work</span><strong class="hc-stat-value" id="hotCacheSummaryActive">—</strong><span class="hc-stat-note">Queued, copying, or evicting</span></article>
                <article class="hc-stat"><span class="hc-stat-label">Cached</span><strong class="hc-stat-value" id="hotCacheSummaryCached">—</strong><span class="hc-stat-note" id="hotCacheSummaryCachedBytes">Completed promotions</span></article>
                <article class="hc-stat"><span class="hc-stat-label">Inventory</span><strong class="hc-stat-value" id="hotCacheSummaryInventory">—</strong><span class="hc-stat-note" id="hotCacheSummarySeries">Across all series</span></article>
                <article class="hc-stat"><span class="hc-stat-label">Failures</span><strong class="hc-stat-value" id="hotCacheSummaryFailures">—</strong><span class="hc-stat-note">Items requiring attention</span></article>
              </section>

              <div class="hc-control-grid">
                <section class="hc-panel">
                  <div class="hc-panel-head"><div><h2>Cache policy</h2><p class="hc-panel-copy">Capacity limits and worker behavior are shared by all replicas.</p></div></div>
                  <div class="hc-form-grid">
                    <label class="hc-field"><span>Active backend</span><select id="hotCacheBackend"><option value="unraid-temp">Unraid /temp</option><option value="cephfs">CephFS 300 GiB</option></select></label>
                    <label class="hc-field"><span>High watermark (%)</span><input id="hotCacheHigh" type="number" min="1" max="99" step="1"></label>
                    <label class="hc-field"><span>Low watermark (%)</span><input id="hotCacheLow" type="number" min="1" max="98" step="1"></label>
                    <label class="hc-field"><span>Maximum lookahead (1–6 episodes)</span><input id="hotCacheLookahead" type="number" min="1" max="6" step="1"></label>
                    <label class="hc-field"><span>Reserve free space (GiB)</span><input id="hotCacheReserve" type="number" min="0" step="1"></label>
                  </div>
                  <label class="hc-check"><input id="hotCachePaused" type="checkbox"> Pause new worker claims</label>
                  <button is="emby-button" type="button" class="raised button-submit" id="hotCacheSave">Save policy</button>
                </section>

                <section class="hc-panel">
                  <div class="hc-panel-head"><div><h2>Inventory actions</h2><p class="hc-panel-copy">Operate on durable inventory IDs; media paths are never submitted.</p></div></div>
                  <label class="hc-field"><span>Inventory item</span><select id="hotCacheItem"><option value="">All eligible items</option></select></label>
                  <div class="hc-actions" style="margin-top: .9rem">
                    <button is="emby-button" type="button" class="raised" data-hot-cache-action="promote">Promote</button>
                    <button is="emby-button" type="button" class="raised" data-hot-cache-action="retry">Retry</button>
                    <button is="emby-button" type="button" data-hot-cache-action="evict">Evict</button>
                    <button is="emby-button" type="button" data-hot-cache-action="reconcile">Reconcile</button>
                  </div>
                  <div class="hc-panel-head" style="margin-top:1.25rem"><div><h2>Manual cache</h2><p class="hc-panel-copy">Enter a Jellyfin library movie or episode ID, or expand a season into eligible episodes.</p></div></div>
                  <label class="hc-field"><span>Movie, episode, or season ID</span><input id="hotCacheManualItem" type="text" placeholder="Jellyfin library item ID"></label>
                  <label class="hc-check"><input id="hotCacheManualSeason" type="checkbox"> Cache the full season</label>
                  <button is="emby-button" type="button" class="raised" id="hotCacheManualCache">Cache now</button>
                </section>
              </div>

              <section class="hc-panel hc-section">
                <div class="hc-panel-head"><div><h2>Backends</h2><p class="hc-panel-copy">Latest mount, health, and capacity observations from workers.</p></div></div>
                <div id="hotCacheBackends" class="hc-backend-grid" aria-live="polite"></div>
              </section>

              <section class="hc-panel hc-section">
                <div class="hc-panel-head"><div><h2>Queue</h2><p class="hc-panel-copy">Current durable job totals, including completed and failed work.</p></div></div>
                <div id="hotCacheQueue" class="hc-queue-grid" aria-live="polite"></div>
              </section>

              <section class="hc-panel hc-section">
                <div class="hc-panel-head"><div><h2>Inventory by series and movies</h2><p class="hc-panel-copy">Playback, lookahead, and requested movie candidates grouped for quick inspection.</p></div></div>
                <div id="hotCacheInventory" aria-live="polite"></div>
              </section>

              <section class="hc-panel hc-section">
                <div class="hc-panel-head">
                  <div><h2>History</h2><p class="hc-panel-copy">Recent worker outcomes and administrator actions.</p></div>
                  <div class="hc-history-tools"><label class="hc-field"><span>Event type</span><select id="hotCacheHistoryKind"><option value="">All events</option><option>copied</option><option>evicted</option><option>failed</option><option>settings</option><option>backend</option><option>promoted</option><option>retry</option><option>reconcile</option><option>manual</option></select></label></div>
                </div>
                <div id="hotCacheHistory" aria-live="polite"></div>
              </section>

              <script>
              (()=>{
                const page=document.querySelector('#hotCachePage'),api=window.ApiClient,root=api.serverAddress()+'/HotCache',gibibyte=1073741824,byId=id=>page.querySelector('#'+id);
                const stateLabels={queued:'Queued',copying:'Copying',evicting:'Evicting',copied:'Cached',evicted:'Evicted',failed:'Failed'};
                const viewState={inventoryRendered:false,openSeries:new Set(),scrollBySeries:new Map(),inventorySignature:null,pickerSignature:null,backendSignature:null,queueSignature:null,historySignature:null};
                const element=(tag,className,text)=>{const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=text;return node;};
                const formatBytes=value=>{const bytes=Number(value)||0;if(bytes<1024)return bytes+' B';const units=['KiB','MiB','GiB','TiB'];let size=bytes,index=-1;do{size/=1024;index++;}while(size>=1024&&index<units.length-1);return new Intl.NumberFormat(undefined,{maximumFractionDigits:size>=10?1:2}).format(size)+' '+units[index];};
                const formatDate=value=>{const date=new Date(value);return Number.isNaN(date.valueOf())?'Unknown':date.toLocaleString();};
                const stateBadge=state=>{const tone=state==='copied'?'good':state==='failed'?'bad':state==='queued'||state==='copying'||state==='evicting'?'warn':'';return element('span','hc-badge '+tone,stateLabels[state]||state||'Unknown');};
                const empty=(target,message)=>target.replaceChildren(element('div','hc-empty',message));
                const table=(target,headers,rows)=>{if(!rows.length){empty(target,'No records to show.');return;}const wrap=element('div','hc-table-wrap'),tableNode=document.createElement('table'),head=tableNode.createTHead().insertRow();headers.forEach(header=>{const th=document.createElement('th');th.textContent=header;head.append(th);});const body=tableNode.createTBody();rows.forEach(values=>{const row=body.insertRow();values.forEach(value=>{const cell=row.insertCell();if(value instanceof Node)cell.append(value);else cell.textContent=value;});});wrap.append(tableNode);target.replaceChildren(wrap);};
                const queueValue=(queue,state)=>queue.find(item=>item.state===state)||{count:0,bytes:0};
                const setBusy=busy=>{byId('hotCacheRefresh').disabled=busy;byId('hotCacheRefresh').textContent=busy?'Refreshing…':'Refresh';};
                const setControlValue=(id,value)=>{const control=byId(id);if(document.activeElement!==control)control.value=value;};
                const showError=error=>{const target=byId('hotCacheError');target.textContent='Unable to load hot-cache state: '+(error?.message||'request failed');target.hidden=false;byId('hotCacheStatus').textContent='Coordinator data unavailable';byId('hotCacheStatusDot').className='hc-live-dot bad';};

                const renderSummary=snapshot=>{
                  const settings=snapshot.settings,backends=snapshot.backends,queue=snapshot.queue,inventory=snapshot.inventory;
                  const selected=backends.find(item=>item.name===settings.backend),healthy=backends.filter(item=>item.mounted&&item.healthy&&!item.stale).length;
                  const active=['queued','copying','evicting'].reduce((total,state)=>total+queueValue(queue,state).count,0),cached=queueValue(queue,'copied'),failures=queueValue(queue,'failed').count,series=new Set(inventory.map(item=>item.seriesName)).size;
                  byId('hotCacheSummaryBackend').textContent=settings.backend;byId('hotCacheSummaryHealth').textContent=selected?(selected.healthy&&!selected.stale?'Healthy observation':'Needs attention'):healthy+'/'+backends.length+' backends healthy';
                  byId('hotCacheSummaryActive').textContent=new Intl.NumberFormat().format(active);byId('hotCacheSummaryCached').textContent=new Intl.NumberFormat().format(cached.count);byId('hotCacheSummaryCachedBytes').textContent=formatBytes(cached.bytes)+' stored';
                  byId('hotCacheSummaryInventory').textContent=new Intl.NumberFormat().format(inventory.length);byId('hotCacheSummarySeries').textContent=series+' groups';byId('hotCacheSummaryFailures').textContent=new Intl.NumberFormat().format(failures);
                };

                const renderBackends=backends=>{const signature=JSON.stringify(backends);if(signature===viewState.backendSignature)return;viewState.backendSignature=signature;const target=byId('hotCacheBackends');if(!backends.length){empty(target,'No worker has reported a backend observation yet.');return;}target.replaceChildren(...backends.map(backend=>{const card=element('article','hc-backend'),title=element('div','hc-backend-title'),status=!backend.mounted?['Unmounted','bad']:backend.stale?['Stale','warn']:backend.healthy?['Healthy','good']:['Unhealthy','bad'],percent=backend.totalBytes?Math.min(100,Math.round((backend.usedBytes/backend.totalBytes)*100)):0,meter=element('div','hc-meter'),fill=document.createElement('span');title.append(element('strong','',backend.name),element('span','hc-badge '+status[1],status[0]));fill.style.width=percent+'%';meter.append(fill);const capacity=element('div','hc-meta');capacity.append(element('span','',formatBytes(backend.usedBytes)+' used'),element('span','',percent+'%'));const observed=element('div','hc-meta');observed.style.marginTop='.45rem';observed.append(element('span','',formatBytes(backend.availableBytes)+' available'),element('span','',formatDate(backend.observedAtUtc)));card.append(title,meter,capacity,observed);return card;}));};
                const renderQueue=queue=>{const signature=JSON.stringify(queue);if(signature===viewState.queueSignature)return;viewState.queueSignature=signature;const target=byId('hotCacheQueue'),order=['queued','copying','evicting','copied','evicted','failed'];target.replaceChildren(...order.map(state=>{const item=queueValue(queue,state),card=element('article','hc-queue-card');card.append(stateBadge(state),element('strong','',new Intl.NumberFormat().format(item.count)),element('span','hc-stat-note',formatBytes(item.bytes)));return card;}));};
                const renderInventory=inventory=>{const target=byId('hotCacheInventory'),picker=byId('hotCacheItem'),signature=JSON.stringify(inventory),inventoryChanged=signature!==viewState.inventorySignature,pickerChanged=signature!==viewState.pickerSignature;const selectedItem=picker.value;if(pickerChanged){if(document.activeElement!==picker){picker.replaceChildren(new Option('All eligible items',''));inventory.forEach(item=>picker.add(new Option(item.seriesName==='Movies'?item.episode:item.seriesName+' — '+item.episode,item.itemId)));if(inventory.some(item=>item.itemId===selectedItem))picker.value=selectedItem;viewState.pickerSignature=signature;}}if(!inventoryChanged)return;if(viewState.inventoryRendered){viewState.openSeries=new Set(Array.from(target.querySelectorAll('.hc-series[open]')).map(details=>details.dataset.series));viewState.scrollBySeries=new Map(Array.from(target.querySelectorAll('.hc-series')).map(details=>[details.dataset.series,details.querySelector('.hc-table-wrap')?.scrollLeft||0]));}if(!inventory.length){empty(target,'No cache candidates yet. Start or resume an episode, then refresh.');viewState.inventoryRendered=true;viewState.inventorySignature=signature;return;}const groups=new Map();inventory.forEach(item=>{if(!groups.has(item.seriesName))groups.set(item.seriesName,[]);groups.get(item.seriesName).push(item);});const renderedSeries=Array.from(groups.entries()).sort(([left],[right])=>left==='Movies'?-1:right==='Movies'?1:left.localeCompare(right)).map(([series,items],index)=>{const isMovie=series==='Movies',details=element('details','hc-series');details.dataset.series=series;details.open=viewState.inventoryRendered?viewState.openSeries.has(series):index===0;const summary=document.createElement('summary'),summaryText=element('strong','',series),total=items.reduce((sum,item)=>sum+item.sizeBytes,0),itemKind=isMovie?(items.length===1?'movie':'movies'):(items.length===1?'episode':'episodes');summary.append(summaryText,element('span','hc-series-count',items.length+' '+itemKind+' · '+formatBytes(total)));const body=document.createElement('div');table(body,[isMovie?'Movie':'Episode','State','Interest','Users','Priority','Size','Backend','Updated'],items.map(item=>[item.episode,stateBadge(item.state),item.reason||'—',String(item.interestedUsers),String(item.priority),formatBytes(item.sizeBytes),item.backend||'—',formatDate(item.updatedAtUtc)]));details.append(summary,body);return details;});target.replaceChildren(...renderedSeries);renderedSeries.forEach(details=>{const tableWrap=details.querySelector('.hc-table-wrap');if(tableWrap)tableWrap.scrollLeft=viewState.scrollBySeries.get(details.dataset.series)||0;});viewState.inventoryRendered=true;viewState.inventorySignature=signature;};
                const renderHistory=history=>{const signature=JSON.stringify(history);if(signature===viewState.historySignature)return;viewState.historySignature=signature;table(byId('hotCacheHistory'),['When','Event','Detail'],history.map(item=>[formatDate(item.createdAtUtc),stateBadge(item.kind),item.detail]));};

                const render=snapshot=>{snapshot.backends??=[];snapshot.queue??=[];snapshot.inventory??=[];snapshot.history??=[];const settings=snapshot.settings;setControlValue('hotCacheBackend',settings.backend);if(document.activeElement!==byId('hotCachePaused'))byId('hotCachePaused').checked=settings.paused;setControlValue('hotCacheHigh',Math.round(settings.highWatermark*100));setControlValue('hotCacheLow',Math.round(settings.lowWatermark*100));setControlValue('hotCacheLookahead',settings.maxLookahead);setControlValue('hotCacheReserve',Math.round(settings.reserveFreeBytes/gibibyte));renderSummary(snapshot);renderBackends(snapshot.backends);renderQueue(snapshot.queue);renderInventory(snapshot.inventory);renderHistory(snapshot.history);const busy=snapshot.queue.some(item=>item.state==='queued'||item.state==='copying');byId('hotCacheSpinner').classList.toggle('active',busy);byId('hotCacheStatus').textContent=settings.paused?'Paused — existing work is held':(busy?'Scanning / caching on ':'Running on ')+settings.backend;byId('hotCacheStatusDot').className='hc-live-dot '+(settings.paused?'':'good');};
                const load=async()=>{setBusy(true);byId('hotCacheError').hidden=true;try{const kind=byId('hotCacheHistoryKind').value,query=kind?'?historyKind='+encodeURIComponent(kind):'',snapshot=await api.ajax({url:root+query,type:'GET',dataType:'json',headers:{Accept:'application/json; profile="CamelCase"'}});render(snapshot);}catch(error){showError(error);}finally{setBusy(false);}};
                const mutate=async(button,request)=>{const original=button.textContent;button.disabled=true;button.textContent='Working…';byId('hotCacheError').hidden=true;try{await api.ajax(request);await load();}catch(error){showError(error);}finally{button.disabled=false;button.textContent=original;}};

                byId('hotCacheRefresh').onclick=load;
                byId('hotCacheSave').onclick=async event=>{const settings={backend:byId('hotCacheBackend').value,paused:byId('hotCachePaused').checked,highWatermark:Number(byId('hotCacheHigh').value)/100,lowWatermark:Number(byId('hotCacheLow').value)/100,maxLookahead:Number(byId('hotCacheLookahead').value),reserveFreeBytes:Math.round(Number(byId('hotCacheReserve').value)*gibibyte)};await mutate(event.currentTarget,{url:root+'/Settings',type:'PUT',contentType:'application/json',data:JSON.stringify(settings)});};
                byId('hotCacheManualCache').onclick=async event=>{const itemId=byId('hotCacheManualItem').value.trim();if(!itemId){showError(new Error('Enter a movie, episode, or season ID.'));return;}await mutate(event.currentTarget,{url:root+'/Cache',type:'POST',contentType:'application/json',data:JSON.stringify({itemId,includeSeason:byId('hotCacheManualSeason').checked})});};
                page.querySelectorAll('[data-hot-cache-action]').forEach(button=>button.onclick=async()=>{const kind=button.dataset.hotCacheAction,id=byId('hotCacheItem').value||null,confirmBulkEviction=kind==='evict'&&!id&&window.confirm('Evict every eligible item? This only affects items that are safe to remove.');if(kind==='evict'&&!id&&!confirmBulkEviction)return;if((kind==='promote'||kind==='retry')&&!id){showError(new Error('Choose an inventory item first.'));return;}await mutate(button,{url:root+'/Actions',type:'POST',contentType:'application/json',data:JSON.stringify({kind,itemId:id,confirmBulkEviction})});});
                byId('hotCacheHistoryKind').onchange=load;setInterval(load,2000);load();
              })();
              </script>
            </div>
          </div>
        </div>
        """;
}
