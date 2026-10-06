using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>Provider priority and wire formats follow QQRebotPlugin's web search tools.</summary>
public sealed class ResearchSearchTools(WebSettings settings, Func<HttpClient> createClient)
{
    public IReadOnlyList<string> Providers => new[] { "brave", "serper", "serpapi", "tavily" }.Where(p => !string.IsNullOrWhiteSpace(Key(p))).ToArray();
    private string Key(string provider) => provider switch
    {
        "brave" => settings.BraveApiKey, "serper" => settings.SerperApiKey,
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
            _ => throw new ArgumentException("未知搜索工具")
        };
        if (provider == "brave") request.Headers.Add("X-Subscription-Token", Key(provider));
        if (provider == "serper") request.Headers.Add("X-API-KEY", Key(provider));
        return request;
    }

    public static IReadOnlyList<AiResearchSource> Normalize(string provider, JsonNode json)
    {
        var rows = (provider switch { "brave" => json["web"]?["results"], "serper" => json["organic"], "serpapi" => json["organic_results"], "tavily" => json["results"], _ => null }) as JsonArray;
        return (rows ?? []).OfType<JsonObject>().Select(x => new AiResearchSource(0, x["title"]?.ToString() ?? "",
            (x["url"] ?? x["link"])?.ToString() ?? "", (x["content"] ?? x["description"] ?? x["snippet"])?.ToString() ?? ""))
            .Where(x => Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
            .DistinctBy(x => x.Url).Take(5).ToArray();
    }
}
