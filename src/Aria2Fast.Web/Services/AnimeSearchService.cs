using System.Diagnostics;
using System.Net;
using System.Xml;
using System.Xml.Linq;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services.Sources;
using Microsoft.Extensions.Caching.Memory;

namespace Aria2Fast.Web.Services;

/// <summary>Searches enabled download sources in parallel; one failing or slow site never blocks the others.</summary>
public sealed class AnimeSearchService(StateStore store, FeedService feeds, HttpGateway http, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(20);
    private const int MaxItemsPerSource = 200;

    public static List<IAnimeSearchSource> Enabled(AnimeSourcesSettings settings) =>
        [.. RssSearchSource.Enabled(settings), .. settings.Torznab.Where(e => e.Enabled).Select(e => new TorznabSearchSource(e))];

    public List<AnimeSourceInfo> Sources() => Enabled(store.Read().Settings.AnimeSources).Select(s => s.Info).ToList();

    public async Task<List<AnimeSearchResponse>> Search(string? source, string? keyword, string? category, string? filter, bool refresh, CancellationToken ct)
    {
        keyword = keyword?.Trim() ?? "";
        if (keyword.Length is < 1 or > 100) throw new ArgumentException("请输入 1 至 100 个字符的关键词");
        var sources = Enabled(store.Read().Settings.AnimeSources);
        var all = string.IsNullOrEmpty(source) || source == "all";
        var selected = all ? sources : sources.Where(s => s.Id == source).ToList();
        if (selected.Count == 0) throw new ArgumentException(all ? "尚未启用任何资源搜索源，请在设置中心开启" : "搜索源不存在或未启用");
        // Categories differ per site, so they only apply to single-source searches. Invalid values are rejected before any request.
        var query = new AnimeSearchQuery(keyword, all ? null : category, all ? null : filter);
        var requests = selected.Select(s => (Source: s, Url: s.FeedUrl(query))).ToList();
        return [.. await Task.WhenAll(requests.Select(r => SearchOne(r.Source, r.Url, refresh, ct)))];
    }

    private async Task<AnimeSearchResponse> SearchOne(IAnimeSearchSource source, string url, bool refresh, CancellationToken ct)
    {
        var key = "anime-search:" + source.Id + ":" + url;
        if (!refresh && cache.TryGetValue<List<FeedItem>>(key, out var hit)) return new(source.Id, source.Name, url, hit!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourceTimeout);
        try
        {
            var items = (await feeds.Fetch(url, timeout.Token)).Items.Take(MaxItemsPerSource).ToList();
            cache.Set(key, items, CacheDuration);
            return new(source.Id, source.Name, url, items);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(source.Id, source.Name, url, [], Describe(ex));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "请求超时，请检查网络代理或改用镜像地址",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable or (HttpStatusCode)429 } http =>
            $"源站拒绝访问（HTTP {(int)http.StatusCode!}），可能触发了人机验证。可改用镜像地址，或通过 Prowlarr / Jackett 接入",
        HttpRequestException { StatusCode: { } status } => $"源站返回 HTTP {(int)status}",
        HttpRequestException http => "无法连接源站：" + http.Message,
        XmlException => "返回内容不是 RSS，可能是人机验证页面或镜像地址有误",
        _ => ex.Message,
    };

    public async Task<object> TestTorznab(TorznabEndpoint endpoint, CancellationToken ct)
    {
        Validation.Torznab(endpoint);
        var timer = Stopwatch.StartNew();
        var xml = await http.GetTextAsync(TorznabUrls.With(endpoint.Url, ("t", "caps"), ("apikey", endpoint.ApiKey)), ct);
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { throw new InvalidDataException("返回内容不是 Torznab XML，请检查地址是否为 Torznab API（通常以 /api 结尾）"); }
        if (doc.Root?.Name.LocalName == "error")
            throw new InvalidOperationException("索引器返回错误：" + ((string?)doc.Root.Attribute("description") ?? "未知错误"));
        if (doc.Root?.Name.LocalName != "caps") throw new InvalidDataException("返回内容不是 Torznab 能力描述（caps）");
        var categories = doc.Descendants().Where(x => x.Name.LocalName is "category" or "subcat").ToList();
        var search = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "search");
        return new
        {
            title = (string?)doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "server")?.Attribute("title") ?? "",
            searchAvailable = search == null || (string?)search.Attribute("available") != "no",
            categories = categories.Count,
            anime = categories.Any(c => (string?)c.Attribute("id") == "5070" || ((string?)c.Attribute("name") ?? "").Contains("Anime", StringComparison.OrdinalIgnoreCase)),
            elapsedMs = timer.ElapsedMilliseconds,
        };
    }
}
