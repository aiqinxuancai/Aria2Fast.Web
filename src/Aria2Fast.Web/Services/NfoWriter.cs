using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Writes scraped metadata beside the videos in the Kodi NFO format, which Jellyfin, Emby and Kodi read: tvshow.nfo / season.nfo /
/// one NFO per episode, movie.nfo, and poster / fanart / seasonNN-poster images. Files we wrote carry a marker comment; NFO files
/// without it (written by hand or another tool), and the images next to them, are never replaced.
/// </summary>
public sealed class NfoWriter(StateStore store, LibraryStore library, ILogger<NfoWriter> logger)
{
    public const string Marker = "Written by Aria2Fast.Web";
    private static readonly string[] ImageExtensions = [".jpg", ".png", ".webp", ".gif"];
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, byte> requested = new();

    /// <summary>A file to write: NFO text, or an image copied from the poster cache. Owner is the NFO that decides whether existing files may be replaced.</summary>
    public sealed record Output(string Path, string? Text, string? Source, string Owner);

    /// <summary>Writes one work soon, in the background (after its match changed).</summary>
    public void Request(string id)
    {
        if (!store.Read().Settings.Library.WriteNfo) return;
        requested[id] = 0;
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try { foreach (var key in requested.Keys) if (requested.TryRemove(key, out _)) Write(key); }
            catch (Exception ex) { logger.LogWarning(ex, "写入 NFO 失败"); }
            finally { gate.Release(); }
        });
    }

    /// <summary>Matched works whose metadata or episodes changed since their files were last written.</summary>
    public async Task Sync(CancellationToken ct)
    {
        if (!store.Read().Settings.Library.WriteNfo) return;
        var due = library.Read().Series.Where(s => s is { Status: LibraryMetadataStatus.Matched, Metadata: not null }
            && (s.NfoWrittenAt is not { } written || written < s.Metadata!.UpdatedAt || written < s.UpdatedAt)).Select(s => s.Id).ToList();
        await gate.WaitAsync(ct);
        try { foreach (var id in due) { ct.ThrowIfCancellationRequested(); Write(id); } }
        finally { gate.Release(); }
    }

    public void Write(string id)
    {
        var settings = store.Read().Settings;
        var state = library.Read();
        if (state.Series.FirstOrDefault(s => s.Id == id) is not { Status: LibraryMetadataStatus.Matched, Metadata: not null } series) return;
        var folders = LibraryFolders.Resolve(settings.Library, settings.DownloadDirectory);
        var errors = new List<string>();
        var written = 0;
        foreach (var output in Plan(series, state, folders, settings.DownloadDirectory, library.PosterDirectory))
        {
            try { if (Apply(output)) written++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { errors.Add(output.Path + "：" + ex.Message); }
        }
        if (errors.Count > 0 && series.NfoWrittenAt == null) store.Log("无法写入《" + LibraryService.DisplayTitle(series) + "》的 NFO 或图片（目录可能是只读的）", "warning", string.Join("\n", errors.Take(5)));
        var now = DateTimeOffset.UtcNow;
        library.Update(l => { if (l.Series.FirstOrDefault(s => s.Id == id) is { } s) s.NfoWrittenAt = now; });
        if (written > 0) logger.LogInformation("已写入 {Count} 个 NFO / 图片文件：{Title}", written, series.Title);
    }

    /// <summary>Writes when the target is missing, or when its owner NFO is ours; an unchanged file is left alone.</summary>
    private static bool Apply(Output output)
    {
        if (File.Exists(output.Path) && !Ours(output.Owner)) return false;
        if (output.Text != null)
        {
            if (File.Exists(output.Path) && File.ReadAllText(output.Path) == output.Text) return false;
            File.WriteAllText(output.Path + ".tmp", output.Text, new System.Text.UTF8Encoding(false));
        }
        else
        {
            if (File.Exists(output.Path) && new FileInfo(output.Path).Length == new FileInfo(output.Source!).Length) return false;
            File.Copy(output.Source!, output.Path + ".tmp", true);
            // The same image cached in another format replaces ours (poster.jpg → poster.png).
            var stem = Path.Combine(Path.GetDirectoryName(output.Path)!, Path.GetFileNameWithoutExtension(output.Path));
            if (Ours(output.Owner)) foreach (var other in ImageExtensions.Select(e => stem + e).Where(p => p != output.Path && File.Exists(p))) File.Delete(other);
        }
        AtomicFile.Replace(output.Path + ".tmp", output.Path);
        return true;
    }

    private static bool Ours(string nfo)
    {
        try { return File.Exists(nfo) && File.ReadLines(nfo).Take(3).Any(line => line.Contains(Marker, StringComparison.Ordinal)); }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// The files for one work. Anime and series: tvshow.nfo, poster and fanart in the work's folder (written by its lowest season when
    /// several seasons share it), seasonNN-poster there too, season.nfo in season subfolders, and an NFO beside every numbered episode.
    /// Movies: movie.nfo, poster and fanart in the movie's folder, or "{video}.nfo / -poster / -fanart" for a loose file.
    /// </summary>
    public static List<Output> Plan(LibrarySeries series, LibraryState state, IReadOnlyList<LibraryFolders.Folder> folders, string downloadRoot, string cache)
    {
        var meta = series.Metadata!;
        var outputs = new List<Output>();
        var episodes = state.Episodes.Where(e => e.SeriesId == series.Id).ToList();
        string? Video(LibraryEpisode e) { try { return LibraryFolders.Locate(e.Path, folders, downloadRoot); } catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return null; } }
        string? Cached(string file) => file.Length > 0 && File.Exists(Path.Combine(cache, file)) ? Path.Combine(cache, file) : null;
        var poster = Cached(meta.PosterFile);
        var fanart = Cached(meta.FanartFile);
        void Images(string folder, string prefix, string owner)
        {
            if (poster != null) outputs.Add(new(Path.Combine(folder, prefix + "poster" + Path.GetExtension(poster)), null, poster, owner));
            if (fanart != null) outputs.Add(new(Path.Combine(folder, prefix + "fanart" + Path.GetExtension(fanart)), null, fanart, owner));
        }
        var folder = folders.FirstOrDefault(f => f.Id == series.FolderId) ?? folders.FirstOrDefault(f => f.Prefix.Length == 0);
        var loose = folder == null || series.Directory == folder.Prefix || series.Directory.Length == 0;
        var directory = loose ? null : LibraryFolders.FullDirectory(series.Directory, folders, downloadRoot);

        if (series.Category == LibraryCategory.Movie)
        {
            if (directory != null)
            {
                var nfo = Path.Combine(directory, "movie.nfo");
                outputs.Add(new(nfo, Document(Movie(series, meta)), null, nfo));
                Images(directory, "", nfo);
            }
            else foreach (var video in episodes.Where(e => e.Kind != LibraryEpisodeKind.Extra).Select(Video).OfType<string>())
            {
                var stem = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video));
                outputs.Add(new(stem + ".nfo", Document(Movie(series, meta)), null, stem + ".nfo"));
                Images(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video) + "-", stem + ".nfo");
            }
            return outputs;
        }

        var season = series.Season > 0 ? series.Season : 1;
        if (directory != null)
        {
            var show = Path.Combine(directory, "tvshow.nfo");
            var primary = !state.Series.Any(s => s.Id != series.Id && s.Directory == series.Directory && s.FolderId == series.FolderId && s.Metadata != null && s.Season < series.Season);
            if (primary)
            {
                outputs.Add(new(show, Document(Show(series, meta)), null, show));
                Images(directory, "", show);
            }
            if (poster != null) outputs.Add(new(Path.Combine(directory, (series.Season == 0 ? "season-specials" : $"season{season:00}") + "-poster" + Path.GetExtension(poster)), null, poster, show));
            // Season subfolders (e.g. "Season 2") get their own season.nfo.
            foreach (var sub in episodes.Select(Video).OfType<string>().Select(Path.GetDirectoryName).OfType<string>().Distinct(LibraryFolders.PathComparer)
                         .Where(d => !LibraryFolders.PathComparer.Equals(d, directory) && d.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                     && ReleaseNames.SeasonFolder(Path.GetFileName(d)) != null))
                outputs.Add(new(Path.Combine(sub, "season.nfo"), Document(Season(meta, ReleaseNames.SeasonFolder(Path.GetFileName(sub)) ?? season)), null, Path.Combine(sub, "season.nfo")));
        }
        foreach (var episode in episodes.Where(e => e.Kind != LibraryEpisodeKind.Extra && e.Number is { } n && n == Math.Floor(n)))
        {
            if (Video(episode) is not { } video) continue;
            var nfo = Path.ChangeExtension(video, ".nfo");
            outputs.Add(new(nfo, Document(Episode(series, meta, episode, episode.Kind == LibraryEpisodeKind.Special ? 0 : season)), null, nfo));
        }
        return outputs;
    }

    private static string Document(XElement root) =>
        new XDeclaration("1.0", "utf-8", "yes") + Environment.NewLine + "<!-- " + Marker + "; delete this line to keep your own edits -->" + Environment.NewLine + root;

    private static IEnumerable<XElement> Common(LibrarySeries series, LibraryMetadata meta)
    {
        var title = LibraryService.DisplayTitle(series);
        yield return new("title", title);
        if (meta.Name.Length > 0 && meta.Name != title) yield return new("originaltitle", meta.Name);
        if (meta.Summary.Length > 0) yield return new("plot", meta.Summary);
        if (meta.AirDate.Length >= 4)
        {
            if (meta.AirDate.Length == 10) yield return new("premiered", meta.AirDate);
            yield return new("year", meta.AirDate[..4]);
        }
        else if (series.Year is { } year) yield return new("year", year);
        if (meta.Score is { } score) yield return new("rating", score.ToString("0.0", CultureInfo.InvariantCulture));
        foreach (var genre in meta.Genres) yield return new("genre", genre);
        if (meta.Studio.Length > 0) yield return new("studio", meta.Studio);
        switch (meta.Source)
        {
            case "bangumi":
                yield return new("uniqueid", new XAttribute("type", "bangumi"), new XAttribute("default", "true"), meta.SourceId);
                yield return new("bangumiid", meta.SourceId);
                break;
            case "tmdb" or "tmdb-movie":
                yield return new("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), meta.SourceId);
                yield return new("tmdbid", meta.SourceId);
                break;
        }
    }

    public static XElement Show(LibrarySeries series, LibraryMetadata meta) => new("tvshow", Common(series, meta));
    public static XElement Movie(LibrarySeries series, LibraryMetadata meta) => new("movie", Common(series, meta));

    public static XElement Season(LibraryMetadata meta, int number) => new("season",
        new XElement("title", number == 0 ? "特别篇" : meta.NameCn.Length > 0 ? meta.NameCn : $"第 {number} 季"), new XElement("seasonnumber", number),
        meta.Summary.Length > 0 ? new XElement("plot", meta.Summary) : null);

    public static XElement Episode(LibrarySeries series, LibraryMetadata meta, LibraryEpisode episode, int season)
    {
        var info = LibraryService.EpisodeInfo(meta, episode);
        var number = (int)episode.Number!.Value;
        var title = info == null ? "" : info.NameCn.Length > 0 ? info.NameCn : info.Name;
        return new("episodedetails",
            new XElement("title", title.Length > 0 ? title : episode.Kind == LibraryEpisodeKind.Special ? $"特别篇 {number}" : $"第 {number} 集"),
            new XElement("showtitle", LibraryService.DisplayTitle(series)), new XElement("season", season), new XElement("episode", number),
            info is { AirDate.Length: 10 } ? new XElement("aired", info.AirDate) : null,
            info is { Summary.Length: > 0 } ? new XElement("plot", info.Summary) : null);
    }
}
