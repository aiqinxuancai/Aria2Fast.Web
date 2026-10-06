using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;

namespace Aria2Fast.Web.Endpoints;

public static class ConfigurationEndpoints
{
    public static void MapConfiguration(this RouteGroupBuilder api)
    {
        api.MapGet("/state", (StateStore store, LocalAriaService local, SubscriptionService subs) =>
        {
            var s = store.Read();
            return new { s.Nodes, s.SelectedNodeId, s.Settings, s.Notices, local = new { local.Running, local.Error }, trackers = s.Trackers, checking = subs.Checking };
        });
        api.MapPut("/settings", (WebSettings input, StateStore store) =>
        {
            if (input.SubscriptionIntervalMinutes is < 1 or > 1440 || input.LocalRpcPort is < 1024 or > 65535) throw new ArgumentException("轮询间隔或 RPC 端口无效");
            Validation.HttpUrl(input.MikanBaseUrl); Validation.HttpUrl(input.PushEndpoint);
            if (input.Oss.IntervalMinutes is < 1 or > 1440) throw new ArgumentException("OSS 同步间隔应在 1 至 1440 分钟之间");
            if (input.Oss.AutoSync)
            {
                Validation.HttpUrl(input.Oss.Endpoint);
                if (string.IsNullOrWhiteSpace(input.Oss.Bucket) || string.IsNullOrWhiteSpace(input.Oss.AccessKeyId) || string.IsNullOrWhiteSpace(input.Oss.AccessKeySecret))
                    throw new ArgumentException("启用自动同步前请填写完整 OSS 配置");
            }
            if (input.ProxyUrl.Length > 0) Validation.HttpUrl(input.ProxyUrl);
            if (string.IsNullOrWhiteSpace(input.DownloadDirectory) || input.DownloadDirectory.Contains('\n') || input.DownloadDirectory.Contains('\r')) throw new ArgumentException("下载目录无效");
            input.DownloadDirectory = Path.GetFullPath(input.DownloadDirectory);
            foreach (var profile in input.AiProfiles) { Validation.HttpUrl(profile.BaseUrl); if (string.IsNullOrWhiteSpace(profile.ModelName)) throw new ArgumentException("模型名称不能为空"); }
            var tuning = input.LocalOptions.Where(x => LocalAriaOptions.Defaults.ContainsKey(x.Key)).ToDictionary();
            input.LocalOptions = tuning;
            LocalAriaOptions.Validate(input);
            store.Update(s =>
            {
                input.LocalOptions = new(s.Settings.LocalOptions);
                foreach (var pair in tuning) input.LocalOptions[pair.Key] = pair.Value;
                s.Settings = input;
            });
            return Results.Ok(new { ok = true });
        });
        api.MapGet("/local/diagnostics", (LocalDiagnostics diagnostics, CancellationToken ct) => diagnostics.Inspect(ct));
        api.MapPost("/local/trackers/update", (TrackerService trackers, CancellationToken ct) => trackers.Update(ct));
        api.MapPost("/local/restart", async (LocalAriaService local, CancellationToken ct) => { await local.Restart(ct); return new { local.Running, local.Error }; });
        api.MapPost("/nodes", (AriaNode node, StateStore store) =>
        {
            Validation.HttpUrl(node.Url);
            if (node.Id == "local") throw new ArgumentException("本地节点请通过设置管理");
            if (string.IsNullOrWhiteSpace(node.Name)) throw new ArgumentException("节点名称不能为空");
            store.Update(s => { s.Nodes.RemoveAll(n => n.Id == node.Id); s.Nodes.Add(node); });
            return node;
        });
        api.MapDelete("/nodes/{id}", (string id, StateStore store) =>
        {
            store.Update(s =>
            {
                if (id == "local" || s.Subscriptions.Any(x => x.NodeId == id)) throw new ArgumentException("本地节点或有订阅关联的节点不可删除");
                s.Nodes.RemoveAll(n => n.Id == id);
                if (s.SelectedNodeId == id) s.SelectedNodeId = "local";
            });
            return Results.Ok();
        });
        api.MapPost("/nodes/{id}/select", (string id, StateStore store, AriaRpc rpc) => { _ = rpc.Node(id); store.Update(s => s.SelectedNodeId = id); return Results.Ok(); });
        api.MapPost("/nodes/{id}/test", (string id, AriaRpc rpc, CancellationToken ct) => rpc.Call("getVersion", nodeId: id, ct: ct));
        api.MapGet("/nodes/{id}/directory", async (string id, DownloadPaths paths, CancellationToken ct) => new { directory = await paths.Default(id, ct) });
        api.MapPost("/push/test", async (BackgroundWorker worker, CancellationToken ct) => { await worker.Push("Aria2Fast Web", "测试通知发送成功", ct); return Results.Ok(); });
        api.MapPost("/ai/test", async (AiService ai, CancellationToken ct) => new { text = await ai.Send("请回复：连接成功", "你是接口测试助手。", ct) });
        api.MapPost("/ai/test-profile", async (AiProfile profile, AiService ai, CancellationToken ct) =>
        {
            Validation.HttpUrl(profile.BaseUrl);
            if (!Enum.IsDefined(profile.Protocol)) throw new ArgumentException("不支持的 AI 协议");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var text = await ai.SendProfile(profile, "请回复：连接成功", "你是接口测试助手。", ct);
            return new { text, elapsedMs = timer.ElapsedMilliseconds };
        });
        api.MapPost("/ai/profiles", (AiProfile profile, StateStore store) =>
        {
            Validation.HttpUrl(profile.BaseUrl);
            if (!Enum.IsDefined(profile.Protocol) || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.ModelName) || string.IsNullOrWhiteSpace(profile.ApiKey))
                throw new ArgumentException("请填写接口名称、有效协议、模型和 API Key");
            store.Update(s =>
            {
                var index = s.Settings.AiProfiles.FindIndex(p => p.Id == profile.Id);
                if (index >= 0) s.Settings.AiProfiles[index] = profile;
                else s.Settings.AiProfiles.Add(profile);
                if (string.IsNullOrEmpty(s.Settings.SelectedAiId)) s.Settings.SelectedAiId = profile.Id;
            });
            return profile;
        });
        api.MapPost("/ai/profiles/{id}/select", (string id, StateStore store) =>
        {
            store.Update(s =>
            {
                if (!s.Settings.AiProfiles.Any(p => p.Id == id)) throw new ArgumentException("AI 接口不存在");
                s.Settings.SelectedAiId = id;
            });
            return Results.Ok();
        });
        api.MapDelete("/ai/profiles/{id}", (string id, StateStore store) =>
        {
            store.Update(s =>
            {
                s.Settings.AiProfiles.RemoveAll(p => p.Id == id);
                if (s.Settings.SelectedAiId == id) s.Settings.SelectedAiId = s.Settings.AiProfiles.FirstOrDefault()?.Id ?? "";
            });
            return Results.Ok();
        });
        api.MapGet("/backup", (BackupService backup) => Results.File(backup.Export(), "application/json", "aria2fast-subscriptions.json"));
        api.MapPost("/backup/import", async (HttpRequest request, BackupService backup, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            return new { imported = backup.Import(await reader.ReadToEndAsync(ct)) };
        });
        api.MapPost("/backup/oss/{action}", (string action, BackupService backup) => action switch { "upload" => backup.Upload(), "download" => backup.Download(), _ => throw new ArgumentException("无效操作") });
    }
}
