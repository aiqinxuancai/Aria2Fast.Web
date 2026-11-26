using System.Net;
using System.Text.Json.Nodes;
using Aria2Fast.Service;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class EnrichTests
{
    static void Check(bool condition, string message = "Enrichment assertion failed") { if (!condition) throw new Exception(message); }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    public static async Task<int> Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        async Task TestAsync(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS " + name); }

        await TestAsync("DeepSeek search and fetch: bearer key (or the DeepSeek AI profile's), 128-character queries, summary as content, Markdown images", async () =>
        {
            var settings = new WebSettings { AiProfiles = [new() { Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", ModelName = "deepseek-chat", ApiKey = "sk-profile" }] };
            Check(ResearchSearchTools.DeepSeekKey(settings) == "sk-profile" && ResearchSearchTools.DeepSeekKey(new WebSettings { DeepSeekApiKey = " sk-own " }) == "sk-own");
            Check(new ResearchSearchTools(new WebSettings(), () => new HttpClient()).Providers.SequenceEqual(["keenable"])); // no key: not offered
            HttpRequestMessage? seen = null;
            string? body = null;
            var tools = new ResearchSearchTools(settings, () => new HttpClient(new Handler(async request =>
            {
                seen = request;
                body = await request.Content!.ReadAsStringAsync();
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/fetch")
                        ? "{\"title\":\"作品页\",\"content\":\"# 作品\\n![海报](https://img.example.com/p.jpg \\\"标题\\\")\\n正文 ![](/relative.png) ![b](https://img.example.com/b.webp)\"}"
                        : "{\"results\":[{\"title\":\"Official\",\"url\":\"https://example.com\",\"snippet\":\"short\",\"summary\":\"long summary for models\",\"content\":\"\"}]}")
                };
            })));
            Check(tools.Providers.Contains("deepseek"));
            var results = await tools.Search(new string('长', 200), "deepseek", default);
            Check(results.Single().Content == "long summary for models" && seen!.RequestUri!.AbsoluteUri == "https://api.deepseek.com/v1/tools/search" && seen.Headers.Authorization!.ToString() == "Bearer sk-profile");
            Check(JsonNode.Parse(body!)!["query"]!.ToString().Length == 128 && JsonNode.Parse(body!)!["count"]!.GetValue<int>() == 5);
            settings.FetchProvider = "deepseek";
            var page = await tools.Read("https://example.com/show", 8000, default);
            Check(seen!.RequestUri!.AbsoluteUri == "https://api.deepseek.com/v1/tools/fetch" && JsonNode.Parse(body!)!["url"]!.ToString() == "https://example.com/show");
            Check(page is { ok: true, title: "作品页" } && page.images!.SequenceEqual(["https://img.example.com/p.jpg", "https://img.example.com/b.webp"]), string.Join(",", page.images!));
        });

        Test("Direct page reading lists images: og:image first, img src and lazy data-src, made absolute", () =>
        {
            var html = "<meta property=\"og:image\" content=\"https://cdn.example.com/og.jpg\"><img src=\"/a.png\"><img class=\"lazy\" data-src=\"b.webp\" src=\"data:image/gif;base64,R0\"><img src=\"https://cdn.example.com/og.jpg\">";
            var images = WebFetchService.Images(html, new Uri("https://example.com/works/1"));
            Check(images.SequenceEqual(["https://cdn.example.com/og.jpg", "https://example.com/a.png", "https://example.com/works/b.webp"]), string.Join(",", images));
        });

        Test("Image input in each AI protocol's own format; requests without images are unchanged", () =>
        {
            var image = new AiImage("image/png", Png);
            var data = Convert.ToBase64String(Png);
            var chat = AiService.Body(AiProtocolType.OpenAIChatCompletions, "m", "p", "i", [image]);
            Check(chat["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.ToString() == "data:image/png;base64," + data && chat["messages"]![1]!["content"]![0]!["text"]!.ToString() == "p");
            Check(AiService.Body(AiProtocolType.OpenAIChatCompletions, "m", "p", "i", [])["messages"]![1]!["content"]!.GetValue<string>() == "p");
            var responses = AiService.Body(AiProtocolType.OpenAIResponses, "m", "p", "i", [image]);
            Check(responses["input"]![0]!["content"]![1]!["type"]!.ToString() == "input_image" && responses["input"]![0]!["content"]![1]!["image_url"]!.ToString().StartsWith("data:image/png;base64,"));
            var claude = AiService.Body(AiProtocolType.Claude, "m", "p", "i", [image]);
            Check(claude["messages"]![0]!["content"]![1]!["source"]!["media_type"]!.ToString() == "image/png" && claude["messages"]![0]!["content"]![1]!["source"]!["data"]!.ToString() == data);
            var gemini = AiService.Body(AiProtocolType.Gemini, "m", "p", "i", [image]);
            Check(gemini["contents"]![0]!["parts"]![1]!["inline_data"]!["mime_type"]!.ToString() == "image/png");
        });

        await TestAsync("AI scraping agent looks at pictures it found, keeps notes, and returns genres, artwork and episodes", async () =>
        {
            var calls = new List<(JsonNode Prompt, int Images)>();
            var downloads = new List<string>();
            string Steps(JsonNode p) => string.Join(",", (p["observations"] as JsonArray ?? []).Where(o => o?["note"] == null).Select(o => o?["tool"]?.ToString() ?? "error"));
            var agent = new LibraryScrapeAgent((prompt, instruction, images, ct) =>
                {
                    var node = JsonNode.Parse(prompt)!;
                    calls.Add((node, images.Count));
                    return Task.FromResult(Steps(node) switch
                    {
                        "" => """{"action":"bangumi_search","keyword":"Show"}""",
                        "bangumi_search" => """{"action":"view_image","url":"https://evil.example/x.png"}""",
                        "bangumi_search,error" => """{"action":"view_image","url":"https://lain.bgm.tv/r/400/s2.jpg","note":"先看第二季封面"}""",
                        "bangumi_search,error,view_image" => """{"action":"finish","source":"bangumi","id":9002,"confidence":"high","reason":"封面标题为第二季","genres":["奇幻","冒险"],"fanart":"https://lain.bgm.tv/r/400/s2.jpg","poster":"https://made.up/p.jpg","totalEpisodes":"x","episodes":[{"number":1,"title":"出发","airDate":"2026-01-02"},{"number":-3,"title":"bad"},{"number":1,"title":"dup"}]}""",
                        _ => """{"action":"finish","source":"none","confidence":"low"}"""
                    });
                }, null, [], null,
                (keyword, ct) => Task.FromResult(new List<BangumiCandidate> { new(9002, "Show 2nd Season", "", "2026-01-01", [], "https://lain.bgm.tv/r/400/s2.jpg") }),
                (id, ct) => throw new InvalidOperationException(), null, null,
                (url, ct) => { downloads.Add(url); return Task.FromResult(Png); });
            var answer = await agent.Run(new ScrapeTarget("anime", "Show S2", 2, null, [], "", ""), default);
            Check(downloads.SequenceEqual(["https://lain.bgm.tv/r/400/s2.jpg"]), string.Join(",", downloads));
            Check(calls[0].Prompt["tools"]!["view_image"]!.GetValue<bool>() && calls[3].Images == 1 && calls[3].Prompt["attachedImages"]![0]!.ToString() == "https://lain.bgm.tv/r/400/s2.jpg" && calls[2].Images == 0);
            Check(calls[3].Prompt["observations"]!.AsArray().Any(o => o?["note"]?.ToString() == "先看第二季封面"));
            Check(answer is { Source: "bangumi", Id: 9002, Fanart: "https://lain.bgm.tv/r/400/s2.jpg", Poster: "", TotalEpisodes: null } && answer.Genres.SequenceEqual(["奇幻", "冒险"])
                && answer.Episodes.Count == 1 && answer.Episodes[0] is { Number: 1, Title: "出发", AirDate: "2026-01-02" }, answer.ToString());
            Check(LibraryScrapeAgent.MediaType(Png) == "image/png" && LibraryScrapeAgent.MediaType([1, 2, 3]) == null);
            // Without a downloader (a model without image input) the tool is not offered.
            var blind = new LibraryScrapeAgent((prompt, instruction, images, ct) => Task.FromResult(JsonNode.Parse(prompt)!["tools"]!["view_image"]!.GetValue<bool>()
                    ? """{"action":"finish","source":"none","confidence":"high","title":"wrong"}""" : """{"action":"finish","source":"none","confidence":"low"}"""),
                null, [], null, (k, ct) => Task.FromResult(new List<BangumiCandidate>()), (id, ct) => throw new InvalidOperationException(), null, null);
            Check((await blind.Run(new ScrapeTarget("anime", "x", 1, null, [], "", ""), default)).Confidence == "low");
        });

        Test("Bangumi genres and studio, episode summaries; TMDB genres, studio and backdrops", () =>
        {
            var subject = BangumiClient.ParseSubject(JsonNode.Parse("""
                {"id":1,"name":"N","tags":[{"name":"2023年10月","count":900},{"name":"奇幻","count":800},{"name":"TV","count":700},{"name":"冒险","count":600}],
                 "infobox":[{"key":"动画制作","value":"MADHOUSE"},{"key":"别名","value":[{"v":"A"}]}]}
                """)!);
            Check(subject.Genres.SequenceEqual(["奇幻", "冒险"]) && subject.Studio == "MADHOUSE", string.Join(",", subject.Genres) + subject.Studio);
            Check(BangumiClient.ParseEpisodes(JsonNode.Parse("""[{"ep":1,"sort":1,"name":"a","name_cn":"甲","desc":"本集简介","type":0}]""")!.AsArray())[0].Summary == "本集简介");
            var tv = TmdbClient.Parse(JsonNode.Parse("""{"id":2,"name":"剧","genres":[{"name":"剧情"}],"networks":[{"name":"AMC"}],"backdrop_path":"/b.jpg"}""")!);
            Check(tv is { Studio: "AMC", FanartUrl: "https://image.tmdb.org/t/p/w1280/b.jpg" } && tv.Genres.SequenceEqual(["剧情"]));
            var movie = TmdbClient.ParseMovie(JsonNode.Parse("""{"id":3,"title":"片","production_companies":[{"name":"Legendary"}],"genres":[{"name":"科幻"}]}""")!);
            Check(movie is { Studio: "Legendary", FanartUrl: "" } && movie.Genres.SequenceEqual(["科幻"]));
        });

        var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-nfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Test("NFO for Jellyfin / Emby / Kodi: show, season folder, episodes, movie folder and loose movie; other tools' files are kept", () =>
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ARIA2FAST_DATA_DIR"] = temporary }).Build();
                var store = new StateStore(config);
                var root = Path.Combine(temporary, "downloads");
                store.Update(s => s.Settings.DownloadDirectory = root);
                var library = new LibraryStore(store);
                void Touch(string relative) { var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); }
                Touch("作品/Season 2/[G] 作品 - 01.mkv");
                Touch("作品/Season 2/[G] 作品 - 02.mkv");
                Touch("作品/Season 2/[G] 作品 - 12.5.mkv");
                Touch("movies/Inception (2010)/Inception.mkv");
                Touch("movies/Loose.2014.mkv");
                File.WriteAllBytes(Path.Combine(library.PosterDirectory, "aaaaaaaaaaaaaaaa.jpg"), [0xFF, 0xD8, 0xFF, 1]);
                File.WriteAllBytes(Path.Combine(library.PosterDirectory, "aaaaaaaaaaaaaaaa-fanart.png"), Png);
                var meta = new LibraryMetadata("bangumi", 400602, "葬送のフリーレン 第2期", "葬送的芙莉莲 第二季", "简介 & <特别>", "2026-01-16", 8.9, 10, "TV")
                {
                    PosterFile = "aaaaaaaaaaaaaaaa.jpg", FanartFile = "aaaaaaaaaaaaaaaa-fanart.png", Genres = ["奇幻"], Studio = "MADHOUSE",
                    Episodes = [new(1, 29, "Ep", "第一话标题", "2026-01-16", 0, "第一集简介")], UpdatedAt = DateTimeOffset.UtcNow
                };
                var movieMeta = new LibraryMetadata("tmdb-movie", 27205, "Inception", "盗梦空间", "梦", "2010-07-15", null, null, "电影") { PosterFile = "aaaaaaaaaaaaaaaa.jpg", UpdatedAt = DateTimeOffset.UtcNow };
                LibraryEpisode Ep(string series, string path, double? number) => new() { Id = LibraryScanner.Hash(path), SeriesId = series, Path = path, Number = number };
                library.Update(l =>
                {
                    l.Series = [
                        new() { Id = "s1", Key = "dir:作品#2", Title = "作品", Directory = "作品", Season = 2, FolderId = "downloads", Category = LibraryCategory.Anime, Status = LibraryMetadataStatus.Matched, Metadata = meta },
                        new() { Id = "m1", Key = "dir:movies/inception (2010)#0", Title = "Inception", Directory = "movies/Inception (2010)", FolderId = "movies", Category = LibraryCategory.Movie, Status = LibraryMetadataStatus.Matched, Metadata = movieMeta },
                        new() { Id = "m2", Key = "title:movies:loose:2014#0", Title = "Loose", Directory = "movies", FolderId = "movies", Category = LibraryCategory.Movie, Year = 2014, Status = LibraryMetadataStatus.Matched, Metadata = movieMeta }];
                    l.Episodes = [Ep("s1", "作品/Season 2/[G] 作品 - 01.mkv", 1), Ep("s1", "作品/Season 2/[G] 作品 - 02.mkv", 2), Ep("s1", "作品/Season 2/[G] 作品 - 12.5.mkv", 12.5),
                        Ep("m1", "movies/Inception (2010)/Inception.mkv", null), Ep("m2", "movies/Loose.2014.mkv", null)];
                });
                // Somebody else's NFO for episode 2 stays as it is.
                File.WriteAllText(Path.Combine(root, "作品/Season 2/[G] 作品 - 02.nfo"), "<episodedetails><title>mine</title></episodedetails>");
                var writer = new NfoWriter(store, library, NullLogger<NfoWriter>.Instance);
                foreach (var id in new[] { "s1", "m1", "m2" }) writer.Write(id);
                string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));
                var show = Read("作品/tvshow.nfo");
                Check(show.Contains(NfoWriter.Marker) && show.Contains("<title>葬送的芙莉莲 第二季</title>") && show.Contains("<plot>简介 &amp; &lt;特别&gt;</plot>") && show.Contains("<uniqueid type=\"bangumi\" default=\"true\">400602</uniqueid>")
                    && show.Contains("<genre>奇幻</genre>") && show.Contains("<studio>MADHOUSE</studio>") && show.Contains("<premiered>2026-01-16</premiered>") && show.Contains("<rating>8.9</rating>"), show);
                Check(File.Exists(Path.Combine(root, "作品/poster.jpg")) && File.Exists(Path.Combine(root, "作品/fanart.png")) && File.Exists(Path.Combine(root, "作品/season02-poster.jpg")));
                Check(Read("作品/Season 2/season.nfo").Contains("<seasonnumber>2</seasonnumber>"));
                var episode = Read("作品/Season 2/[G] 作品 - 01.nfo");
                Check(episode.Contains("<title>第一话标题</title>") && episode.Contains("<season>2</season>") && episode.Contains("<episode>1</episode>") && episode.Contains("<aired>2026-01-16</aired>") && episode.Contains("<plot>第一集简介</plot>"), episode);
                Check(Read("作品/Season 2/[G] 作品 - 02.nfo") == "<episodedetails><title>mine</title></episodedetails>");
                Check(!File.Exists(Path.Combine(root, "作品/Season 2/[G] 作品 - 12.5.nfo"))); // no integer episode number
                var movie = Read("movies/Inception (2010)/movie.nfo");
                Check(movie.Contains("<movie>") && movie.Contains("<uniqueid type=\"tmdb\" default=\"true\">27205</uniqueid>") && movie.Contains("<tmdbid>27205</tmdbid>") && File.Exists(Path.Combine(root, "movies/Inception (2010)/poster.jpg")));
                Check(File.Exists(Path.Combine(root, "movies/Loose.2014.nfo")) && File.Exists(Path.Combine(root, "movies/Loose.2014-poster.jpg")) && !File.Exists(Path.Combine(root, "movies/movie.nfo")));
                Check(library.Read().Series.All(s => s.NfoWrittenAt != null));

                // Ours is rewritten when the metadata changes; a show NFO someone took over (marker removed) is left alone.
                library.Update(l => l.Series[0].Metadata = meta with { NameCn = "新标题", UpdatedAt = DateTimeOffset.UtcNow });
                writer.Write("s1");
                Check(Read("作品/tvshow.nfo").Contains("<title>新标题</title>"));
                File.WriteAllText(Path.Combine(root, "作品/tvshow.nfo"), "<tvshow><title>手动</title></tvshow>");
                File.WriteAllText(Path.Combine(root, "作品/poster.jpg"), "custom");
                writer.Write("s1");
                Check(Read("作品/tvshow.nfo") == "<tvshow><title>手动</title></tvshow>" && Read("作品/poster.jpg") == "custom");
            });
        }
        finally { Directory.Delete(temporary, true); }
        return count;
    }
}
