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
    public string MikanFallbackUrl { get; set; } = "https://mikanani.me";
    public string ProxyUrl { get; set; } = "";
    /// <summary>Fetch .torrent and Torznab download links on the server (through ProxyUrl) before submitting them to aria2. Mikan links are always fetched this way.</summary>
    public bool ProxyTorrentDownloads { get; set; } = true;
    public AnimeSourcesSettings AnimeSources { get => animeSources; set => animeSources = value ?? new(); }
    private AnimeSourcesSettings animeSources = new();
    public string TmdbApiKey { get; set; } = "";
    public string TavilyApiKey { get; set; } = "";
    /// <summary>Optional: without it Keenable's free keyless endpoint is used (with a lower rate limit).</summary>
    public string KeenableApiKey { get; set; } = "";
    /// <summary>For DeepSeek's web search and fetch APIs; empty uses the key of an AI profile pointing at api.deepseek.com.</summary>
    public string DeepSeekApiKey { get; set; } = "";
    /// <summary>How the AI agents read web pages: direct (this server fetches the page) or deepseek (DeepSeek's fetch API, billed per call).</summary>
    public string FetchProvider { get; set; } = "direct";
    /// <summary>Web search tried first by the AI agents: keenable, brave, serper, serpapi, tavily, or none to switch searching off.</summary>
    public string SearchProvider { get; set; } = "keenable";
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
    public bool BarkEnabled { get; set; }
    public string BarkEndpoint { get; set; } = "https://api.day.app/";
    public string BarkKey { get; set; } = "";
    /// <summary>Address this site is reached at from outside (e.g. behind a reverse proxy); notifications link to the player with it.</summary>
    public string PublicUrl { get; set; } = "";
    public OssSettings Oss { get; set; } = new();
    public LibrarySettings Library { get => library; set => library = value ?? new(); }
    private LibrarySettings library = new();
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

/// <summary>Anime matches Bangumi first; series (TV dramas) and movies match TMDB first when a key is set.</summary>
public enum LibraryCategory { Anime, Series, Movie }

public sealed class LibraryFolder
{
    /// <summary>Stable short name; videos outside the download directory are stored as "@{Id}/relative path".</summary>
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public LibraryCategory Category { get; set; }
}

public sealed class LibrarySettings
{
    public const string DefaultBangumiApiUrl = "https://api.bgm.tv";
    public bool Enabled { get; set; } = true;
    public bool FetchMetadata { get; set; } = true;
    public int ScanIntervalMinutes { get; set; } = 60;
    /// <summary>Bangumi API root, or a mirror / reverse proxy of it.</summary>
    public string BangumiApiUrl { get; set; } = DefaultBangumiApiUrl;
    /// <summary>
    /// Folders the library indexes, each with a category. Relative paths (and the empty path) are inside the download directory,
    /// so the defaults are the download directory itself for anime plus its tvshows and movies folders.
    /// </summary>
    public List<LibraryFolder> Folders { get => folders; set => folders = value ?? DefaultFolders(); }
    private List<LibraryFolder> folders = DefaultFolders();
    public static List<LibraryFolder> DefaultFolders() =>
    [
        new() { Id = "downloads", Path = "", Category = LibraryCategory.Anime },
        new() { Id = "tvshows", Path = "tvshows", Category = LibraryCategory.Series },
        new() { Id = "movies", Path = "movies", Category = LibraryCategory.Movie }
    ];
    /// <summary>When normal matching finds nothing, let the AI agent identify the work (each work once; needs an AI service).</summary>
    public bool AiScrape { get; set; } = true;
    /// <summary>Write Kodi-style NFO files and artwork beside the videos, read by Jellyfin, Emby and Kodi.</summary>
    public bool WriteNfo { get; set; } = true;
    /// <summary>Use ffmpeg (when found) to remux or transcode videos the browser cannot play directly.</summary>
    public bool Transcoding { get; set; } = true;
    /// <summary>ffmpeg executable or the folder holding ffmpeg and ffprobe; empty searches the application folder and PATH.</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>libx264 (software) or a hardware H.264 encoder such as h264_nvenc, h264_qsv, h264_vaapi, h264_videotoolbox, h264_amf.</summary>
    public string VideoEncoder { get; set; } = "libx264";
    /// <summary>Transcoded height limit; 0 keeps the original size.</summary>
    public int MaxHeight { get; set; } = 1080;
    /// <summary>Videos converted at the same time; further players wait for one to stop.</summary>
    public int MaxTranscodes { get; set; } = 2;
    /// <summary>Render device for h264_vaapi (Linux).</summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
}

public sealed class AnimeSourcesSettings
{
    // Setters tolerate null from older clients or hand-edited state files.
    public SearchSourceConfig AcgRip { get => acgRip; set => acgRip = value ?? new(); }
    public SearchSourceConfig Nyaa { get => nyaa; set => nyaa = value ?? new(); }
    public SearchSourceConfig Dmhy { get => dmhy; set => dmhy = value ?? new(); }
    public SearchSourceConfig Acgnx { get => acgnx; set => acgnx = value ?? new(); }
    public List<TorznabEndpoint> Torznab { get => torznab; set => torznab = value ?? []; }
    private SearchSourceConfig acgRip = new() { Enabled = true, BaseUrl = "https://acg.rip" };
    private SearchSourceConfig nyaa = new() { Enabled = true, BaseUrl = "https://nyaa.si" };
    private SearchSourceConfig dmhy = new() { Enabled = true, BaseUrl = "https://share.dmhy.org" };
    private SearchSourceConfig acgnx = new() { Enabled = false, BaseUrl = "https://share.acgnx.se" };
    private List<TorznabEndpoint> torznab = [];
}

public sealed class SearchSourceConfig
{
    public bool Enabled { get; set; }
    /// <summary>Site root or mirror / reverse proxy root; blank uses the built-in default.</summary>
    public string BaseUrl { get; set; } = "";
}

public sealed class TorznabEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Prowlarr / Jackett";
    /// <summary>Torznab API URL, e.g. http://127.0.0.1:9696/1/api.</summary>
    public string Url { get; set; } = "";
    /// <summary>Sent as the apikey query parameter on the server; never stored in subscription URLs.</summary>
    public string ApiKey { get; set; } = "";
    public string Categories { get; set; } = "5070";
    public bool Enabled { get; set; } = true;
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
    /// <summary>The model accepts image input; the AI scraping agent may then look at candidate posters.</summary>
    public bool Vision { get; set; }
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
    public bool PushDeerNotified { get; set; }
    public bool BarkNotified { get; set; }
}

public sealed class TrackerCache
{
    public List<string> Urls { get; set; } = [];
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? Error { get; set; }
}
