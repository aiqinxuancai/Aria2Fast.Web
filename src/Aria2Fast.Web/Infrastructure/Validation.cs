using System.Text.RegularExpressions;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Infrastructure;

public static class Validation
{
    public static Uri HttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入有效的 HTTP / HTTPS 地址（不要在地址中包含密码）");
        return uri;
    }

    public static string Segment(string value)
    {
        var result = Regex.Replace(value.Trim(), "[\\x00-\\x1f<>:\"/\\\\|?*]", "_").Trim('.', ' ');
        if (result.Length == 0 || result is "." or "..") throw new ArgumentException("名称不可为空或包含路径跳转");
        return result[..Math.Min(result.Length, 160)];
    }

    public static bool Matches(string title, string filter, bool regex)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (regex) return Regex.IsMatch(title, filter, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static void Subscription(Subscription s, AppState state)
    {
        HttpUrl(s.Url);
        if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("订阅名称不能为空");
        if (!state.Nodes.Any(n => n.Id == s.NodeId)) throw new ArgumentException("订阅节点不存在");
        if (s.Season is < 0 or > 999) throw new ArgumentException("季度应在 0 到 999 之间");
        if (s.IsFilterRegex)
        {
            _ = new Regex(s.Filter, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            _ = new Regex(s.ExcludeFilter, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        }
        if (!string.IsNullOrWhiteSpace(s.NamePath)) s.NamePath = Segment(s.NamePath);
    }

    public static string LocalFile(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot, comparison)) throw new ArgumentException("文件不在本地下载目录中");
        for (var current = new FileInfo(full) as FileSystemInfo; current != null; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("不允许通过符号链接访问文件");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), comparison)) break;
        }
        return full;
    }
}
