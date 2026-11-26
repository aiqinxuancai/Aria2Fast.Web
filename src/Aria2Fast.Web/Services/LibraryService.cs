using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Keeps the media library in step with the local download directory: scans on start, on a timer and after
/// local downloads finish, then fetches metadata and posters for new works one at a time.
/// </summary>
public sealed class LibraryService(StateStore store, LibraryStore library, HttpGateway http, MikanService mikan, BangumiClient bangumi, TmdbClient tmdb, ILogger<LibraryService> logger) : BackgroundService
{
    private const int PosterLimit = 10 * 1024 * 1024;
    private readonly SemaphoreSlim scanGate = new(1);
    private readonly SemaphoreSlim scrapeGate = new(1);
    private readonly SemaphoreSlim wake = new(0);
    private readonly ConcurrentDictionary<string, byte> completed = new();
    public bool Scanning { get; private set; }

    public void RequestScan() { if (wake.CurrentCount == 0) wake.Release(); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store.Read().Settings.Library.Enabled) { await Scan(stoppingToken); await ScrapeDue(stoppingToken); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "媒体库更新失败"); }
            try
            {
                var interval = TimeSpan.FromMinutes(Math.Clamp(store.Read().Settings.Library.ScanIntervalMinutes, 5, 1440));
                // Batch torrents finish many files together; wait briefly so they are indexed in one scan.
                if (await wake.WaitAsync(interval, stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                while (wake.Wait(0)) { }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<object> Scan(CancellationToken ct)
    {
        if (!store.Read().Settings.Library.Enabled) throw new ArgumentException("媒体库已关闭，请先在设置中启用");
        LibraryState next;
        await scanGate.WaitAsync(ct);
        Scanning = true;
        try
        {
            var state = store.Read();
            var root = Path.GetFullPath(state.Settings.DownloadDirectory);
            var claims = Claims(state, root);
            var subscriptions = state.Subscriptions.Where(s => s.NodeId == "local").ToDictionary(s => s.Id);
            var (files, truncated) = await Task.Run(() => (LibraryScanner.Walk(root, out var cut), cut), ct);
            var now = DateTimeOffset.UtcNow;
            // Merge under the store lock so metadata fetched during the walk is not overwritten.
            next = library.Replace(current => LibraryScanner.Build(files, claims, subscriptions, current, now, truncated));
            RemoveStalePosters(next);
        }
        finally { Scanning = false; scanGate.Release(); }
        return Status(next);
    }

    /// <summary>
    /// Work folders of local subscriptions, relative to the root. A folder shared by several subscriptions
    /// without a work name (e.g. a manual "Anime" folder) holds many works, so it is not claimed.
    /// </summary>
    public static List<LibraryScanner.Claim> Claims(AppState state, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var claims = new List<LibraryScanner.Claim>();
        foreach (var subscription in state.Subscriptions.Where(s => s.NodeId == "local"))
        {
            try
            {
                var parent = string.IsNullOrWhiteSpace(subscription.Directory) ? root : subscription.Directory;
                var relative = Path.GetRelativePath(root, Path.GetFullPath(DownloadPaths.Compose(parent, subscription.NamePath, 0))).Replace('\\', '/').TrimEnd('/');
                if (relative is "." or "" or ".." || relative.StartsWith("../") || Path.IsPathRooted(relative)) continue;
                claims.Add(new(relative, subscription));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return claims.Where(c => !string.IsNullOrWhiteSpace(c.Subscription.NamePath) || claims.Count(o => string.Equals(o.Directory, c.Directory, comparison)) == 1).ToList();
    }

    /// <summary>A local download finished: remember which subscription produced its videos, then rescan.</summary>
    public void Completed(string gid, JsonNode task)
    {
        if (!completed.TryAdd(gid, 0)) return;
        if (completed.Count > 10000) completed.Clear();
        try
        {
            var state = store.Read();
            if (!state.Settings.Library.Enabled) return;
            RecordOrigins(state, gid, task);
            RequestScan();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "媒体库来源记录失败 {Gid}", gid); }
    }

    private void RecordOrigins(AppState state, string gid, JsonNode task)
    {
        // Magnets and .torrent links finish as a follow-up task; the subscription history holds the parent GID.
        var parent = task["following"]?.ToString();
        var subscription = state.Subscriptions.FirstOrDefault(s => s.NodeId == "local" && s.History.Any(h => h.Gid != null && (h.Gid == gid || h.Gid == parent)));
        if (subscription == null) return;
        var root = Path.GetFullPath(state.Settings.DownloadDirectory);
        var paths = (task["files"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => f["selected"]?.ToString() != "false").Select(f => f["path"]?.ToString() ?? "")
            .Where(p => p.Length > 0 && ReleaseNames.IsVideo(p))
            .Select(p => Path.GetRelativePath(root, Path.GetFullPath(p)).Replace('\\', '/'))
            .Where(p => p != ".." && !p.StartsWith("../") && !Path.IsPathRooted(p)).ToList();
        if (paths.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        library.Update(l => { foreach (var path in paths) l.Origins[path] = new(subscription.Id, gid, now); });
    }

    private async Task ScrapeDue(CancellationToken ct)
    {
        if (!store.Read().Settings.Library.FetchMetadata) return;
        var now = DateTimeOffset.UtcNow;
        var due = library.Read().Series
            .Where(s => s.Status == LibraryMetadataStatus.Pending || (s.Status is LibraryMetadataStatus.Failed or LibraryMetadataStatus.Unmatched && s.NextAttempt <= now))
            .Select(s => s.Id).ToList();
        foreach (var id in due)
        {
            if (!store.Read().Settings.Library.FetchMetadata) return;
            try { await Scrape(id, ct); }
            catch (FileNotFoundException) { } // Removed by a rescan meanwhile.
            await Task.Delay(TimeSpan.FromSeconds(1), ct); // Keep request rates to the public APIs modest.
        }
    }

    /// <summary>Identity order: manual or subscription ID, then the bgm.tv link on the Mikan page, then a Bangumi search, then TMDB.</summary>
    public async Task Scrape(string id, CancellationToken ct)
    {
        await scrapeGate.WaitAsync(ct);
        try
        {
            var series = library.Read().Series.FirstOrDefault(s => s.Id == id) ?? throw new FileNotFoundException("作品不存在或已被移除");
            LibraryMatchSource? source = series.ManualMatch ? LibraryMatchSource.Manual : series.BgmId != null || series.TmdbId != null ? LibraryMatchSource.Subscription : null;
            LibraryMetadata? metadata = null;
            string? error = null;
            try
            {
                var bgmId = series.BgmId;
                var known = bgmId != null || series.TmdbId != null;
                if (!known && series.MikanId != null && (bgmId = await mikan.BangumiTvId(series.MikanId, ct)) != null) source = LibraryMatchSource.Mikan;
                var terms = ReleaseNames.SearchTerms(series.Title);
                if (!known && bgmId == null && (bgmId = await bangumi.Find(terms, series.Season, ct)) != null) source = LibraryMatchSource.Search;
                if (bgmId is int subject) metadata = await bangumi.Subject(subject, ct);
                else if (series.TmdbId is int tv) metadata = await tmdb.Tv(tv, ct);
                else if (tmdb.Configured && (metadata = await tmdb.Search(terms, ct)) != null) source = LibraryMatchSource.Search;
                if (metadata != null) metadata = await Poster(series.Id, metadata, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { error = ex.Message; }
            Save(series, metadata, source, error);
        }
        finally { scrapeGate.Release(); }
    }

    private void Save(LibrarySeries series, LibraryMetadata? metadata, LibraryMatchSource? source, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        library.Update(l =>
        {
            var current = l.Series.FirstOrDefault(s => s.Id == series.Id);
            // A rescan or manual match may have changed the identity while the request was in flight.
            if (current == null || current.BgmId != series.BgmId || current.TmdbId != series.TmdbId || current.MikanId != series.MikanId || current.ManualMatch != series.ManualMatch) return;
            if (metadata != null)
            {
                current.Metadata = metadata; current.MatchedBy = source; current.Status = LibraryMetadataStatus.Matched;
                current.Error = null; current.Attempts = 0; current.NextAttempt = null;
                if (metadata.Source == "bangumi") current.BgmId = metadata.SourceId; else current.TmdbId = metadata.SourceId;
                return;
            }
            current.Attempts++;
            current.Status = error == null ? LibraryMetadataStatus.Unmatched : LibraryMetadataStatus.Failed;
            current.Error = error ?? "Bangumi" + (tmdb.Configured ? " 和 TMDB" : "") + " 中未找到匹配的条目";
            current.NextAttempt = now + (current.Attempts switch { 1 => TimeSpan.FromHours(1), 2 => TimeSpan.FromHours(6), _ => TimeSpan.FromDays(1) });
        });
        // Mikan links and manual choices are reliable; keep them on the subscription for later folders and views.
        if (metadata?.Source == "bangumi" && source is LibraryMatchSource.Mikan or LibraryMatchSource.Manual && series.SubscriptionId is { } subscriptionId
            && store.Read().Subscriptions.FirstOrDefault(s => s.Id == subscriptionId) is { } owner && owner.BgmId != metadata.SourceId)
            store.Update(s => { if (s.Subscriptions.FirstOrDefault(x => x.Id == subscriptionId) is { } sub) sub.BgmId = metadata.SourceId; });
    }

    /// <summary>Sets a Bangumi subject (or, without one, a TMDB TV ID) by hand; both null returns to automatic matching.</summary>
    public async Task<object> Match(string id, int? bgmId, int? tmdbId, CancellationToken ct)
    {
        if (bgmId is <= 0 || tmdbId is <= 0) throw new ArgumentException("Bangumi / TMDB 条目 ID 无效");
        if (bgmId == null && tmdbId != null && !tmdb.Configured) throw new ArgumentException("请先在设置中填写 TMDB API Key");
        var manual = bgmId != null || tmdbId != null;
        int? previous = null;
        string? subscriptionId = null;
        library.Update(l =>
        {
            var series = l.Series.FirstOrDefault(s => s.Id == id) ?? throw new FileNotFoundException("作品不存在或已被移除");
            previous = series.ManualMatch ? series.BgmId : null;
            subscriptionId = series.SubscriptionId;
            series.ManualMatch = manual;
            series.BgmId = bgmId;
            series.TmdbId = bgmId == null ? tmdbId : null;
            series.Metadata = null; series.MatchedBy = null; series.Status = LibraryMetadataStatus.Pending;
            series.Error = null; series.Attempts = 0; series.NextAttempt = null;
        });
        // Clearing a manual match also clears the copy kept on the subscription, so Mikan decides again.
        if (!manual && previous != null && subscriptionId != null)
            store.Update(s => { if (s.Subscriptions.FirstOrDefault(x => x.Id == subscriptionId) is { BgmId: var kept } sub && kept == previous) sub.BgmId = null; });
        await Scrape(id, ct);
        return Detail(id);
    }

    public async Task<object> Refresh(string id, CancellationToken ct)
    {
        await Scrape(id, ct);
        return Detail(id);
    }

    public object Status() => Status(library.Read());

    private object Status(LibraryState state)
    {
        var settings = store.Read().Settings.Library;
        int Count(LibraryMetadataStatus status) => state.Series.Count(s => s.Status == status);
        return new
        {
            settings.Enabled, settings.FetchMetadata, Scanning, state.LastScan, warning = state.LastScanWarning,
            series = state.Series.Count, episodes = state.Episodes.Count,
            matched = Count(LibraryMetadataStatus.Matched), pending = Count(LibraryMetadataStatus.Pending),
            unmatched = Count(LibraryMetadataStatus.Unmatched), failed = Count(LibraryMetadataStatus.Failed)
        };
    }

    public object Overview()
    {
        var state = library.Read();
        var episodes = state.Episodes.ToLookup(e => e.SeriesId);
        return new { status = Status(state), series = state.Series.Select(s => Summary(s, episodes[s.Id].ToList())) };
    }

    public object Detail(string id)
    {
        var state = library.Read();
        var series = state.Series.FirstOrDefault(s => s.Id == id) ?? throw new FileNotFoundException("作品不存在或已被移除");
        var episodes = state.Episodes.Where(e => e.SeriesId == id).ToList();
        var meta = series.Metadata;
        return new
        {
            series = Summary(series, episodes),
            metadata = meta == null ? null : new
            {
                meta.Source, meta.SourceId, meta.Name, meta.NameCn, meta.Summary, meta.AirDate, meta.Score, meta.TotalEpisodes, meta.Platform, meta.UpdatedAt,
                url = meta.Source == "bangumi" ? $"https://bgm.tv/subject/{meta.SourceId}" : $"https://www.themoviedb.org/tv/{meta.SourceId}"
            },
            episodes = episodes.Select(e =>
            {
                var info = EpisodeInfo(meta, e);
                return new { e.Id, e.Path, name = Path.GetFileName(e.Path), e.Size, e.Modified, e.Number, e.Kind, e.AddedAt, title = info == null ? "" : info.NameCn.Length > 0 ? info.NameCn : info.Name, airDate = info?.AirDate ?? "" };
            })
        };
    }

    private static object Summary(LibrarySeries s, List<LibraryEpisode> episodes)
    {
        var meta = s.Metadata;
        var main = episodes.Where(e => e.Kind == LibraryEpisodeKind.Episode).ToList();
        return new
        {
            s.Id, title = meta is null ? s.Title : meta.NameCn.Length > 0 ? meta.NameCn : meta.Name.Length > 0 ? meta.Name : s.Title,
            originalTitle = meta?.Name ?? "", folderTitle = s.Title, s.Directory, s.Season, s.SubscriptionId, s.MikanId, s.BgmId, s.TmdbId,
            s.ManualMatch, s.MatchedBy, s.Status, s.Error,
            poster = meta is { PosterFile.Length: > 0 } ? $"/api/library/{s.Id}/poster?v={meta.UpdatedAt.ToUnixTimeSeconds()}" : "",
            airDate = meta?.AirDate ?? "", score = meta?.Score, totalEpisodes = meta?.TotalEpisodes,
            episodeCount = main.Count, extraCount = episodes.Count - main.Count, latestEpisode = main.Max(e => e.Number), size = episodes.Sum(e => e.Size),
            s.AddedAt, s.UpdatedAt
        };
    }

    private static LibraryEpisodeMeta? EpisodeInfo(LibraryMetadata? meta, LibraryEpisode episode)
    {
        if (meta == null || episode.Number is not double number || episode.Kind == LibraryEpisodeKind.Extra) return null;
        var type = episode.Kind == LibraryEpisodeKind.Special ? 1 : 0;
        // Fansubs number later seasons either from 1 or continuing the previous season; try both.
        return meta.Episodes.FirstOrDefault(m => m.Type == type && m.Number == number) ?? meta.Episodes.FirstOrDefault(m => m.Type == type && m.Sort == number);
    }

    /// <summary>Poster files are named after the series ID, so serving one needs no index read.</summary>
    public string PosterPath(string id)
    {
        if (!Regex.IsMatch(id, "^[0-9a-f]{16}$")) throw new FileNotFoundException("海报不存在");
        return Directory.EnumerateFiles(library.PosterDirectory, id + ".*").FirstOrDefault(p => !p.EndsWith(".tmp", StringComparison.Ordinal))
            ?? throw new FileNotFoundException("海报不存在");
    }

    private async Task<LibraryMetadata> Poster(string id, LibraryMetadata metadata, CancellationToken ct)
    {
        if (metadata.PosterUrl.Length == 0) return metadata;
        try
        {
            var bytes = await http.GetBytesAsync(metadata.PosterUrl, PosterLimit, ct);
            var extension = ImageExtension(bytes) ?? throw new InvalidDataException("海报不是 JPEG、PNG、WebP 或 GIF 图片");
            var target = Path.Combine(library.PosterDirectory, id + extension);
            await File.WriteAllBytesAsync(target + ".tmp", bytes, ct);
            File.Move(target + ".tmp", target, true);
            foreach (var stale in Directory.EnumerateFiles(library.PosterDirectory, id + ".*").Where(p => p != target)) File.Delete(stale);
            return metadata with { PosterFile = Path.GetFileName(target) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Metadata is useful without artwork; refreshing the work retries the download.
            logger.LogInformation(ex, "海报下载失败 {Url}", metadata.PosterUrl);
            return metadata;
        }
    }

    private void RemoveStalePosters(LibraryState state)
    {
        var ids = state.Series.Select(s => s.Id).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(library.PosterDirectory))
        {
            var name = Path.GetFileName(file);
            if (!ids.Contains(name.Split('.')[0])) try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Raster formats only: SVG can carry script and is never stored.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => ".png",
        [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        _ => null
    };

    public static string ContentType(string path) => Path.GetExtension(path) switch
    {
        ".jpg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "application/octet-stream"
    };
}
