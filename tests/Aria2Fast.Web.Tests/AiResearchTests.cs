using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
using Microsoft.Extensions.Configuration;

static class AiResearchTests
{
    static void Check(bool condition) { if (!condition) throw new Exception("Research assertion failed"); }
    static string Finish(string citation = "") => JsonSerializer.Serialize(new { action = "finish", score = 8, overview = "作品身份" + citation,
        originalWork = "原作作者" + citation, adaptation = "动画改编" + citation, review = "评析" + citation, recommendation = "观看建议", caveats = "状态待核实" });
    static Task<string> Answer(string text) => Task.FromResult(text);
    static async Task Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static async Task<int> Run()
    {
        var count = 0;
        async Task Test(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS " + name); }
        await Test("Agent observes search and fetched pages before adaptive follow-up; deduplicates citations", async () =>
        {
            var turn = 0;
            var searches = new List<string>();
            var agent = new AnimeReviewAgent((prompt, instruction, ct) =>
            {
                var context = JsonNode.Parse(prompt)!;
                Check(instruction.Contains("不可信"));
                return Answer(++turn switch
                {
                    1 => "{\"action\":\"search\",\"query\":\"作品 官方\",\"provider\":\"brave\"}",
                    2 when context["observations"]!.ToJsonString().Contains("Official") => "{\"action\":\"fetch\",\"url\":\"https://example.com/anime\"}",
                    3 when context["observations"]!.ToJsonString().Contains("AuthorName") => "{\"action\":\"search\",\"query\":\"AuthorName 原作 出版社\",\"provider\":\"serper\"}",
                    _ => Finish("[1]")
                });
            }, (query, provider, ct) =>
            {
                searches.Add(provider + ":" + query);
                return Task.FromResult<IReadOnlyList<AiResearchSource>>([new(0, "Official", "https://example.com/anime", "Original summary")]);
            }, ["brave", "serper"], (url, ct) => Task.FromResult(new WebFetchService.FetchResult(url, url, 200, "text/html", "Official", "AuthorName", false, true, "")));
            var review = await agent.Run("作品", "简介", CancellationToken.None);
            Check(turn == 4 && searches.Count == 2 && searches[1].Contains("AuthorName"));
            Check(review.References.Count == 1 && review.References[0].Content == "AuthorName" && review.Score == 8 && review.Version == 2);
        });
        await Test("Agent preserves JSON numeric scores and uses the last duplicate field", async () =>
        {
            foreach (var (json, expected) in new (string, double?)[]
            {
                ("8", 8), ("8.5", 8.5), ("8e0", 8), ("null", null),
                ("\"8\"", null), ("0", null), ("11", null), ("1e400", null),
                ("2,\"score\":8", 8)
            })
            {
                var turn = 0;
                var agent = new AnimeReviewAgent((p, i, c) => Answer(++turn == 1
                    ? "{\"action\":\"search\",\"query\":\"作品 官方\"}"
                    : Finish("[1]").Replace("\"score\":8", "\"score\":" + json)),
                    (q, p, c) => Task.FromResult<IReadOnlyList<AiResearchSource>>(
                        [new(0, "Official", "https://example.com/anime", "Evidence")]), ["brave"]);
                var review = await agent.Run("作品", "简介", default);
                if (review.Score != expected)
                    throw new Exception($"Score {json}: expected {expected}, got {review.Score}");
            }
        });
        await Test("Agent repairs invalid JSON and fabricated source identifiers", async () =>
        {
            var turn = 0;
            var agent = new AnimeReviewAgent((p, i, c) => Answer(++turn switch { 1 => "not JSON", 2 => Finish("[999]"), _ => Finish() }), null);
            var review = await agent.Run("作品", "简介", default);
            Check(turn == 3 && review.Score is null && review.Warnings.Count > 0);
        });
        await Test("Search failure is an observation, not a fabricated successful source", async () =>
        {
            var turn = 0;
            var agent = new AnimeReviewAgent((p, i, c) => Answer(++turn == 1 ? "{\"action\":\"search\",\"query\":\"作品 原作\"}" : Finish()),
                (q, p, c) => throw new HttpRequestException("secret must not appear"), ["tavily"]);
            var review = await agent.Run("作品", "简介", default);
            Check(review.Score is null && review.References.Count == 0 && review.Warnings.Count >= 1 && !JsonSerializer.Serialize(review).Contains("secret"));
        });
        await Test("Agent tool and iteration budgets stop runaway searches", async () =>
        {
            var turn = 0; var calls = 0;
            var agent = new AnimeReviewAgent((p, i, c) => Answer(JsonSerializer.Serialize(new { action = "search", query = "query " + ++turn })),
                (q, p, c) => { calls++; return Task.FromResult<IReadOnlyList<AiResearchSource>>([]); }, ["brave"]);
            await Fails<InvalidDataException>(async () => await agent.Run("作品", "简介", default));
            Check(calls == AnimeReviewAgent.MaxSearches && turn == 16);
        });
        await Test("Agent cancellation propagates without producing a result", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var agent = new AnimeReviewAgent((p, i, c) => throw new Exception("must not call"), null);
            await Fails<OperationCanceledException>(async () => await agent.Run("作品", "简介", cancelled.Token));
        });
        var fixtures = new Dictionary<string, string>
        {
            ["brave"] = "{\"web\":{\"results\":[{\"title\":\"Official\",\"url\":\"https://example.com\",\"description\":\"Fact\"}]}}",
            ["serper"] = "{\"organic\":[{\"title\":\"Official\",\"link\":\"https://example.com\",\"snippet\":\"Fact\"}]}",
            ["serpapi"] = "{\"organic_results\":[{\"title\":\"Official\",\"link\":\"https://example.com\",\"snippet\":\"Fact\"}]}",
            ["tavily"] = "{\"results\":[{\"title\":\"Official\",\"url\":\"https://example.com\",\"content\":\"Fact\"}]}"
        };
        foreach (var provider in fixtures.Keys)
            await Test(provider + " request authentication and result normalization", async () =>
            {
                var settings = new WebSettings();
                typeof(WebSettings).GetProperty(provider switch { "brave" => "BraveApiKey", "serper" => "SerperApiKey", "serpapi" => "SerpApiKey", _ => "TavilyApiKey" })!.SetValue(settings, "test-key");
                var tools = new ResearchSearchTools(settings, () => new HttpClient(new Handler(async request =>
                {
                    if (provider == "brave") Check(request.Headers.GetValues("X-Subscription-Token").Single() == "test-key");
                    if (provider == "serper") Check(request.Headers.GetValues("X-API-KEY").Single() == "test-key");
                    if (provider == "serpapi") Check(request.RequestUri!.Query.Contains("api_key=test-key"));
                    if (provider == "tavily") Check((await request.Content!.ReadAsStringAsync()).Contains("test-key"));
                    return new(HttpStatusCode.OK) { Content = new StringContent(fixtures[provider]) };
                })));
                var result = await tools.Search("原作 author", provider, default);
                Check(result.Single().Content == "Fact" && result.Single().Title == "Official");
            });
        await Test("Search automatically falls back across all four configured providers", async () =>
        {
            var hosts = new List<string>();
            var tools = new ResearchSearchTools(new() { BraveApiKey = "b", SerperApiKey = "s", SerpApiKey = "p", TavilyApiKey = "t" },
                () => new HttpClient(new Handler(request =>
                {
                    hosts.Add(request.RequestUri!.Host);
                    return Task.FromResult(new HttpResponseMessage(hosts.Count == 4 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests) { Content = new StringContent(fixtures["tavily"]) });
                })));
            Check((await tools.Search("原作作者", "auto", default)).Count == 1 && hosts.Count == 4);
        });
        await Test("Web fetch extracts text, exposes truncation and rejects private redirects", async () =>
        {
            using var client = new HttpClient(new Handler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<title>作品</title><script>secret script</script><p>Original author &amp; publisher</p>", System.Text.Encoding.UTF8, "text/html") })));
            var fetch = new WebFetchService(client);
            var page = await fetch.FetchAsync("https://example.com", 12);
            Check(page.ok && page.truncated && page.content.Length == 12 && !page.content.Contains("secret") && page.title == "作品");
            using var redirectClient = new HttpClient(new Handler(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("http://127.0.0.1/secrets"); return Task.FromResult(response);
            }));
            await Fails<ArgumentException>(async () => await new WebFetchService(redirectClient).FetchAsync("https://example.com"));
            foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "169.254.169.254", "::1", "::ffff:192.168.1.1", "fc00::1" }) Check(!WebFetchService.IsPublicAddress(IPAddress.Parse(address)));
            await Fails<ArgumentException>(async () => await fetch.FetchAsync("file:///secret"));
        });
        await Test("Review persistence survives restart, model changes and expiry; failed refresh keeps saved result", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "aria-research-" + Guid.NewGuid().ToString("N"));
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ARIA2FAST_DATA_DIR"] = directory }).Build();
                var store = new StateStore(config);
                var old = new AiReview(8, "旧版评析", DateTimeOffset.UtcNow.AddYears(-1));
                store.Update(s => { s.Reviews["123:old-profile"] = old; s.Settings.SelectedAiId = "new-profile"; });
                var restarted = new StateStore(config);
                var ai = new AiService(restarted, new HttpGateway(restarted));
                Check(JsonSerializer.Serialize(await ai.Review("123", "作品", "简介", default)) == JsonSerializer.Serialize(old));
                await Fails<ArgumentException>(async () => await ai.Review("123", "作品", "简介", default, refresh: true));
                Check(JsonSerializer.Serialize(ai.CachedReview("123")) == JsonSerializer.Serialize(old));
                var rich = old with { Version = 2, OriginalWork = "原作资料", References = [new(1, "Official", "https://example.com", "证据")] };
                store.Update(s => s.Reviews["123"] = rich);
                var saved = new StateStore(config).Read().Reviews["123"];
                Check(saved.OriginalWork == "原作资料" && saved.References.Single().Content == "证据");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        return count;
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
