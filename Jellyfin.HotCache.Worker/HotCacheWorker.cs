using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.HotCache.Worker;

public enum HotCacheJobKind { Promotion, Eviction }

public sealed record HotCacheJob(Guid Id, HotCacheJobKind Kind, string CanonicalPath, string? HotPath, long SourceLength, DateTime SourceModifiedUtc, int Priority, bool IsActive, bool IsPinned, bool IsCopying, DateTime LastAccessUtc, int Attempts);
public sealed record HotCacheQueueSnapshot(long Depth, TimeSpan OldestAge, TimeSpan OldestLeaseAge);

public sealed class HotCacheOptions
{
    public required string CanonicalRoot { get; init; }
    public required string HotRoot { get; init; }
    public double HighWatermark { get; init; } = .90;
    public double LowWatermark { get; init; } = .75;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PartialFileMaxAge { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CanonicalRoot) || string.IsNullOrWhiteSpace(HotRoot))
        {
            throw new InvalidOperationException("Hot-cache roots must be configured.");
        }

        if (Path.GetFullPath(CanonicalRoot).Equals(Path.GetFullPath(HotRoot), StringComparison.Ordinal)
            || HighWatermark is <= 0 or > 1
            || LowWatermark is < 0 or >= 1
            || LowWatermark >= HighWatermark
            || LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Hot-cache roots and watermarks are invalid.");
        }
    }
}

public interface IHotCacheJobStore
{
    Task<HotCacheJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task ProgressAsync(Guid jobId, string workerId, long bytes, CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, string workerId, string? hotPath, CancellationToken cancellationToken);
    Task FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken);
    Task<HotCacheJob?> ClaimEvictionAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> CanEvictAsync(Guid jobId, string workerId, CancellationToken cancellationToken);
    Task<HotCacheQueueSnapshot> SnapshotAsync(CancellationToken cancellationToken);
    Task EventAsync(Guid jobId, string kind, string detail, CancellationToken cancellationToken);
}

public interface IFileOperations
{
    long GetAvailableSpace(string path);
    long GetTotalSpace(string path);
    bool FileExists(string path);
    FileInfo GetFileInfo(string path);
    IEnumerable<string> EnumerateFiles(string root, string pattern);
    void CreateDirectory(string path);
    Task CopyAsync(string source, string destination, Func<long, CancellationToken, Task> progress, CancellationToken cancellationToken);
    void MoveNoReplace(string source, string destination);
    void Delete(string path);
}

public sealed class PhysicalFileOperations : IFileOperations
{
    public long GetAvailableSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
    public long GetTotalSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).TotalSize;
    public bool FileExists(string path) => File.Exists(path);
    public FileInfo GetFileInfo(string path) => new(path);
    public IEnumerable<string> EnumerateFiles(string root, string pattern) => Directory.Exists(root) ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories) : [];
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public async Task CopyAsync(string source, string destination, Func<long, CancellationToken, Task> progress, CancellationToken cancellationToken)
    {
        await using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024]; long copied = 0; int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            await progress(copied, cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public void MoveNoReplace(string source, string destination) => File.Move(source, destination, false);
    public void Delete(string path) => File.Delete(path);
}

