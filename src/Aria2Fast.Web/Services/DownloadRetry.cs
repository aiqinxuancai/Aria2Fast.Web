using System.Text.Json.Nodes;

namespace Aria2Fast.Web.Services;

public static class DownloadRetry
{
    public static (string[] Urls, Dictionary<string, string> Options) Source(JsonNode task)
    {
        var options = new Dictionary<string, string> { ["pause"] = "false", ["continue"] = "true", ["auto-file-renaming"] = "false" };
        if (task["dir"] is { } dir) options["dir"] = dir.ToString();
        var files = task["files"]?.AsArray() ?? [];
        if (task["infoHash"]?.ToString() is { Length: > 0 } hash)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$")) throw new ArgumentException("无法恢复磁力链接，请重新添加原始种子");
            var magnet = "magnet:?xt=urn:btih:" + hash;
            if (task["bittorrent"]?["announceList"] is JsonArray tiers)
                foreach (var tracker in tiers.OfType<JsonArray>().SelectMany(x => x).Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                    magnet += "&tr=" + Uri.EscapeDataString(tracker!);
            var selected = files.Where(x => x?["selected"]?.ToString() == "true").Select(x => x!["index"]?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToArray();
            if (selected.Length > 0) options["select-file"] = string.Join(',', selected);
            return ([magnet], options);
        }
        if (files.Count != 1) throw new ArgumentException("无法恢复多文件任务的下载源，请重新添加原始种子或 Metalink");
        var urls = (files[0]?["uris"]?.AsArray() ?? []).Select(x => x?["uri"]?.ToString())
            .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "sftp" or "magnet").Cast<string>().Distinct().ToArray();
        if (urls.Length == 0) throw new ArgumentException("任务没有可恢复的下载地址，请重新添加链接或种子");
        var path = files[0]?["path"]?.ToString()?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(path))
        {
            var slash = path.LastIndexOf('/');
            options["out"] = path[(slash + 1)..];
            if (slash >= 0) options["dir"] = path[..(slash + 1)];
        }
        return (urls, options);
    }
}
