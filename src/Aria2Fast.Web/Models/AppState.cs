using Aria2Fast.Service;

namespace Aria2Fast.Web.Models;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public WebSettings Settings { get; set; } = new();
    public List<AriaNode> Nodes { get; set; } = [new() { Id = "local", Name = "本地 Aria2", Url = "http://127.0.0.1:6800/jsonrpc" }];
    public string SelectedNodeId { get; set; } = "local";
    public List<Subscription> Subscriptions { get; set; } = [];
    public List<DownloadRecord> Downloads { get; set; } = [];
    public List<Notice> Notices { get; set; } = [];
    public Dictionary<string, AiReview> Reviews { get; set; } = [];
    public Dictionary<string, string> Translations { get; set; } = [];
    public TrackerCache Trackers { get; set; } = new();
    public string PasswordHash { get; set; } = "";
}

public sealed class WebSettings
{
    public bool LocalEnabled { get; set; } = true;
    public string Aria2Executable { get; set; } = "";
    public int LocalRpcPort { get; set; } = 6800;
    public int LocalBtPort { get; set; } = 16888;
    public int LocalDhtPort { get; set; } = 16888;
    public bool TrackerAutoUpdate { get; set; }
    public string TrackerSources { get; set; } = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";
    public int TrackerUpdateHours { get; set; } = 24;
    public string DownloadDirectory { get; set; } = "";
    public int SubscriptionIntervalMinutes { get; set; } = 15;
    public string MikanBaseUrl { get; set; } = "https://mikanime.tv";
    public string ProxyUrl { get; set; } = "";
    public string TmdbApiKey { get; set; } = "";
    public string TavilyApiKey { get; set; } = "";
    public string BraveApiKey { get; set; } = "";
    public string SerperApiKey { get; set; } = "";
    public string SerpApiKey { get; set; } = "";
    public bool TranslateSummary { get; set; }
    public bool AutoReview { get; set; }
    public List<AiProfile> AiProfiles { get; set; } = [];
    public string SelectedAiId { get; set; } = "";
    public bool PushEnabled { get; set; }
    public string PushEndpoint { get; set; } = "https://api2.pushdeer.com/message/push";
    public string PushKey { get; set; } = "";
    public OssSettings Oss { get; set; } = new();
    public Dictionary<string, string> LocalOptions { get; set; } = [];
}

public sealed class OssSettings
{
    public bool AutoSync { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";
    public string ObjectKey { get; set; } = "aria2fast-web/subscriptions.json";
}

public sealed class AriaNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "远程节点";
    public string Url { get; set; } = "http://127.0.0.1:6800/jsonrpc";
    public string Secret { get; set; } = "";
    public string DownloadDirectory { get; set; } = "";
}

public sealed class AiProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "AI 配置";
    public AiProtocolType Protocol { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed record Notice(DateTimeOffset Time, string Level, string Message, string? Details = null);
public sealed record AiReview(double? Score, string Review, DateTimeOffset CreatedAt, string Sources = "")
{
    public string Overview { get; init; } = "";
    public string OriginalWork { get; init; } = "";
    public string Adaptation { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Caveats { get; init; } = "";
    public List<AiResearchSource> References { get; init; } = [];
    public List<string> Queries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string Model { get; init; } = "";
    public int Version { get; init; }
}
public sealed record AiResearchSource(int Id, string Title, string Url, string Content);
public sealed record DownloadRecord(string NodeId, string Gid, string Name, DateTimeOffset AddedAt)
{
    public bool Notified { get; set; }
}

public sealed class TrackerCache
{
    public List<string> Urls { get; set; } = [];
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Error { get; set; }
}
