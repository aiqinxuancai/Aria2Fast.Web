using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Library folders resolved against the download directory. A video's library path is relative to the download directory when
/// it lies inside it, so its ID, playback progress and notification links do not depend on the folder settings; videos in folders
/// elsewhere use "@{folder ID}/relative path".
/// </summary>
public static class LibraryFolders
{
    public sealed record Folder(string Id, string FullPath, string Prefix, LibraryCategory Category);
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static List<Folder> Resolve(LibrarySettings settings, string downloadRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot));
        var result = new List<Folder>();
        foreach (var folder in settings.Folders)
        {
            if (FullPath(folder.Path, root) is not { } full || result.Any(f => PathComparer.Equals(f.FullPath, full))) continue;
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            var inside = relative == "." || !(relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative));
            result.Add(new(folder.Id, full, !inside ? "@" + folder.Id : relative == "." ? "" : relative, folder.Category));
        }
        return result;
    }

    /// <summary>Empty and relative paths are inside the download directory.</summary>
    public static string? FullPath(string path, string root)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? root : Path.IsPathRooted(path) ? path : Path.Combine(root, path))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Folders configured inside another one are left to their own category when the outer one is scanned.</summary>
    public static HashSet<string> Nested(Folder folder, IEnumerable<Folder> all) =>
        all.Where(o => o != folder && o.FullPath.StartsWith(folder.FullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison))
            .Select(o => o.FullPath).ToHashSet(PathComparer);

    /// <summary>The file at a library path, which must stay inside its folder (no "..", no symbolic links).</summary>
    public static string Locate(string libraryPath, IReadOnlyList<Folder> folders, string downloadRoot)
    {
        if (!libraryPath.StartsWith('@')) return Inside(downloadRoot, libraryPath);
        var slash = libraryPath.IndexOf('/');
        var folder = slash > 1 ? folders.FirstOrDefault(f => f.Prefix == libraryPath[..slash]) : null;
        if (folder == null) throw new FileNotFoundException("视频所在的媒体目录已从设置中移除");
        return Inside(folder.FullPath, libraryPath[(slash + 1)..]);
    }

    /// <summary>Outside the folder is refused before existence is checked; symbolic links are refused last (LocalFile needs an existing path).</summary>
    private static string Inside(string folder, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, Comparison)) throw new ArgumentException("文件不在媒体目录中");
        if (!File.Exists(full)) throw new FileNotFoundException("视频文件不存在，可能已被移动或删除，请重新扫描媒体库");
        return Validation.LocalFile(root, full);
    }

    /// <summary>Settings check: paths resolve, are not repeated, and IDs are short, unique and stable (new folders get one from their path).</summary>
    public static void Validate(LibrarySettings settings, string downloadRoot)
    {
        if (settings.Folders.Count > 20) throw new ArgumentException("媒体目录最多 20 个");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot));
        var seen = new HashSet<string>(PathComparer);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in settings.Folders)
        {
            folder.Path = folder.Path?.Trim().Trim('"') ?? "";
            if (folder.Path.IndexOfAny(['\n', '\r', '\0']) >= 0 || FullPath(folder.Path, root) is not { } full) throw new ArgumentException("媒体目录路径无效：" + folder.Path);
            if (!seen.Add(full)) throw new ArgumentException("媒体目录重复：" + (folder.Path.Length > 0 ? folder.Path : "下载目录"));
            if (!Enum.IsDefined(folder.Category)) throw new ArgumentException("媒体目录类别无效");
            folder.Id = folder.Id?.Trim() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(folder.Id, "^[a-z0-9-]{1,24}$") || ids.Contains(folder.Id)) folder.Id = "f" + LibraryScanner.Hash(full)[..8];
            if (!ids.Add(folder.Id)) throw new ArgumentException("媒体目录重复：" + folder.Path);
        }
    }
}
