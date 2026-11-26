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
            _ = new Regex(s.Filter, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        if (s.IsExcludeFilterRegex ?? s.IsFilterRegex)
            _ = new Regex(s.ExcludeFilter, RegexOptions.None, TimeSpan.FromMilliseconds(200));
        if (!string.IsNullOrWhiteSpace(s.NamePath)) s.NamePath = Segment(s.NamePath);
        // The RSS address is authoritative, so editing it never leaves a stale ID behind.
        s.MikanId = MikanId(s.Url) ?? (string.IsNullOrWhiteSpace(s.MikanId) ? null : s.MikanId.Trim());
        if (s.MikanId != null && !Regex.IsMatch(s.MikanId, @"^\d{1,12}$")) throw new ArgumentException("Mikan 番剧 ID 无效");
        if (s.BgmId is <= 0 || s.TmdbId is <= 0) throw new ArgumentException("Bangumi / TMDB 条目 ID 无效");
    }

    /// <summary>bangumiId from a Mikan RSS address such as /RSS/Bangumi?bangumiId=3310&amp;subgroupid=583.</summary>
    public static string? MikanId(string url) =>
        Regex.Match(url, @"[?&]bangumiId=(\d{1,12})(?=&|#|$)", RegexOptions.IgnoreCase) is { Success: true } match ? match.Groups[1].Value : null;

    public static void AnimeSources(AnimeSourcesSettings sources)
    {
        foreach (var config in new[] { sources.AcgRip, sources.Nyaa, sources.Dmhy, sources.Acgnx })
        {
            config.BaseUrl = config.BaseUrl?.Trim() ?? "";
            if (config.BaseUrl.Length > 0) HttpUrl(config.BaseUrl);
        }
        foreach (var endpoint in sources.Torznab) Torznab(endpoint);
        if (sources.Torznab.Select(e => e.Id).Distinct().Count() != sources.Torznab.Count) throw new ArgumentException("Torznab 索引器 ID 重复");
    }

    public static void Torznab(TorznabEndpoint endpoint)
    {
        endpoint.Id = endpoint.Id?.Trim() ?? "";
        endpoint.Name = endpoint.Name?.Trim() ?? "";
        endpoint.Url = endpoint.Url?.Trim() ?? "";
        endpoint.ApiKey = endpoint.ApiKey?.Trim() ?? "";
        endpoint.Categories = Regex.Replace(endpoint.Categories ?? "", @"\s", "");
        if (!Regex.IsMatch(endpoint.Id, "^[A-Za-z0-9_-]{1,64}$")) throw new ArgumentException("Torznab 索引器 ID 无效");
        if (endpoint.Name.Length is 0 or > 60) throw new ArgumentException("请填写 1 至 60 个字符的索引器名称");
        var url = HttpUrl(endpoint.Url);
        // Keys stay in their own field so they never end up in subscription URLs or backups.
        if (Regex.IsMatch(url.Query, @"[?&](jackett_)?apikey=", RegexOptions.IgnoreCase)) throw new ArgumentException("请将 API Key 填写在单独的输入框中，不要放在地址里");
        if (endpoint.ApiKey.Length > 256) throw new ArgumentException("API Key 过长");
        if (!Regex.IsMatch(endpoint.Categories, @"^(\d{1,6}(,\d{1,6}){0,19})?$")) throw new ArgumentException("分类应为逗号分隔的数字，例如 5070");
    }

    public static void Transcoding(LibrarySettings settings)
    {
        settings.FfmpegPath = settings.FfmpegPath?.Trim().Trim('"') ?? "";
        if (settings.FfmpegPath.IndexOfAny(['\n', '\r', '\0']) >= 0) throw new ArgumentException("ffmpeg 路径无效");
        if (!Services.FfmpegService.Encoders.Contains(settings.VideoEncoder)) throw new ArgumentException("不支持的视频编码器");
        if (settings.MaxHeight is not (0 or 480 or 720 or 1080 or 1440 or 2160)) throw new ArgumentException("转码分辨率无效");
        if (settings.MaxTranscodes is < 1 or > 8) throw new ArgumentException("同时转码数应在 1 至 8 之间");
        settings.VaapiDevice = string.IsNullOrWhiteSpace(settings.VaapiDevice) ? "/dev/dri/renderD128" : settings.VaapiDevice.Trim();
        if (!settings.VaapiDevice.StartsWith("/dev/", StringComparison.Ordinal) || settings.VaapiDevice.Contains("..")) throw new ArgumentException("VAAPI 设备应为 /dev/ 下的路径");
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
