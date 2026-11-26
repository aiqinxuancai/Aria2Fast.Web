using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Web search for the AI agents. Keenable works without a key (its public endpoint only asks for an application name), so it is
/// the default; the others need keys. The chosen provider goes first and the rest provide automatic failover.
/// </summary>
public sealed class ResearchSearchTools(WebSettings settings, Func<HttpClient> createClient)
{
    public static readonly string[] All = ["keenable", "brave", "serper", "serpapi", "tavily"];
    // Identifies this application to Keenable's keyless endpoint; nothing about the user is sent.
    private const string KeenableTitle = "Aria2Fast.Web";

    public IReadOnlyList<string> Providers => settings.SearchProvider == "none" ? [] :
        All.Where(p => p == "keenable" || !string.IsNullOrWhiteSpace(Key(p))).OrderBy(p => p == settings.SearchProvider ? 0 : 1).ToArray();
    private string Key(string provider) => provider switch
    {
        "keenable" => settings.KeenableApiKey, "brave" => settings.BraveApiKey, "serper" => settings.SerperApiKey,
        "serpapi" => settings.SerpApiKey, "tavily" => settings.TavilyApiKey, _ => ""
    };

    public async Task<IReadOnlyList<AiResearchSource>> Search(string query, string provider, CancellationToken ct)
    {
        if (query.Length is < 2 or > 300) throw new ArgumentException("检索词长度应为2到300字");
        var available = Providers;
        if (provider != "auto" && !available.Contains(provider)) throw new ArgumentException("搜索工具未配置");
        // An explicit choice is tried first; configured alternatives provide automatic failover.
        var ordered = provider == "auto" ? available : new[] { provider }.Concat(available.Where(p => p != provider)).ToArray();
        var errors = new List<string>();
        foreach (var current in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = Request(current, query);
                using var client = createClient();
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) { errors.Add($"{current}: HTTP {(int)response.StatusCode}"); continue; }
                var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new InvalidDataException("搜索返回为空");
                var results = Normalize(current, json);
                if (results.Count > 0) return results;
                errors.Add(current + ": 无结果");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Never return request URLs/exceptions: SerpApi credentials are in the query string.
                errors.Add(current + ": 请求失败或超时");
            }
        }
        throw new InvalidDataException("搜索未获得结果：" + string.Join("；", errors));
    }

    private HttpRequestMessage Request(string provider, string query)
    {
        var encoded = Uri.EscapeDataString(query);
        var request = provider switch
        {
            "brave" => new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?q={encoded}&count=5"),
            "serper" => new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search") { Content = JsonContent.Create(new { q = query, num = 5 }) },
            "serpapi" => new HttpRequestMessage(HttpMethod.Get, $"https://serpapi.com/search.json?engine=google&q={encoded}&num=5&api_key={Uri.EscapeDataString(Key(provider))}"),
            "tavily" => new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search") { Content = JsonContent.Create(new { api_key = Key(provider), query, max_results = 5, search_depth = "basic", include_answer = false }) },
            "keenable" => new HttpRequestMessage(HttpMethod.Post, string.IsNullOrWhiteSpace(Key(provider)) ? "https://api.keenable.ai/v1/search/public" : "https://api.keenable.ai/v1/search")
                { Content = JsonContent.Create(new { query, max_results = 5 }) },
            _ => throw new ArgumentException("未知搜索工具")
        };
        if (provider == "keenable")
        {
            request.Headers.Add("X-Keenable-Title", KeenableTitle);
            if (!string.IsNullOrWhiteSpace(Key(provider))) request.Headers.Add("X-API-Key", Key(provider).Trim());
        }
        if (provider == "brave") request.Headers.Add("X-Subscription-Token", Key(provider));
        if (provider == "serper") request.Headers.Add("X-API-KEY", Key(provider));
        return request;
    }

    public static IReadOnlyList<AiResearchSource> Normalize(string provider, JsonNode json)
    {
        var rows = (provider switch { "brave" => json["web"]?["results"], "serper" => json["organic"], "serpapi" => json["organic_results"], "tavily" or "keenable" => json["results"], _ => null }) as JsonArray;
        // Keenable keeps a deprecated, always empty "description" beside "snippet", so take the first text that is not empty.
        return (rows ?? []).OfType<JsonObject>().Select(x => new AiResearchSource(0, x["title"]?.ToString() ?? "",
            (x["url"] ?? x["link"])?.ToString() ?? "", new[] { "content", "snippet", "description" }.Select(k => x[k]?.ToString() ?? "").FirstOrDefault(s => s.Length > 0) ?? ""))
            .Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
            .DistinctBy(x => x.Url).Take(5).ToArray();
    }
}
