using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>Playing library videos: the player page, streaming, subtitles, progress and links for external players.</summary>
public sealed class PlaybackService(StateStore store, LibraryStore library, PlaybackStore playback, MediaLinks links)
{
    /// <summary>The episode and its file, which must still be inside the download directory.</summary>
    public (LibraryEpisode Episode, string File) Resolve(string id)
    {
        var episode = library.Query(s => s.Episodes.FirstOrDefault(e => e.Id == id)) ?? throw new FileNotFoundException("视频不存在或已被移除");
        return (episode, Locate(episode, Root()));
    }

    private string Root() => Path.GetFullPath(store.Read().Settings.DownloadDirectory);

    private static string Locate(LibraryEpisode episode, string root)
    {
        var file = Validation.LocalFile(root, Path.Combine(root, episode.Path));
        return File.Exists(file) ? file : throw new FileNotFoundException("视频文件不存在，可能已被移动或删除，请重新扫描媒体库");
    }

    public object Player(string id)
    {
        var (episode, file) = Resolve(id);
        var (series, list) = Series(episode.SeriesId);
        var progress = Progress(list);
        var index = list.FindIndex(e => e.Id == id);
        // Previous and next stay within the kind, so specials do not interrupt the main episodes.
        var previous = list.Take(index).LastOrDefault(e => e.Kind == episode.Kind);
        var next = list.Skip(index + 1).FirstOrDefault(e => e.Kind == episode.Kind);
        return new
        {
            episode = LibraryService.EpisodeItem(episode, series.Metadata, progress),
            series = new { series.Id, title = LibraryService.DisplayTitle(series), series.Season, poster = LibraryService.PosterUrl(series) },
            stream = $"/api/library/episodes/{id}/stream", type = ContentType(file),
            subtitles = Subtitles.Find(file, episode.Number).Select((t, i) => new { index = i, t.Label, t.Language, t.Format, url = $"/api/library/episodes/{id}/subtitles/{i}" }),
            previous = previous?.Id, next = next?.Id,
            episodes = list.Select(e => LibraryService.EpisodeItem(e, series.Metadata, progress))
        };
    }

    private (LibrarySeries Series, List<LibraryEpisode> Episodes) Series(string seriesId) => library.Query(s =>
        (s.Series.FirstOrDefault(x => x.Id == seriesId) ?? throw new FileNotFoundException("作品不存在或已被移除"),
         LibraryService.Ordered(s.Episodes.Where(e => e.SeriesId == seriesId)).ToList()));

    private Dictionary<string, PlaybackProgress> Progress(IEnumerable<LibraryEpisode> episodes) => playback.Query(p =>
        episodes.Where(e => p.Episodes.ContainsKey(e.Id)).ToDictionary(e => e.Id, e => p.Episodes[e.Id]));

    public string Subtitle(string id, int index)
    {
        var (episode, file) = Resolve(id);
        var tracks = Subtitles.Find(file, episode.Number);
        if (index < 0 || index >= tracks.Count) throw new FileNotFoundException("字幕不存在");
        var path = Validation.LocalFile(Root(), tracks[index].Path);
        if (new FileInfo(path).Length > Subtitles.SizeLimit) throw new ArgumentException("字幕文件超过 10 MB");
        return Subtitles.ToVtt(File.ReadAllBytes(path), Path.GetExtension(path));
    }

    /// <summary>
    /// Past 90 % (the credits) counts as watched. A watched episode stays watched when replayed; only marking it
    /// unwatched clears that.
    /// </summary>
    public PlaybackProgress Save(string id, double position, double duration, bool? watched)
    {
        if (!double.IsFinite(position) || !double.IsFinite(duration) || position < 0 || duration < 0 || duration > 7 * 86400) throw new ArgumentException("播放进度无效");
        if (!library.Query(s => s.Episodes.Any(e => e.Id == id))) throw new FileNotFoundException("视频不存在或已被移除");
        if (duration > 0) position = Math.Min(position, duration);
        PlaybackProgress? saved = null;
        playback.Update(p =>
        {
            var old = p.Episodes.GetValueOrDefault(id);
            var done = watched ?? (old?.Watched == true || (duration > 0 && position >= duration * 0.9));
            p.Episodes[id] = saved = new(position, duration > 0 ? duration : old?.Duration ?? 0, done, DateTimeOffset.UtcNow);
        });
        return saved!;
    }

    public void Mark(string id, bool watched)
    {
        if (!library.Query(s => s.Episodes.Any(e => e.Id == id))) throw new FileNotFoundException("视频不存在或已被移除");
        Mark([id], watched);
    }

    /// <summary>Marks a whole work, e.g. one already watched elsewhere. Extras (OP / ED / menus) are left alone.</summary>
    public int MarkSeries(string seriesId, bool watched)
    {
        var ids = Series(seriesId).Episodes.Where(e => e.Kind != LibraryEpisodeKind.Extra).Select(e => e.Id).ToList();
        Mark(ids, watched);
        return ids.Count;
    }

    private void Mark(IReadOnlyCollection<string> ids, bool watched)
    {
        var now = DateTimeOffset.UtcNow;
        playback.Update(p =>
        {
            // Unwatched episodes have no entry, so they do not count as recently played.
            foreach (var id in ids)
                if (watched) p.Episodes[id] = new(0, p.Episodes.GetValueOrDefault(id)?.Duration ?? 0, true, now);
                else p.Episodes.Remove(id);
        });
    }

    public object Link(string id)
    {
        var (episode, file) = Resolve(id);
        return new { url = MediaUrl(episode.Id, file), expiresAt = DateTimeOffset.UtcNow + MediaLinks.Lifetime };
    }

    /// <summary>Signed links for a whole work, for an M3U playlist in an external player.</summary>
    public object SeriesLinks(string seriesId)
    {
        var (series, list) = Series(seriesId);
        var root = Root();
        var items = new List<object>();
        foreach (var episode in list)
        {
            string file;
            try { file = Locate(episode, root); }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { continue; }
            var item = LibraryService.EpisodeInfo(series.Metadata, episode);
            items.Add(new { episodeId = episode.Id, episode.Number, episode.Kind, title = item == null ? "" : item.NameCn.Length > 0 ? item.NameCn : item.Name, name = Path.GetFileName(file), url = MediaUrl(episode.Id, file) });
        }
        return new { series.Id, title = LibraryService.DisplayTitle(series), series.Season, expiresAt = DateTimeOffset.UtcNow + MediaLinks.Lifetime, episodes = items };
    }

    // The file name only makes the link readable in players; the token alone selects the video.
    private string MediaUrl(string id, string file) => "/media/" + links.Token(id) + "/" + Uri.EscapeDataString(Path.GetFileName(file));

    /// <summary>The file a signed link points to, or null when the link is invalid, expired or revoked, or the file is gone.</summary>
    public string? Open(string token)
    {
        if (links.EpisodeId(token) is not { } id) return null;
        try { return Resolve(id).File; }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException) { return null; }
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".ts" or ".m2ts" => "video/mp2t",
        ".avi" => "video/x-msvideo",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".rmvb" => "application/vnd.rn-realmedia-vbr",
        _ => "application/octet-stream"
    };
}
