using Jellyfin.HotCache.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.HotCache.Worker.Tests;

public sealed class HotCacheWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hot-cache-tests-" + Guid.NewGuid());

    public HotCacheWorkerTests()
    {
        Directory.CreateDirectory(ColdRoot);
        Directory.CreateDirectory(HotRoot);
    }

    private string ColdRoot => Path.Combine(_root, "cold");

    private string HotRoot => Path.Combine(_root, "hot");

    [Fact]
    public void NestedMountSelectionUsesLongestContainingRoot()
    {
        var root = PhysicalFileOperations.SelectMountRoot("/mnt/ceph/hot-cache/file.mkv", ["/", "/mnt", "/mnt/ceph"]);
        Assert.Equal("/mnt/ceph", root);
    }

    [Fact]
    public async Task ProcessDeathMidCopyFailsWithoutPartial()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        await CreateWorker(store, new TestFiles { ThrowDuringCopy = true }).ExecuteOnceAsync("one", default);
        Assert.Equal(1, store.Failures);
        Assert.Empty(Directory.EnumerateFiles(HotRoot, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExpiredLeaseAllowsReplacementWorker()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc")) { LeaseExpired = true };
        Assert.NotNull(await store.ClaimAsync("one", TimeSpan.FromMinutes(1), default));
        Assert.NotNull(await store.ClaimAsync("two", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task LiveLeaseExcludesDuplicateWorker()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        Assert.NotNull(await store.ClaimAsync("one", TimeSpan.FromMinutes(1), default));
        Assert.Null(await store.ClaimAsync("two", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task FullFilesystemDegradesToColdPlayback()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        await CreateWorker(store, new TestFiles { ThrowDuringCopy = true }).ExecuteOnceAsync("one", default);
        Assert.Equal(1, store.Failures);
        Assert.False(File.Exists(Path.Combine(HotRoot, "video.bin")));
    }

    [Fact]
    public async Task SourceMutationDoesNotPublish()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        var files = new TestFiles { MutateSource = Path.Combine(ColdRoot, "video.bin") };
        await CreateWorker(store, files).ExecuteOnceAsync("one", default);
        Assert.Equal(1, store.Failures);
        Assert.False(File.Exists(Path.Combine(HotRoot, "video.bin")));
    }

    [Fact]
    public async Task PublishedPromotionPersistsHotPathForCapacityEviction()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);
        Assert.Equal(Path.Combine(HotRoot, "video.bin"), store.CompletedHotPath);
    }

    [Fact]
    public async Task StaleHotCopyIsRepairedBeforePromotionCompletes()
    {
        var job = CreatePromotion("video.bin", "fresh-content");
        var target = Path.Combine(HotRoot, "video.bin");
        await File.WriteAllTextAsync(target, "stale");
        var files = new TestFiles();
        var store = new TestStore(job);

        await CreateWorker(store, files).ExecuteOnceAsync("one", default);

        Assert.Equal("fresh-content", await File.ReadAllTextAsync(target));
        Assert.Equal(new FileInfo(job.CanonicalPath).LastWriteTimeUtc, new FileInfo(target).LastWriteTimeUtc);
        Assert.Equal(1, files.Copies);
    }

    [Fact]
    public async Task ValidHotCopyIsIdempotentlyReused()
    {
        var job = CreatePromotion("video.bin", "already-hot");
        var target = Path.Combine(HotRoot, "video.bin");
        await File.WriteAllTextAsync(target, "already-hot");
        File.SetLastWriteTimeUtc(target, new FileInfo(job.CanonicalPath).LastWriteTimeUtc);
        var files = new TestFiles();
        var store = new TestStore(job);

        await CreateWorker(store, files).ExecuteOnceAsync("one", default);

        Assert.Equal(0, files.Copies);
        Assert.Equal(target, store.CompletedHotPath);
    }

    [Fact]
    public async Task FailedStaleCopyRepairRetainsExistingHotCopy()
    {
        var job = CreatePromotion("video.bin", "fresh-content");
        var target = Path.Combine(HotRoot, "video.bin");
        await File.WriteAllTextAsync(target, "stale");
        var store = new TestStore(job);

        await CreateWorker(store, new TestFiles { ThrowDuringCopy = true }).ExecuteOnceAsync("one", default);

        Assert.Equal("stale", await File.ReadAllTextAsync(target));
        Assert.Equal(1, store.Failures);
    }

    [Fact]
    public async Task MountLossDegradesToColdPlayback()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"));
        await CreateWorker(store, new TestFiles { ThrowOnInfo = true }).ExecuteOnceAsync("one", default);
        Assert.Equal(1, store.Failures);
    }

    [Fact]
    public async Task LongCopyRenewsLease()
    {
        var store = new TestStore(CreatePromotion("video.bin", new string('x', 2_000_000)));
        await CreateWorker(store, new TestFiles(), TimeSpan.FromTicks(1)).ExecuteOnceAsync("one", default);
        Assert.True(store.Renewals > 1);
    }

    [Fact]
    public async Task EvictionIsIdempotent()
    {
        var path = Path.Combine(HotRoot, "old.bin");
        await File.WriteAllTextAsync(path, "x");
        var store = new TestStore(CreateEviction(path));
        var worker = CreateWorker(store, new TestFiles());
        await worker.ExecuteOnceAsync("one", default);
        await worker.ExecuteOnceAsync("two", default);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ProtectionAddedAfterClaimDefersEvictionAndRetainsCopy()
    {
        var path = Path.Combine(HotRoot, "protected.bin");
        await File.WriteAllTextAsync(path, "cache");
        var store = new TestStore(CreateEviction(path)) { CanEvict = false };

        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);

        Assert.True(File.Exists(path));
        Assert.Equal(1, store.DeferredEvictions);
        Assert.Null(store.CompletedHotPath);
    }

    [Fact]
    public async Task CapacityEvictionUsesLruAndCleansPartials()
    {
        var path = Path.Combine(HotRoot, "old.bin");
        var partial = Path.Combine(HotRoot, ".dead.partial");
        await File.WriteAllTextAsync(path, "x");
        await File.WriteAllTextAsync(partial, "x");
        File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddHours(-2));
        var store = new TestStore(null) { Candidates = [CreateEviction(path)] };
        await CreateWorker(store, new TestFiles { Available = 0, Total = 100 }).ExecuteOnceAsync("one", default);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task ConfiguredHotRootSymlinkDoesNotDeleteOutsidePartial()
    {
        var outside = Path.Combine(_root, "outside-hot");
        Directory.CreateDirectory(outside);
        var partial = Path.Combine(outside, ".old.partial");
        await File.WriteAllTextAsync(partial, "outside");
        File.SetLastWriteTimeUtc(partial, DateTime.UtcNow.AddHours(-2));
        Directory.Delete(HotRoot);
        Directory.CreateSymbolicLink(HotRoot, outside);

        var store = new TestStore(CreatePromotion("episode.mkv", "canonical"));
        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);

        Assert.True(File.Exists(partial));
        Assert.False(File.Exists(Path.Combine(outside, "episode.mkv")));
        Assert.Equal(1, store.Failures);
    }

    [Fact]
    public async Task ConfiguredCanonicalRootSymlinkFailsPromotionWithoutOutsideCopy()
    {
        var outside = Path.Combine(_root, "outside-media");
        Directory.CreateDirectory(outside);
        Directory.Delete(ColdRoot);
        Directory.CreateSymbolicLink(ColdRoot, outside);
        var logicalPath = Path.Combine(ColdRoot, "episode.mkv");
        var outsidePath = Path.Combine(outside, "episode.mkv");
        await File.WriteAllTextAsync(outsidePath, "outside");
        var info = new FileInfo(outsidePath);
        var store = new TestStore(new HotCacheJob(Guid.NewGuid(), HotCacheJobKind.Promotion, logicalPath, null, info.Length, info.LastWriteTimeUtc, 0, false, false, false, DateTime.UtcNow, 0));

        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);

        Assert.Equal(1, store.Failures);
        Assert.False(File.Exists(Path.Combine(HotRoot, "episode.mkv")));
    }

    [Fact]
    public async Task PausedSettingsDoNotClaimOrEvictWork()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"))
        {
            Settings = new HotCacheWorkerSettings("unraid-temp", true, .5, .1),
        };
        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);
        Assert.Equal(0, store.Claims);
    }

    [Fact]
    public async Task ObserveOnlyWorkerRecordsBackendWithoutClaimingOrEvicting()
    {
        var path = Path.Combine(HotRoot, "protected.bin");
        await File.WriteAllTextAsync(path, "cache");
        var store = new TestStore(CreatePromotion("video.bin", "abc"))
        {
            Candidates = [CreateEviction(path)],
        };

        await CreateWorker(store, new TestFiles { Available = 0, Total = 100 }, observeOnly: true).ExecuteOnceAsync("one", default);

        Assert.Equal(0, store.Claims);
        Assert.Single(store.Candidates);
        Assert.True(File.Exists(path));
        Assert.Equal(("unraid-temp", true, true, 100L, 0L), store.BackendObservation);
    }

    [Fact]
    public async Task FormerBackendDrainsByStoppingNewClaims()
    {
        var store = new TestStore(CreatePromotion("video.bin", "abc"))
        {
            Settings = new HotCacheWorkerSettings("cephfs", false, .5, .1),
        };
        await CreateWorker(store, new TestFiles()).ExecuteOnceAsync("one", default);
        Assert.Equal(0, store.Claims);
    }

    [Fact]
    public async Task WorkerRecordsBackendObservationInSharedStore()
    {
        var store = new TestStore(null);
        await CreateWorker(store, new TestFiles { Available = 40, Total = 100 }).ExecuteOnceAsync("one", default);
        Assert.Equal(("unraid-temp", true, true, 100L, 40L), store.BackendObservation);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private HotCacheWorker CreateWorker(TestStore store, TestFiles files, TimeSpan? leaseDuration = null, bool observeOnly = false)
        => new(
            store,
            files,
            new HotCacheOptions
            {
                CanonicalRoot = ColdRoot,
                HotRoot = HotRoot,
                Backend = "unraid-temp",
                HighWatermark = .5,
                LowWatermark = .1,
                PartialFileMaxAge = TimeSpan.FromHours(1),
                LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2),
                ObserveOnly = observeOnly,
            },
            NullLogger<HotCacheWorker>.Instance);

    private HotCacheJob CreatePromotion(string name, string content)
    {
        var path = Path.Combine(ColdRoot, name);
        File.WriteAllText(path, content);
        var info = new FileInfo(path);
        return new HotCacheJob(Guid.NewGuid(), HotCacheJobKind.Promotion, path, null, info.Length, info.LastWriteTimeUtc, 0, false, false, false, DateTime.UtcNow, 0);
    }

    private static HotCacheJob CreateEviction(string path)
        => new(Guid.NewGuid(), HotCacheJobKind.Eviction, string.Empty, path, 0, DateTime.UtcNow, 0, false, false, false, DateTime.UtcNow.AddDays(-1), 0);

    private sealed class TestStore(HotCacheJob? next) : IHotCacheJobStore
    {
        private HotCacheJob? _next = next;

        public List<HotCacheJob> Candidates { get; set; } = [];

        public int Failures { get; private set; }

        public int Claims { get; private set; }

        public bool CanEvict { get; init; } = true;

        public int DeferredEvictions { get; private set; }

        public HotCacheWorkerSettings Settings { get; init; } = new("unraid-temp", false, .5, .1);

        public (string Backend, bool Mounted, bool Healthy, long Total, long Available)? BackendObservation { get; private set; }

        public bool LeaseExpired { get; init; }

        public int Renewals { get; private set; }

        public string? CompletedHotPath { get; private set; }

        public Task<HotCacheJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Claims++;
            if (_next is null || (!LeaseExpired && _next.Attempts > 0))
            {
                return Task.FromResult<HotCacheJob?>(null);
            }

            _next = _next with { Attempts = _next.Attempts + 1 };
            return Task.FromResult<HotCacheJob?>(_next);
        }

        public Task<bool> RenewAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Renewals++;
            return Task.FromResult(true);
        }

        public Task<bool> ProgressAsync(Guid jobId, string workerId, long bytes, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> CompleteAsync(Guid jobId, string workerId, string? hotPath, string backend, CancellationToken cancellationToken)
        {
            CompletedHotPath = hotPath;
            _next = null;
            return Task.FromResult(true);
        }

        public Task<bool> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken)
        {
            Failures++;
            _next = null;
            return Task.FromResult(true);
        }

        public Task<HotCacheJob?> ClaimEvictionAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            var candidate = Candidates.FirstOrDefault();
            if (candidate is not null)
            {
                Candidates.Remove(candidate);
            }

            return Task.FromResult(candidate);
        }

        public Task<bool> TryEvictAsync(Guid jobId, string workerId, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
        {
            if (!CanEvict)
            {
                return Task.FromResult(false);
            }

            return DeleteAsync(delete, cancellationToken);
        }

        private static async Task<bool> DeleteAsync(Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
        {
            await delete(cancellationToken);
            return true;
        }

        public Task<bool> DeferEvictionAsync(Guid jobId, string workerId, CancellationToken cancellationToken)
        {
            DeferredEvictions++;
            return Task.FromResult(true);
        }

        public Task<HotCacheQueueSnapshot> SnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HotCacheQueueSnapshot(_next is null ? 0 : 1, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0, 0));

        public Task<HotCacheWorkerSettings> GetSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

        public Task ObserveBackendAsync(string backend, bool mounted, bool healthy, long totalBytes, long availableBytes, CancellationToken cancellationToken)
        {
            BackendObservation = (backend, mounted, healthy, totalBytes, availableBytes);
            return Task.CompletedTask;
        }

        public Task EventAsync(Guid jobId, string kind, string detail, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestFiles : IFileOperations
    {
        private readonly PhysicalFileOperations _inner = new();

        public long Available { get; init; } = 100;

        public string? MutateSource { get; init; }

        public bool ThrowDuringCopy { get; init; }

        public bool ThrowOnInfo { get; init; }

        public int Copies { get; private set; }

        public long Total { get; init; } = 100;

        public long GetAvailableSpace(string path) => Available;

        public long GetTotalSpace(string path) => Total;

        public bool FileExists(string path) => _inner.FileExists(path);

        public FileInfo GetFileInfo(string path)
        {
            if (ThrowOnInfo)
            {
                throw new IOException("mount lost");
            }

            return _inner.GetFileInfo(path);
        }

        public IEnumerable<string> EnumerateFiles(string root, string pattern) => _inner.EnumerateFiles(root, pattern);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public async Task CopyAsync(string source, string destination, Func<long, CancellationToken, Task> progress, CancellationToken cancellationToken)
        {
            if (ThrowDuringCopy)
            {
                throw new IOException("full filesystem");
            }

            Copies++;
            await _inner.CopyAsync(source, destination, progress, cancellationToken);
            if (MutateSource is not null)
            {
                await File.AppendAllTextAsync(MutateSource, "!", cancellationToken);
            }
        }

        public void SetLastWriteTimeUtc(string path, DateTime value) => _inner.SetLastWriteTimeUtc(path, value);

        public void MoveNoReplace(string source, string destination) => _inner.MoveNoReplace(source, destination);

        public void MoveReplace(string source, string destination) => _inner.MoveReplace(source, destination);

        public void Delete(string path) => _inner.Delete(path);
    }
}
