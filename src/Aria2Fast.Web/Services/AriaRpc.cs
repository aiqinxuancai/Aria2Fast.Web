using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class AriaRpc(StateStore store, HttpGateway http)
{
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
        var dir = directory ?? (node.Id == "local" ? store.Read().Settings.DownloadDirectory : node.DownloadDirectory);
        if (!string.IsNullOrWhiteSpace(dir)) options["dir"] = dir;
        var gid = (await Call("addUri", [new[] { uri }, options], node.Id, ct)).ToString();
        Track(node.Id, gid, uri);
        return gid;
    }

    public void Track(string nodeId, string gid, string name) => store.Update(s =>
    {
        s.Downloads.Add(new(nodeId, gid, name, DateTimeOffset.UtcNow));
        if (s.Downloads.Count > 20000) s.Downloads.RemoveAll(x => x.Notified);
    });
}
