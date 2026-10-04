using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class BackgroundWorker(StateStore store, SubscriptionService subscriptions, AriaRpc rpc, HttpGateway http, ILogger<BackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCheck = DateTimeOffset.UtcNow.AddSeconds(8);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextCheck)
                {
                    await subscriptions.Check(ct: stoppingToken);
                    nextCheck = DateTimeOffset.UtcNow.AddMinutes(store.Read().Settings.SubscriptionIntervalMinutes);
                }
                await CheckCompletions(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "后台检查失败"); }
        }
    }

    private async Task CheckCompletions(CancellationToken ct)
    {
        var snapshot = store.Read();
        foreach (var group in snapshot.Downloads.Where(x => !x.Notified).GroupBy(x => x.NodeId))
        {
            try
            {
                var stopped = (await rpc.Call("tellStopped", [0, 1000], group.Key, ct)).AsArray();
                foreach (var download in group)
                {
                    var task = stopped.FirstOrDefault(x => x?["gid"]?.ToString() == download.Gid && x?["status"]?.ToString() == "complete");
                    if (task == null) continue;
                    // Magnet metadata completes separately; follow the real content GID.
                    if (task["followedBy"] is System.Text.Json.Nodes.JsonArray children && children.Count > 0)
                    {
                        store.Update(s =>
                        {
                            s.Downloads.First(x => x.NodeId == download.NodeId && x.Gid == download.Gid).Notified = true;
                            foreach (var child in children)
                                if (!s.Downloads.Any(x => x.NodeId == download.NodeId && x.Gid == child!.ToString())) s.Downloads.Add(new(download.NodeId, child!.ToString(), download.Name, DateTimeOffset.UtcNow));
                        });
                        continue;
                    }
                    var name = task["bittorrent"]?["info"]?["name"]?.ToString() ?? task["files"]?[0]?["path"]?.ToString() ?? download.Name;
                    if (snapshot.Settings.PushEnabled) await Push("下载完成", name, ct);
                    store.Update(s => s.Downloads.First(x => x.NodeId == download.NodeId && x.Gid == download.Gid).Notified = true);
                    store.Log("下载完成：" + name, "success");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "节点完成检查失败 {Node}", group.Key); }
        }
    }

    public async Task Push(string title, string description, CancellationToken ct)
    {
        var settings = store.Read().Settings;
        if (string.IsNullOrWhiteSpace(settings.PushKey)) throw new ArgumentException("请设置 PushDeer Key");
        using var client = http.Create();
        using var response = await client.PostAsync(Validation.HttpUrl(settings.PushEndpoint), new FormUrlEncodedContent(new Dictionary<string, string> { ["pushkey"] = settings.PushKey, ["text"] = title, ["desp"] = description, ["type"] = "text" }), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>(ct);
        if (body?["code"]?.ToString() is { } code && code != "0") throw new InvalidOperationException("PushDeer 推送失败");
    }
}
