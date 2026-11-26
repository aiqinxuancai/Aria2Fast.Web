using System.Security.Cryptography;
using System.Text;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Groups the videos in the library folders into works. Walk reads the disk; Build is pure.
/// A work is the subscription folder that contains the file, else the first folder below the library folder,
/// else (for loose files) the title parsed from the file name. Season folders split anime and series works;
/// a movie is one folder (or one loose file) without episode numbers.
/// </summary>
public static class LibraryScanner
{
    public const int FileLimit = 50000;
    /// <summary>Path is the library path (see LibraryFolders); Folder the ID of the library folder it was found in.</summary>
    public sealed record ScannedFile(string Path, long Size, DateTimeOffset Modified, string Folder = "");
    /// <summary>A local subscription's work folder, relative to the download root.</summary>
    public sealed record Claim(string Directory, Subscription Subscription);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static List<ScannedFile> Walk(string root, out bool truncated) => Walk(root, "", "", new HashSet<string>(), FileLimit, out truncated);

    /// <summary>Videos under a folder, as library paths below prefix; folders in excluded (other library folders) are skipped.</summary>
    public static List<ScannedFile> Walk(string root, string prefix, string folderId, IReadOnlySet<string> excluded, int limit, out bool truncated)
    {
        truncated = false;
        var result = new List<ScannedFile>();
        // Symlinks are skipped as file access already refuses them (Validation.LocalFile).
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.Count > 0)
        {
            List<FileSystemInfo> entries;
            try { entries = pending.Pop().EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child) { if (!SkippedDirectory(child.Name) && !excluded.Contains(child.FullName)) pending.Push(child); continue; }
                // aria2 keeps a .aria2 control file beside downloads still in progress.
                if (entry is not FileInfo file || file.Name.StartsWith('.') || !ReleaseNames.IsVideo(file.Name) || File.Exists(file.FullName + ".aria2")) continue;
                if (result.Count >= limit) { truncated = true; return result; }
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                result.Add(new(prefix.Length == 0 ? relative : prefix + "/" + relative, file.Length, file.LastWriteTimeUtc, folderId));
            }
        }
        return result;
    }

    // NAS metadata and recycle bins: Synology @eaDir, #recycle, Windows $RECYCLE.BIN.
    private static bool SkippedDirectory(string name) => name[0] is '.' or '@' or '#' or '$' || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Folders lists the library folders by ID (null: everything is anime in the download root). Works of folders in keep — folders
    /// that are missing or empty now but had videos before, such as an unmounted share — are carried over unchanged.
    /// </summary>
    public static LibraryState Build(IReadOnlyList<ScannedFile> files, IReadOnlyList<Claim> claims, IReadOnlyDictionary<string, Subscription> subscriptions, LibraryState previous, DateTimeOffset now,
        bool truncated = false, IReadOnlyDictionary<string, LibraryFolders.Folder>? folders = null, IReadOnlySet<string>? keep = null)
    {
        var oldEpisodes = previous.Episodes.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.First());
        var groups = new Dictionary<string, Group>();
        foreach (var file in files)
        {
            var folder = folders?.GetValueOrDefault(file.Folder);
            var prefix = folder?.Prefix ?? "";
            var category = folder?.Category ?? LibraryCategory.Anime;
            var segments = file.Path.Split('/');
            var prefixDepth = prefix.Length == 0 ? 0 : prefix.Split('/').Length;
            // Subscription folders are relative to the download root and only count inside the file's own library folder.
            var claimed = file.Path.StartsWith('@') ? null : claims
                .Where(c => (prefix.Length == 0 || c.Directory.StartsWith(prefix + "/", PathComparison)) && file.Path.StartsWith(c.Directory + "/", PathComparison))
                .Select(c => c.Directory).MaxBy(d => d.Length);
            var loose = claimed == null && segments.Length <= prefixDepth + 1;
            var directory = claimed ?? (loose ? prefix : string.Join('/', segments[..(prefixDepth + 1)]));
            var depth = directory.Length == 0 ? 0 : directory.Split('/').Length;
            var name = loose ? "" : segments[depth - 1];
            var candidates = claimed == null ? new List<Subscription>() : claims.Where(c => string.Equals(c.Directory, claimed, PathComparison)).Select(c => c.Subscription).ToList();
            var parsed = ReleaseNames.ParseFile(segments[^1]);
            var kind = parsed.Kind;
            int? folderSeason = null;
            foreach (var segment in segments[depth..^1])
            {
                if (ReleaseNames.FolderKind(segment) is { } folderKind) { if (kind == LibraryEpisodeKind.Episode) kind = folderKind; }
                else folderSeason = ReleaseNames.SeasonFolder(segment) ?? folderSeason;
            }
            string title;
            int season;
            int? year = null;
            double? number = parsed.Number;
            if (category == LibraryCategory.Movie)
            {
                // A movie folder may hold parts, versions and extras, but no episodes or seasons.
                (title, year) = ReleaseNames.Movie(name.Length > 0 ? name : Path.GetFileNameWithoutExtension(segments[^1]));
                season = 0;
                number = null;
                if (kind == LibraryEpisodeKind.Special) kind = LibraryEpisodeKind.Episode;
            }
            else
            {
                // Season folders are explicit (subscriptions create them), so they win over names.
                season = folderSeason ?? parsed.Season ?? (name.Length > 0 ? ReleaseNames.Season(name) : null) ?? (candidates.Count == 1 ? (int?)candidates[0].Season : null) ?? 0;
                title = name.Length > 0 ? ReleaseNames.Title(name) : parsed.Title.Length > 0 ? parsed.Title : ReleaseNames.Title(Path.GetFileNameWithoutExtension(file.Path));
            }
            var key = (loose ? "title:" + (prefix.Length > 0 ? prefix + ":" : "") + ReleaseNames.Key(title) + (year is { } y ? ":" + y : "") : "dir:" + directory).ToLowerInvariant() + "#" + season;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new(directory, season, title, file.Folder, category, year);
            group.Claimed ??= candidates.FirstOrDefault(s => s.Season == season) ?? candidates.FirstOrDefault(s => s.Season == 0 || season == 0);
            var old = oldEpisodes.GetValueOrDefault(file.Path);
            var fix = previous.Overrides.GetValueOrDefault(file.Path);
            group.Episodes.Add(new LibraryEpisode
            {
                Id = Hash(file.Path), Path = file.Path, Size = file.Size, Modified = file.Modified,
                Number = fix == null ? number : fix.Number, Kind = fix?.Kind ?? kind, NumberSource = fix?.Source,
                // Existing files keep their modification time, so a first scan still orders works by recency.
                AddedAt = old?.AddedAt ?? (file.Modified < now ? file.Modified : now)
            });
        }

        var oldSeries = previous.Series.ToDictionary(s => s.Key);
        var series = new List<LibrarySeries>();
        var episodes = new List<LibraryEpisode>();
        foreach (var (key, group) in groups)
        {
            var old = oldSeries.GetValueOrDefault(key);
            var subscription = group.Claimed ?? group.Episodes.Select(e => previous.Origins.GetValueOrDefault(e.Path)?.SubscriptionId)
                .OfType<string>().Where(subscriptions.ContainsKey).GroupBy(id => id).OrderByDescending(g => g.Count()).Select(g => subscriptions[g.Key]).FirstOrDefault();
            var item = new LibrarySeries
            {
                Id = old?.Id ?? Hash(key), Key = key, Title = group.Title, Directory = group.Directory, Season = group.Season,
                FolderId = group.Folder, Category = group.Category, Year = group.Year,
                SubscriptionId = subscription?.Id, MikanId = subscription?.MikanId,
                AddedAt = old?.AddedAt ?? group.Episodes.Min(e => e.AddedAt), UpdatedAt = group.Episodes.Max(e => e.AddedAt)
            };
            if (old != null && (old.ManualMatch || SameIdentity(old, item, subscription))) CopyMatch(old, item);
            else { item.BgmId = subscription?.BgmId; item.TmdbId = subscription?.TmdbId; }
            foreach (var episode in group.Episodes) episode.SeriesId = item.Id;
            series.Add(item);
            episodes.AddRange(group.Episodes.OrderBy(e => e.Kind).ThenBy(e => e.Number ?? double.MaxValue).ThenBy(e => e.Path, StringComparer.Ordinal));
        }
        if (keep is { Count: > 0 })
        {
            // Works indexed before the folder settings existed have no folder ID; they came from the download root.
            var rootFolder = folders?.Values.FirstOrDefault(f => f.Prefix.Length == 0)?.Id ?? "";
            var kept = previous.Series.Where(s => keep.Contains(s.FolderId.Length > 0 ? s.FolderId : rootFolder) && !groups.ContainsKey(s.Key)).ToList();
            var keptIds = kept.Select(s => s.Id).ToHashSet();
            series.AddRange(kept);
            episodes.AddRange(previous.Episodes.Where(e => keptIds.Contains(e.SeriesId)));
        }
        var paths = episodes.Select(e => e.Path).ToHashSet();
        return new LibraryState
        {
            Series = series.OrderByDescending(s => s.UpdatedAt).ToList(),
            Episodes = episodes,
            Origins = previous.Origins.Where(o => paths.Contains(o.Key)).ToDictionary(),
            Overrides = previous.Overrides.Where(o => paths.Contains(o.Key)).ToDictionary(),
            LastScan = now,
            LastScanWarning = truncated ? $"视频文件超过 {FileLimit} 个，仅索引了前 {FileLimit} 个" : null
        };
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>An automatic match stays valid while the work it was derived from is unchanged.</summary>
    private static bool SameIdentity(LibrarySeries old, LibrarySeries next, Subscription? subscription) =>
        old.MikanId == next.MikanId && old.Title == next.Title && old.Category == next.Category
        && (subscription?.BgmId is not { } bgm || bgm == old.BgmId) && (subscription?.TmdbId is not { } tmdb || tmdb == old.TmdbId);

    private static void CopyMatch(LibrarySeries from, LibrarySeries to)
    {
        to.BgmId = from.BgmId; to.TmdbId = from.TmdbId; to.ManualMatch = from.ManualMatch; to.MatchedBy = from.MatchedBy;
        to.Status = from.Status; to.Error = from.Error; to.Attempts = from.Attempts; to.NextAttempt = from.NextAttempt; to.Metadata = from.Metadata;
        to.AiTriedAt = from.AiTriedAt;
    }

    private sealed class Group(string directory, int season, string title, string folder, LibraryCategory category, int? year)
    {
        public string Directory { get; } = directory;
        public int Season { get; } = season;
        public string Title { get; } = title;
        public string Folder { get; } = folder;
        public LibraryCategory Category { get; } = category;
        public int? Year { get; } = year;
        public Subscription? Claimed { get; set; }
        public List<LibraryEpisode> Episodes { get; } = [];
    }
}
