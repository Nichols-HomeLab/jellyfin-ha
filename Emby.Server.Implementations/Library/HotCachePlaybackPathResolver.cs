using System;
using System.Collections.Concurrent;
using System.IO;
using MediaBrowser.Controller.Library;
using Prometheus;

namespace Emby.Server.Implementations.Library;

/// <summary>Validates an atomically published hot file and otherwise fails open to canonical storage.</summary>
public sealed class HotCachePlaybackPathResolver : IPlaybackPathResolver
{
    private const int MaximumReportedPaths = 4096;
    private static readonly TimeSpan ObservationThrottleWindow = TimeSpan.FromMinutes(1);
    private static readonly Counter Resolutions = Metrics.CreateCounter("jellyfin_hot_cache_playback_resolutions_total", "Playback path resolutions by cache result and fallback health.", new CounterConfiguration { LabelNames = ["result", "fallback", "reason"] });
    private readonly string _canonicalRoot;
    private readonly string _hotRoot;
    private readonly IHotCacheCoordinator _coordinator;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _reported = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="HotCachePlaybackPathResolver"/> class.
    /// </summary>
    /// <param name="canonicalRoot">The canonical media root.</param>
    /// <param name="hotRoot">The disposable hot-cache root.</param>
    /// <param name="coordinator">The hot-cache coordinator.</param>
    public HotCachePlaybackPathResolver(string canonicalRoot, string hotRoot, IHotCacheCoordinator coordinator, TimeProvider? timeProvider = null)
    {
        _canonicalRoot = Path.GetFullPath(canonicalRoot);
        _hotRoot = Path.GetFullPath(hotRoot);
        _coordinator = coordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public PlaybackPathResolution Resolve(in PlaybackPathRequest request)
    {
        try
        {
            var canonical = Path.GetFullPath(request.CanonicalPath);
            if (!IsContained(_canonicalRoot, canonical) || HasLinkComponent(_canonicalRoot, canonical))
            {
                return Observe(request, Cold(request.CanonicalPath, "outside-canonical-root"));
            }

            var relative = Path.GetRelativePath(_canonicalRoot, canonical);
            var candidate = Path.GetFullPath(Path.Combine(_hotRoot, relative));
            if (!IsContained(_hotRoot, candidate) || HasLinkComponent(_hotRoot, candidate) || !File.Exists(candidate))
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-miss"));
            }

            var final = File.ResolveLinkTarget(candidate, true)?.FullName ?? candidate;
            if (!IsContained(_hotRoot, final))
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-root-escape"));
            }

            var canonicalInfo = new FileInfo(canonical);
            var hot = new FileInfo(final);
            if ((request.ExpectedLength.HasValue && hot.Length != request.ExpectedLength.Value)
                || hot.Length != canonicalInfo.Length)
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-length-mismatch"));
            }

            if (hot.LastWriteTimeUtc != canonicalInfo.LastWriteTimeUtc)
            {
                return Observe(request, Cold(request.CanonicalPath, "hot-mtime-mismatch"));
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
        Resolutions.WithLabels(resolution.IsHot ? "hot" : "cold", FallbackClass(resolution), resolution.Reason).Inc();
        // A resolver may be called per segment. Coalesce only the observation, never its correctness check.
        var key = request.CanonicalPath + '\u001f' + resolution.Reason;
        var now = _timeProvider.GetUtcNow();
        if (_reported.Count >= MaximumReportedPaths)
        {
            _reported.Clear();
        }

        if (!_reported.TryGetValue(key, out var previous) || now - previous >= ObservationThrottleWindow)
        {
            _reported[key] = now;
            _coordinator.ObserveResolution(request, resolution);
        }

        return resolution;
    }

    private static PlaybackPathResolution Cold(string path, string reason) => new(path, false, reason);

    // A missing disposable copy is normal. Validation, mount, or containment failures are actionable.
    private static string FallbackClass(in PlaybackPathResolution resolution)
        => resolution.IsHot ? "none" : resolution.Reason == "hot-miss" ? "normal" : "unhealthy";

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool HasLinkComponent(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(part) || part == ".")
            {
                continue;
            }

            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
