using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class SubscriptionService(StateStore store, FeedService feeds, AriaRpc rpc, AiService ai)
{
    private readonly SemaphoreSlim gate = new(1);
    public bool Checking { get; private set; }

    public async Task Check(string? id = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        Checking = true;
        try
        {
            foreach (var subscription in store.Read().Subscriptions.Where(s => s.Enabled && (id == null || s.Id == id)))
            {
                try
                {
                    var feed = await feeds.Fetch(subscription.Url, ct);
                    var history = subscription.History.Select(h => h.Key).ToHashSet();
                    foreach (var item in feed.Items.AsEnumerable().Reverse())
                    {
                        if (history.Contains(item.Key) || subscription.History.Any(x => x.Url == item.Url)) continue;
                        if (!store.Read().Subscriptions.Any(x => x.Id == subscription.Id && x.Enabled)) break;
                        if (!Validation.Matches(item.Title, subscription.Filter, subscription.IsFilterRegex)) continue;
                        if (!string.IsNullOrWhiteSpace(subscription.ExcludeFilter) && Validation.Matches(item.Title, subscription.ExcludeFilter, subscription.IsFilterRegex)) continue;
                        var skipped = !subscription.Initialized && subscription.SkipExisting;
                        string? gid = null;
                        if (!skipped)
                        {
                            var node = rpc.Node(subscription.NodeId);
                            var directory = string.IsNullOrWhiteSpace(subscription.Directory) ? node.DownloadDirectory : subscription.Directory;
                            if (string.IsNullOrWhiteSpace(directory) && node.Id == "local") directory = store.Read().Settings.DownloadDirectory;
                            if (!string.IsNullOrWhiteSpace(subscription.NamePath)) directory = Join(directory, subscription.NamePath);
                            if (subscription.AutoDir) directory = Join(directory, Validation.Segment(await ai.Send(item.Title, "从文件标题中提取作品名称。只输出名称，不含季度、集数、字幕组、说明或标点包裹。", ct)));
                            if (subscription.Season > 0) directory = Join(directory, $"Season {subscription.Season}");
                            gid = await rpc.Add(item.Url, directory, subscription.NodeId, ct: ct);
                        }
                        var record = new SubscriptionEntry(item.Key, item.Title, item.Url, gid, DateTimeOffset.UtcNow, skipped);
                        store.Update(s => s.Subscriptions.FirstOrDefault(x => x.Id == subscription.Id)?.History.Add(record));
                    }
                    store.Update(s =>
                    {
                        var current = s.Subscriptions.FirstOrDefault(x => x.Id == subscription.Id);
                        if (current == null) return;
                        current.Initialized = true;
                        current.LastChecked = DateTimeOffset.UtcNow;
                        current.LastError = null;
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    store.Update(s =>
                    {
                        var current = s.Subscriptions.FirstOrDefault(x => x.Id == subscription.Id);
                        if (current != null) { current.LastError = ex.Message; current.LastChecked = DateTimeOffset.UtcNow; }
                    });
                    store.Log($"订阅 {subscription.Name}：{ex.Message}", "error");
                }
            }
        }
        finally { Checking = false; gate.Release(); }
    }

    private static string Join(string root, string name) => string.IsNullOrWhiteSpace(root) ? name : root.TrimEnd('/', '\\') + "/" + name;
}
