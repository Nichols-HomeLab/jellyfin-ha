using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.Library;

/// <summary>Validates an atomically published hot file and otherwise fails open to canonical storage.</summary>
public sealed class HotCachePlaybackPathResolver : IPlaybackPathResolver
{
    private readonly string _canonicalRoot;
    private readonly string _hotRoot;
    private readonly IHotCacheCoordinator _coordinator;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>Initializes a resolver with fixed server-local mount roots.</summary>
    public HotCachePlaybackPathResolver(string canonicalRoot, string hotRoot, IHotCacheCoordinator coordinator)
    {
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        _hotRoot = Path.GetFullPath(hotRoot);
        _coordinator = coordinator;
    }

    /// <inheritdoc />
    public PlaybackPathResolution Resolve(in PlaybackPathRequest request)
    {
        try
        {
            var canonical = Path.GetFullPath(request.CanonicalPath);
            if (!IsContained(_canonicalRoot, canonical))
            {
                return Observe(request, Cold(request.CanonicalPath, "outside-canonical-root"));
            }

            var relative = Path.GetRelativePath(_canonicalRoot, canonical);
            var candidate = Path.GetFullPath(Path.Combine(_hotRoot, relative));
            if (!IsContained(_hotRoot, candidate) || !File.Exists(candidate))
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-miss"));
            }

            var final = File.ResolveLinkTarget(candidate, true)?.FullName ?? candidate;
            if (!IsContained(_hotRoot, final))
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-root-escape"));
            }

            var hot = new FileInfo(final);
            if ((request.ExpectedLength.HasValue && hot.Length != request.ExpectedLength.Value)
                || hot.Length != new FileInfo(canonical).Length)
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-length-mismatch"));
            }

            using var stream = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length > 0 && stream.ReadByte() < 0)
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-unreadable"));
            }

            return Observe(request, new PlaybackPathResolution(candidate, true, "hot-hit"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Observe(request, Cold(request.CanonicalPath, "hot-validation-failed"));
        }
    }

    private PlaybackPathResolution Observe(in PlaybackPathRequest request, in PlaybackPathResolution resolution)
    {
        // A resolver may be called per segment. Coalesce only the observation, never its correctness check.
        if (_reported.TryAdd(request.CanonicalPath + '\u001f' + resolution.Reason, 0))
        {
            _coordinator.ObserveResolution(request, resolution);
        }

        return resolution;
    }

    private static PlaybackPathResolution Cold(string path, string reason) => new(path, false, reason);

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
