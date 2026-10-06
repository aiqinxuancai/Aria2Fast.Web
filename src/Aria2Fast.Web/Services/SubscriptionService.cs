using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class SubscriptionService(StateStore store, FeedService feeds, AriaRpc rpc, AiService ai, DownloadPaths paths)
{
    private readonly SemaphoreSlim gate = new(1);
    public bool Checking { get; private set; }

    public async Task<SubscriptionCheckResult> Check(string? id = null, CancellationToken ct = default, bool redownload = false)
    {
        if (redownload && string.IsNullOrWhiteSpace(id)) throw new ArgumentException("请选择要重新下载的订阅");
        await gate.WaitAsync(ct);
        Checking = true;
        var submitted = 0;
        var skippedCount = 0;
        var errors = new List<string>();
        try
        {
            if (redownload && !store.Read().Subscriptions.Any(s => s.Id == id)) throw new ArgumentException("订阅不存在");
            foreach (var subscription in store.Read().Subscriptions.Where(s => (s.Enabled || redownload) && (id == null || s.Id == id)))
            {
                try
                {
                    var feed = await feeds.Fetch(subscription.Url, ct);
                    var history = subscription.History.Select(h => h.Key).ToHashSet();
                    foreach (var item in feed.Items.AsEnumerable().Reverse())
                    {
                        if (!redownload && (history.Contains(item.Key) || subscription.History.Any(x => x.Url == item.Url))) continue;
                        if (!store.Read().Subscriptions.Any(x => x.Id == subscription.Id && (x.Enabled || redownload))) break;
                        if (!Validation.Matches(item.Title, subscription.Filter, subscription.IsFilterRegex)) continue;
                        if (!string.IsNullOrWhiteSpace(subscription.ExcludeFilter) && Validation.Matches(item.Title, subscription.ExcludeFilter, subscription.IsFilterRegex)) continue;
                        var skipped = !redownload && !subscription.Initialized && subscription.SkipExisting;
                        string? gid = null;
                        if (!skipped)
                        {
                            var root = string.IsNullOrWhiteSpace(subscription.Directory) ? await paths.Default(subscription.NodeId, ct) : subscription.Directory;
                            var aiName = subscription.AutoDir ? await ai.Send(item.Title, "从文件标题中提取作品名称。只输出名称，不含季度、集数、字幕组、说明或标点包裹。", ct) : null;
                            var directory = DownloadPaths.Compose(root, subscription.NamePath, subscription.Season, aiName);
                            gid = await rpc.Add(item.Url, directory, subscription.NodeId, ct: ct);
                            submitted++;
                        }
                        else skippedCount++;
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
                    errors.Add(subscription.Name + "：" + ex.Message);
                }
            }
            return new(submitted, skippedCount, errors);
        }
        finally { Checking = false; gate.Release(); }
    }

}

public sealed record SubscriptionCheckResult(int Submitted, int Skipped, List<string> Errors);
