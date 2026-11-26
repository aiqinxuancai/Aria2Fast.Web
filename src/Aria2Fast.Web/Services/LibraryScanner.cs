using System.Security.Cryptography;
using System.Text;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Groups the videos under the download root into works. Walk reads the disk; Build is pure.
/// A work is the subscription folder that contains the file, else the first folder under the root,
/// else (for loose files in the root) the title parsed from the file name. Season folders split works.
/// </summary>
public static class LibraryScanner
{
    public const int FileLimit = 50000;
    public sealed record ScannedFile(string Path, long Size, DateTimeOffset Modified);
    /// <summary>A local subscription's work folder, relative to the root.</summary>
    public sealed record Claim(string Directory, Subscription Subscription);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static List<ScannedFile> Walk(string root, out bool truncated)
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
                if (entry is DirectoryInfo child) { if (!SkippedDirectory(child.Name)) pending.Push(child); continue; }
                // aria2 keeps a .aria2 control file beside downloads still in progress.
                if (entry is not FileInfo file || file.Name.StartsWith('.') || !ReleaseNames.IsVideo(file.Name) || File.Exists(file.FullName + ".aria2")) continue;
                if (result.Count >= FileLimit) { truncated = true; return result; }
                result.Add(new(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc));
            }
        }
        return result;
    }

    // NAS metadata and recycle bins: Synology @eaDir, #recycle, Windows $RECYCLE.BIN.
    private static bool SkippedDirectory(string name) => name[0] is '.' or '@' or '#' or '$' || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    public static LibraryState Build(IReadOnlyList<ScannedFile> files, IReadOnlyList<Claim> claims, IReadOnlyDictionary<string, Subscription> subscriptions, LibraryState previous, DateTimeOffset now, bool truncated = false)
    {
        var oldEpisodes = previous.Episodes.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.First());
        var groups = new Dictionary<string, Group>();
        foreach (var file in files)
        {
            var segments = file.Path.Split('/');
            var claimed = claims.Where(c => file.Path.StartsWith(c.Directory + "/", PathComparison)).Select(c => c.Directory).MaxBy(d => d.Length);
            var directory = claimed ?? (segments.Length > 1 ? segments[0] : "");
            var depth = directory.Length == 0 ? 0 : directory.Split('/').Length;
            var folder = depth == 0 ? "" : segments[depth - 1];
            var candidates = claimed == null ? new List<Subscription>() : claims.Where(c => string.Equals(c.Directory, claimed, PathComparison)).Select(c => c.Subscription).ToList();
            var parsed = ReleaseNames.ParseFile(segments[^1]);
            var kind = parsed.Kind;
            int? folderSeason = null;
            foreach (var segment in segments[depth..^1])
            {
                if (ReleaseNames.FolderKind(segment) is { } folderKind) { if (kind == LibraryEpisodeKind.Episode) kind = folderKind; }
                else folderSeason = ReleaseNames.SeasonFolder(segment) ?? folderSeason;
            }
            // Season folders are explicit (subscriptions create them), so they win over names.
            var season = folderSeason ?? parsed.Season ?? (folder.Length > 0 ? ReleaseNames.Season(folder) : null) ?? (candidates.Count == 1 ? (int?)candidates[0].Season : null) ?? 0;
            var title = folder.Length > 0 ? ReleaseNames.Title(folder) : parsed.Title.Length > 0 ? parsed.Title : ReleaseNames.Title(Path.GetFileNameWithoutExtension(file.Path));
            var key = (directory.Length > 0 ? "dir:" + directory : "title:" + ReleaseNames.Key(title)).ToLowerInvariant() + "#" + season;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new(directory, season, title);
            group.Claimed ??= candidates.FirstOrDefault(s => s.Season == season) ?? candidates.FirstOrDefault(s => s.Season == 0 || season == 0);
            var old = oldEpisodes.GetValueOrDefault(file.Path);
            group.Episodes.Add(new LibraryEpisode
            {
                Id = Hash(file.Path), Path = file.Path, Size = file.Size, Modified = file.Modified, Number = parsed.Number, Kind = kind,
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
                SubscriptionId = subscription?.Id, MikanId = subscription?.MikanId,
                AddedAt = old?.AddedAt ?? group.Episodes.Min(e => e.AddedAt), UpdatedAt = group.Episodes.Max(e => e.AddedAt)
            };
            if (old != null && (old.ManualMatch || SameIdentity(old, item, subscription))) CopyMatch(old, item);
            else { item.BgmId = subscription?.BgmId; item.TmdbId = subscription?.TmdbId; }
            foreach (var episode in group.Episodes) episode.SeriesId = item.Id;
            series.Add(item);
            episodes.AddRange(group.Episodes.OrderBy(e => e.Kind).ThenBy(e => e.Number ?? double.MaxValue).ThenBy(e => e.Path, StringComparer.Ordinal));
        }
        var paths = episodes.Select(e => e.Path).ToHashSet();
        return new LibraryState
        {
            Series = series.OrderByDescending(s => s.UpdatedAt).ToList(),
            Episodes = episodes,
            Origins = previous.Origins.Where(o => paths.Contains(o.Key)).ToDictionary(),
            LastScan = now,
            LastScanWarning = truncated ? $"视频文件超过 {FileLimit} 个，仅索引了前 {FileLimit} 个" : null
        };
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>An automatic match stays valid while the work it was derived from is unchanged.</summary>
    private static bool SameIdentity(LibrarySeries old, LibrarySeries next, Subscription? subscription) =>
        old.MikanId == next.MikanId && old.Title == next.Title
        && (subscription?.BgmId is not { } bgm || bgm == old.BgmId) && (subscription?.TmdbId is not { } tmdb || tmdb == old.TmdbId);

    private static void CopyMatch(LibrarySeries from, LibrarySeries to)
    {
        to.BgmId = from.BgmId; to.TmdbId = from.TmdbId; to.ManualMatch = from.ManualMatch; to.MatchedBy = from.MatchedBy;
        to.Status = from.Status; to.Error = from.Error; to.Attempts = from.Attempts; to.NextAttempt = from.NextAttempt; to.Metadata = from.Metadata;
    }

    private sealed class Group(string directory, int season, string title)
    {
        public string Directory { get; } = directory;
        public int Season { get; } = season;
        public string Title { get; } = title;
        public Subscription? Claimed { get; set; }
        public List<LibraryEpisode> Episodes { get; } = [];
    }
}