public sealed class HotCacheWorker
{
    private readonly IHotCacheJobStore _store; private readonly IFileOperations _files; private readonly HotCacheOptions _options; private readonly ILogger<HotCacheWorker> _logger;
    public HotCacheWorker(IHotCacheJobStore store, IFileOperations files, HotCacheOptions options, ILogger<HotCacheWorker> logger)
    {
        options.Validate();
        (_store, _files, _options, _logger) = (store, files, options, logger);
    }
    public async Task ExecuteOnceAsync(string workerId, CancellationToken ct)
    {
        CleanupPartials();
        var job = await _store.ClaimAsync(workerId, _options.LeaseDuration, ct).ConfigureAwait(false);
        if (job is not null) await ExecuteJobAsync(job, workerId, ct).ConfigureAwait(false);
        await EnforceCapacityAsync(workerId, ct).ConfigureAwait(false);
        HotCacheMetrics.Queue(await _store.SnapshotAsync(ct).ConfigureAwait(false));
        HotCacheMetrics.Backend(true);
    }
    private async Task ExecuteJobAsync(HotCacheJob job, string workerId, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            string? hotPath = null;
            if (job.Kind == HotCacheJobKind.Promotion)
            {
                hotPath = await PromoteAsync(job, workerId, ct).ConfigureAwait(false);
            }
            else
            {
                await EvictAsync(job, workerId, ct).ConfigureAwait(false);
            }

            await _store.CompleteAsync(job.Id, workerId, hotPath, ct).ConfigureAwait(false);
            HotCacheMetrics.JobCompleted(job, DateTime.UtcNow - started);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await _store.FailAsync(job.Id, workerId, Bound(ex.Message), CancellationToken.None).ConfigureAwait(false);
            HotCacheMetrics.JobFailed(job, DateTime.UtcNow - started);
            _logger.LogWarning(ex, "Hot-cache job {JobId} for item {ItemId} degraded to cold playback", job.Id, ItemId(job));
        }
    }
    private async Task<string> PromoteAsync(HotCacheJob job, string workerId, CancellationToken ct)
    {
        if (!await _store.RenewAsync(job.Id, workerId, _options.LeaseDuration, ct).ConfigureAwait(false))
        {
            throw new IOException("Worker lease expired before promotion.");
        }

        var source = Contained(_options.CanonicalRoot, job.CanonicalPath);
        var relative = Path.GetRelativePath(Path.GetFullPath(_options.CanonicalRoot), source);
        var target = Contained(_options.HotRoot, Path.Combine(_options.HotRoot, relative));
        var before = _files.GetFileInfo(source);
        if (before.Length != job.SourceLength || before.LastWriteTimeUtc != job.SourceModifiedUtc)
        {
            throw new IOException("Source changed before promotion.");
        }

        if (_files.FileExists(target))
        {
            await _store.EventAsync(job.Id, "already-published", ItemId(job), ct).ConfigureAwait(false);
            return target;
        }

        _files.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{job.Id:N}.partial");
        var nextRenewal = DateTime.UtcNow + TimeSpan.FromTicks(_options.LeaseDuration.Ticks / 3);
        long reportedBytes = 0;
        try
        {
            await _files.CopyAsync(
                source,
                partial,
                async (bytes, cancellationToken) =>
                {
                    await _store.ProgressAsync(job.Id, workerId, bytes, cancellationToken).ConfigureAwait(false);
                    HotCacheMetrics.BytesCopied(bytes - reportedBytes);
                    reportedBytes = bytes;
                    if (DateTime.UtcNow >= nextRenewal)
                    {
                        if (!await _store.RenewAsync(job.Id, workerId, _options.LeaseDuration, cancellationToken).ConfigureAwait(false))
                        {
                            throw new IOException("Worker lease expired during promotion.");
                        }

                        nextRenewal = DateTime.UtcNow + TimeSpan.FromTicks(_options.LeaseDuration.Ticks / 3);
                    }
                },
                ct).ConfigureAwait(false);
            var after = _files.GetFileInfo(source);
            if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                throw new IOException("Source changed during promotion.");
            }

            _files.MoveNoReplace(partial, target);
            await _store.EventAsync(job.Id, "published", ItemId(job), ct).ConfigureAwait(false);
            return target;
        }
        catch
        {
            if (_files.FileExists(partial))
            {
                _files.Delete(partial);
            }

            throw;
        }
    }
    private async Task EvictAsync(HotCacheJob job, string workerId, CancellationToken ct)
    { if (job.IsActive || job.IsPinned || job.Priority > 0 || !await _store.CanEvictAsync(job.Id, workerId, ct).ConfigureAwait(false)) return; var path = Contained(_options.HotRoot, job.HotPath!); if (_files.FileExists(path)) _files.Delete(path); HotCacheMetrics.Evicted("capacity"); await _store.EventAsync(job.Id, "evicted", path, ct).ConfigureAwait(false); }
    private async Task EnforceCapacityAsync(string workerId, CancellationToken ct)
    { if (UsedRatio() < _options.HighWatermark) return; while (UsedRatio() > _options.LowWatermark) { var job = await _store.ClaimEvictionAsync(workerId, _options.LeaseDuration, ct).ConfigureAwait(false); if (job is null) break; await ExecuteJobAsync(job, workerId, ct).ConfigureAwait(false); } }
    private double UsedRatio() => 1d - ((double)_files.GetAvailableSpace(_options.HotRoot) / _files.GetTotalSpace(_options.HotRoot));
    private void CleanupPartials() { foreach (var path in _files.EnumerateFiles(_options.HotRoot, "*.partial")) if (DateTime.UtcNow - _files.GetFileInfo(path).LastWriteTimeUtc > _options.PartialFileMaxAge) _files.Delete(path); }
    private static string Contained(string root, string path) { var fullRoot = Path.GetFullPath(root); var fullPath = Path.GetFullPath(path); var relative = Path.GetRelativePath(fullRoot, fullPath); if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new IOException("Path escapes configured root."); return fullPath; }
    private static string ItemId(HotCacheJob job) => job.Id.ToString("N");
    private static string Bound(string value) => value.Length <= 512 ? value : value[..512];
}

public sealed class HotCacheHostedService(HotCacheWorker worker, ILogger<HotCacheHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) { var id = Environment.MachineName + ":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture); while (!stoppingToken.IsCancellationRequested) { try { await worker.ExecuteOnceAsync(id, stoppingToken).ConfigureAwait(false); } catch (Exception ex) { HotCacheMetrics.Backend(false); logger.LogError(ex, "Hot-cache backend unavailable; serving remains cold."); } await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); } }
}
