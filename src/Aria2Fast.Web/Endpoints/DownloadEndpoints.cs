using System.Text.Json.Nodes;
using Aria2Fast.Web.Services;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Endpoints;

public static class DownloadEndpoints
{
    private static readonly HashSet<string> AllowedOptions = ["max-download-limit", "max-upload-limit", "max-overall-download-limit", "max-overall-upload-limit", "min-split-size", "bt-max-peers", "max-concurrent-downloads", "split", "max-connection-per-server", "seed-ratio", "seed-time", "bt-tracker", "select-file", "out", "dir", "header", "referer", "user-agent", "all-proxy", "pause"];
    public static void MapDownloads(this RouteGroupBuilder api)
    {
        api.MapGet("/tasks", (string? node, AriaRpc rpc, CancellationToken ct) => rpc.Snapshot(node, ct));
        api.MapGet("/tasks/{gid}", (string gid, string? node, AriaRpc rpc, CancellationToken ct) => rpc.Call("tellStatus", [gid], node, ct));
        api.MapGet("/tasks/{gid}/peers", (string gid, string? node, AriaRpc rpc, CancellationToken ct) => rpc.Call("getPeers", [gid], node, ct));
        api.MapPost("/tasks", async (AddRequest input, AriaRpc rpc, CancellationToken ct) =>
        {
            if (input.Urls.Length is < 1 or > 200) throw new ArgumentException("每次添加 1 至 200 个链接");
            ValidateOptions(input.Options);
            var results = new List<object>();
            foreach (var url in input.Urls.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
            {
                try { results.Add(new { url, gid = await rpc.Add(url.Trim(), input.Directory, input.NodeId, input.Options is null ? null : new(input.Options), ct), error = (string?)null }); }
                catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new { url, gid = (string?)null, error = ex.Message }); }
            }
            return Results.Ok(results);
        });
        api.MapPost("/tasks/upload", async (HttpRequest request, AriaRpc rpc, DownloadPaths paths, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new ArgumentException("请选择种子或 Metalink 文件");
            if (file.Length is < 1 or > 16 * 1024 * 1024) throw new ArgumentException("文件大小不能超过 16 MB");
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".torrent" or ".metalink" or ".meta4")) throw new ArgumentException("仅支持 torrent / metalink / meta4");
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, ct);
            var node = rpc.Node(form["nodeId"].FirstOrDefault());
            var options = new Dictionary<string, string>();
            var directory = form["directory"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(directory)) directory = await paths.Default(node.Id, ct);
            if (!string.IsNullOrWhiteSpace(directory)) options["dir"] = directory;
            var data = Convert.ToBase64String(memory.ToArray());
            var result = extension == ".torrent" ? await rpc.Call("addTorrent", [data, Array.Empty<string>(), options], node.Id, ct) : await rpc.Call("addMetalink", [data, options], node.Id, ct);
            if (result is JsonArray array) foreach (var gid in array) rpc.Track(node.Id, gid!.ToString(), file.FileName);
            else rpc.Track(node.Id, result.ToString(), file.FileName);
            return Results.Ok(result);
        });
        api.MapPost("/tasks/action", async (ActionRequest input, AriaRpc rpc, CancellationToken ct) =>
        {
            var method = input.Action switch { "pause" => "pause", "resume" => "unpause", "remove" => "remove", "forget" => "removeDownloadResult", "pauseAll" => "pauseAll", "resumeAll" => "unpauseAll", "purge" => "purgeDownloadResult", "save" => "saveSession", _ => throw new ArgumentException("不支持的操作") };
            if (input.Action is "pauseAll" or "resumeAll" or "purge" or "save") return Results.Ok(await rpc.Call(method, nodeId: input.NodeId, ct: ct));
            var results = new List<object>();
            foreach (var gid in input.Gids.Take(1000))
            {
                try { results.Add(new { gid, result = (await rpc.Call(method, [gid], input.NodeId, ct)).ToString(), error = (string?)null }); }
                catch (Exception ex) when (ex is not OperationCanceledException) { results.Add(new { gid, result = "", error = ex.Message }); }
            }
            return Results.Ok(results);
        });
        api.MapPost("/tasks/{gid}/position", (string gid, PositionRequest input, AriaRpc rpc, CancellationToken ct) => rpc.Call("changePosition", [gid, input.Position, "POS_SET"], input.NodeId, ct));
        api.MapGet("/options", (string? node, string? gid, AriaRpc rpc, CancellationToken ct) => rpc.Call(gid == null ? "getGlobalOption" : "getOption", gid == null ? null : [gid], node, ct));
        api.MapPost("/options", async (OptionsRequest input, AriaRpc rpc, StateStore store, CancellationToken ct) =>
        {
            ValidateOptions(input.Options);
            var node = rpc.Node(input.NodeId);
            if (node.Id == "local" && input.Gid == null)
            {
                var settings = store.Read().Settings;
                foreach (var pair in input.Options) settings.LocalOptions[pair.Key] = pair.Value;
                LocalAriaOptions.Validate(settings);
            }
            var effective = new Dictionary<string, string>(input.Options);
            if (node.Id == "local" && input.Gid == null && effective.ContainsKey("bt-tracker"))
            {
                var snapshot = store.Read();
                snapshot.Settings.LocalOptions["bt-tracker"] = effective["bt-tracker"];
                effective["bt-tracker"] = LocalAriaOptions.Trackers(snapshot);
            }
            var result = await rpc.Call(input.Gid == null ? "changeGlobalOption" : "changeOption", input.Gid == null ? [effective] : [input.Gid, effective], node.Id, ct);
            if (node.Id == "local" && input.Gid == null)
                store.Update(s => { foreach (var entry in input.Options) s.Settings.LocalOptions[entry.Key] = entry.Value; });
            return result;
        });
    }
    private static void ValidateOptions(Dictionary<string, string>? options)
    {
        if (options != null && options.Any(x => !AllowedOptions.Contains(x.Key) || x.Value.Length > 16000)) throw new ArgumentException("包含不支持的 Aria2 参数");
    }
    public sealed record AddRequest(string[] Urls, string? Directory, string? NodeId, Dictionary<string, string>? Options);
    public sealed record ActionRequest(string Action, string[] Gids, string? NodeId);
    public sealed record OptionsRequest(string? NodeId, string? Gid, Dictionary<string, string> Options);
    public sealed record PositionRequest(string? NodeId, int Position);
}
