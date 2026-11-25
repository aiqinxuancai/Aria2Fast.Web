using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services.Sources;

public sealed record AnimeSearchQuery(string Keyword, string? Category = null, string? Filter = null);
public sealed record AnimeSearchResponse(string SourceId, string SourceName, string? SubscribeRssUrl, List<FeedItem> Items, string? Error = null);
public sealed record SearchOption(string Value, string Label);
public sealed record AnimeSourceInfo(string Id, string Name, string Kind, List<SearchOption> Categories, List<SearchOption> Filters);

/// <summary>A search / list type download site. Every query maps to an RSS URL, which also serves as the one-click subscription address.</summary>
public interface IAnimeSearchSource
{
    string Id { get; }
    string Name { get; }
    AnimeSourceInfo Info { get; }
    /// <summary>Builds the feed URL; throws <see cref="ArgumentException"/> for an unsupported category or filter. Never contains secrets.</summary>
    string FeedUrl(AnimeSearchQuery query);
}

/// <summary>Built-in public sites. BaseUrl may point at a mirror or reverse proxy, including one with a path prefix.</summary>
public sealed class RssSearchSource(string id, string name, string baseUrl, List<SearchOption> categories, List<SearchOption> filters,
    Func<string, string, string, string, string> build) : IAnimeSearchSource
{
    public string Id => id;
    public string Name => name;
    public AnimeSourceInfo Info => new(id, name, "builtin", categories, filters);

    public string FeedUrl(AnimeSearchQuery query)
    {
        var category = Pick(categories, query.Category, "分类");
        var filter = Pick(filters, query.Filter, "筛选条件");
        return build(baseUrl.TrimEnd('/'), Uri.EscapeDataString(query.Keyword), category, filter);
    }

    private static string Pick(List<SearchOption> options, string? value, string label)
    {
        if (string.IsNullOrEmpty(value)) return options.FirstOrDefault()?.Value ?? "";
        return options.Any(o => o.Value == value) ? value : throw new ArgumentException("不支持的" + label);
    }

    public static readonly Dictionary<string, (string Name, string DefaultUrl)> Defaults = new()
    {
        ["acgrip"] = ("ACG.RIP", "https://acg.rip"),
        ["nyaa"] = ("Nyaa", "https://nyaa.si"),
        ["dmhy"] = ("动漫花园", "https://share.dmhy.org"),
        ["acgnx"] = ("末日动漫", "https://share.acgnx.se"),
    };

    public static List<IAnimeSearchSource> Enabled(AnimeSourcesSettings settings)
    {
        string Root(string id, SearchSourceConfig config) => string.IsNullOrWhiteSpace(config.BaseUrl) ? Defaults[id].DefaultUrl : config.BaseUrl.Trim();
        var list = new List<IAnimeSearchSource>();
        if (settings.AcgRip.Enabled)
            list.Add(new RssSearchSource("acgrip", Defaults["acgrip"].Name, Root("acgrip", settings.AcgRip),
                [new("", "全部"), new("1", "动画")], [],
                (root, q, c, _) => $"{root}/{c}.xml?term={q}"));
        if (settings.Nyaa.Enabled)
            list.Add(new RssSearchSource("nyaa", Defaults["nyaa"].Name, Root("nyaa", settings.Nyaa),
                [new("1_0", "动画（全部）"), new("1_3", "非英文字幕"), new("1_2", "英文字幕"), new("1_4", "生肉")],
                [new("0", "全部"), new("1", "排除重制"), new("2", "仅可信")],
                (root, q, c, f) => $"{root}/?page=rss&q={q}&c={c}&f={f}"));
        if (settings.Dmhy.Enabled)
            list.Add(new RssSearchSource("dmhy", Defaults["dmhy"].Name, Root("dmhy", settings.Dmhy),
                [new("", "全部"), new("2", "动画"), new("31", "季度全集")], [],
                (root, q, c, _) => $"{root}/topics/rss/rss.xml?keyword={q}" + (c.Length > 0 ? "&sort_id=" + c : "")));
        if (settings.Acgnx.Enabled)
            list.Add(new RssSearchSource("acgnx", Defaults["acgnx"].Name, Root("acgnx", settings.Acgnx), [], [],
                (root, q, _, _) => $"{root}/rss.xml?keyword={q}"));
        return list;
    }
}

/// <summary>Torznab aggregator (Prowlarr / Jackett): one endpoint covers many public and private indexers.</summary>
public sealed class TorznabSearchSource(TorznabEndpoint endpoint) : IAnimeSearchSource
{
    public string Id => "torznab:" + endpoint.Id;
    public string Name => endpoint.Name;
    public AnimeSourceInfo Info => new(Id, Name, "torznab", [], []);

    public string FeedUrl(AnimeSearchQuery query) =>
        TorznabUrls.With(endpoint.Url, ("t", "search"), ("cat", endpoint.Categories), ("q", query.Keyword));
}

public static class TorznabUrls
{
    private static readonly string[] SecretParameters = ["apikey", "jackett_apikey"];

    public static bool SameEndpoint(Uri uri, string endpointUrl) =>
        Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint)
        && uri.Scheme == endpoint.Scheme
        && uri.Authority.Equals(endpoint.Authority, StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.TrimEnd('/').Equals(endpoint.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds the configured API key to requests for a Torznab endpoint so stored URLs never need to contain it.</summary>
    public static string WithApiKey(string url, WebSettings settings)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || Parameters(uri.Query).Any(p => p.Name.Equals("apikey", StringComparison.OrdinalIgnoreCase))) return url;
        var endpoint = settings.AnimeSources.Torznab.FirstOrDefault(e => e.ApiKey.Length > 0 && SameEndpoint(uri, e.Url));
        return endpoint == null ? url : With(url, ("apikey", endpoint.ApiKey));
    }

    public static string With(string url, params (string Name, string Value)[] parameters)
    {
        var builder = new UriBuilder(url);
        var query = Parameters(builder.Query).Where(p => !parameters.Any(n => n.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Raw)
            .Concat(parameters.Where(p => p.Value.Length > 0).Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value)));
        builder.Query = string.Join("&", query);
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>Replaces API key values with *** for display, backups and logs.</summary>
    public static string Mask(string url)
    {
        var mark = url.IndexOf('?');
        if (mark < 0) return url;
        var end = url.IndexOf('#', mark);
        var query = end < 0 ? url[(mark + 1)..] : url[(mark + 1)..end];
        var masked = string.Join("&", query.Split('&').Select(part =>
        {
            var name = part.Split('=', 2)[0];
            return SecretParameters.Contains(Uri.UnescapeDataString(name), StringComparer.OrdinalIgnoreCase) ? name + "=***" : part;
        }));
        return url[..(mark + 1)] + masked + (end < 0 ? "" : url[end..]);
    }

    private static IEnumerable<(string Name, string Raw)> Parameters(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(raw => (Uri.UnescapeDataString(raw.Split('=', 2)[0]), raw));
}
