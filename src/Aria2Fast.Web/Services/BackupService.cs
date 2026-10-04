using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aliyun.OSS;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class BackupService(StateStore store)
{
    public byte[] Export() => JsonSerializer.SerializeToUtf8Bytes(store.Read().Subscriptions, StateStore.Json);
    public int Import(string json)
    {
        var array = JsonNode.Parse(json)?.AsArray() ?? throw new ArgumentException("请导入订阅 JSON 数组");
        if (array.Count > 5000) throw new ArgumentException("订阅数量超过限制");
        var list = new List<Subscription>();
        foreach (var item in array)
        {
            if (item is not JsonObject) throw new ArgumentException("订阅项目必须为 JSON 对象");
            var sub = item.Deserialize<Subscription>(StateStore.Json) ?? throw new ArgumentException("订阅数据为空");
            // Desktop subscription exports use Path and AlreadyAddedDownloadModel.
            if (item["Path"] is { } path) sub.Directory = path.ToString();
            if (item["AlreadyAddedDownloadModel"] is JsonArray history)
            {
                sub.History = history.Select(x => new SubscriptionEntry(x?["Url"]?.ToString() ?? "", x?["Name"]?.ToString() ?? "", x?["Url"]?.ToString() ?? "", null, DateTimeOffset.UtcNow, true)).ToList();
                sub.Initialized = true;
            }
            Validation.Subscription(sub, store.Read());
            list.Add(sub);
        }
        return store.Update(s =>
        {
            var count = 0;
            foreach (var sub in list)
            {
                var existing = s.Subscriptions.FirstOrDefault(x => x.Url == sub.Url && x.NodeId == sub.NodeId);
                if (existing != null)
                {
                    foreach (var record in sub.History)
                        if (!existing.History.Any(h => h.Key == record.Key || h.Url == record.Url)) existing.History.Add(record);
                    existing.Initialized |= sub.Initialized;
                    continue;
                }
                sub.Id = Guid.NewGuid().ToString("N"); s.Subscriptions.Add(sub); count++;
            }
            return count;
        });
    }
    private (OssClient Client, OssSettings Options) Client()
    {
        var options = store.Read().Settings.Oss;
        Validation.HttpUrl(options.Endpoint);
        if (string.IsNullOrWhiteSpace(options.Bucket) || string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.AccessKeySecret)) throw new ArgumentException("请完整配置 OSS");
        return (new OssClient(options.Endpoint, options.AccessKeyId, options.AccessKeySecret), options);
    }
    public object Upload()
    {
        var (client, options) = Client();
        using var stream = new MemoryStream(Export());
        client.PutObject(options.Bucket, options.ObjectKey, stream);
        return new { ok = true };
    }
    public object Download()
    {
        var (client, options) = Client();
        using var stream = client.GetObject(options.Bucket, options.ObjectKey).Content;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new { imported = Import(reader.ReadToEnd()) };
    }
}
