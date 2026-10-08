using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class AriaRpc(StateStore store, HttpGateway http)
{
    private readonly SemaphoreSlim retryGate = new(1);
    private readonly Dictionary<string, string> retried = [];
    public async Task<string> Retry(string gid, string? nodeId, CancellationToken ct)
    {
        var node = Node(nodeId);
        await retryGate.WaitAsync(ct);
        try
        {
            var key = node.Id + ":" + gid;
            if (retried.TryGetValue(key, out var existing)) return existing;
            var task = await Call("tellStatus", [gid], node.Id, ct);
            if (task["status"]?.ToString() != "error") throw new ArgumentException("仅出错的任务可以重试");
            var (urls, options) = DownloadRetry.Source(task);
            try
            {
                if (await Call("getOption", [gid], node.Id, ct) is JsonObject saved)
                    foreach (var option in new[] { "header", "referer", "user-agent", "all-proxy", "http-user", "http-passwd", "ftp-user", "ftp-passwd", "max-download-limit", "max-upload-limit", "split", "max-connection-per-server", "bt-tracker" })
                        if (saved[option] is { } value) options[option] = value.ToString();
            }
            catch (InvalidOperationException) { } // aria2 may discard options for stopped tasks.
            var newGid = await Submit(urls, options, node.Id, ct);
            retried[key] = newGid;
            if (retried.Count > 2000) retried.Remove(retried.Keys.First());
            Track(node.Id, newGid, urls[0]);
            try { await Call("removeDownloadResult", [gid], node.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { store.Log("任务已重试，但旧错误记录未能清理：" + gid, "warning"); }
            store.Log($"任务已重试：{gid} → {newGid}", "success");
            return newGid;
        }
        finally { retryGate.Release(); }
    }

    public AriaNode Node(string? id = null)
    {
        var state = store.Read();
        return state.Nodes.FirstOrDefault(n => n.Id == (id ?? state.SelectedNodeId)) ?? throw new ArgumentException("节点不存在");
    }

    public async Task<JsonNode> Call(string method, object?[]? args = null, string? nodeId = null, CancellationToken ct = default)
    {
        var node = Node(nodeId);
        var parameters = new List<object?>();
        if (!string.IsNullOrEmpty(node.Secret)) parameters.Add("token:" + node.Secret);
        parameters.AddRange(args ?? []);
        using var client = http.Create(false, 20);
        // aria2's HTTP RPC server requires Content-Length; JsonContent streams chunked bodies.
        var payload = System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "aria2." + method, @params = parameters });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Validation.HttpUrl(node.Url), content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidDataException("RPC 返回为空");
        if (json["error"] is { } error) throw new InvalidOperationException(error["message"]?.ToString() ?? "RPC 错误");
        return json["result"]?.DeepClone() ?? JsonValue.Create("")!;
    }

    public async Task<object> Snapshot(string? nodeId, CancellationToken ct)
    {
        var id = Node(nodeId).Id;
        var queries = new[] { Call("getGlobalStat", nodeId: id, ct: ct), Call("tellActive", nodeId: id, ct: ct), Call("tellWaiting", [0, 1000], id, ct), Call("tellStopped", [0, 1000], id, ct) };
        var data = await Task.WhenAll(queries);
        return new { nodeId = id, stats = data[0], active = data[1], waiting = data[2], stopped = data[3] };
    }

    public async Task<string> Add(string uri, string? directory, string? nodeId = null, Dictionary<string, string>? options = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "ftp" or "magnet")) throw new ArgumentException("不支持的下载链接");
        var node = Node(nodeId);
        options ??= [];
        var dir = string.IsNullOrWhiteSpace(directory) ? (node.Id == "local" ? store.Read().Settings.DownloadDirectory : node.DownloadDirectory) : directory;
        if (!string.IsNullOrWhiteSpace(dir)) options["dir"] = dir;
        var gid = await Submit([uri], options, node.Id, ct);
        Track(node.Id, gid, uri);
        return gid;
    }

    private async Task<string> Submit(string[] urls, Dictionary<string, string> options, string nodeId, CancellationToken ct)
    {
        var settings = store.Read().Settings;
        if (Uri.TryCreate(urls[0], UriKind.Absolute, out var source) && MikanTorrentDownload.Matches(source, settings))
        {
            var data = await MikanTorrentDownload.Fetch(source, settings, http, ct);
            var torrentOptions = new Dictionary<string, string>(options);
            // An old failed HTTP task's output name belongs to the torrent, not its payload.
            torrentOptions.Remove("out");
            return (await Call("addTorrent", [Convert.ToBase64String(data), Array.Empty<string>(), torrentOptions], nodeId, ct)).ToString();
        }
        return (await Call("addUri", [urls, options], nodeId, ct)).ToString();
    }

    public void Track(string nodeId, string gid, string name) => store.Update(s =>
    {
        s.Downloads.Add(new(nodeId, gid, name, DateTimeOffset.UtcNow));
        if (s.Downloads.Count > 20000) s.Downloads.RemoveAll(x => x.Notified);
    });
}
