using System.Text.Json.Nodes;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;

static class CategoryTests
{
    static void Check(bool condition, string message = "Category assertion failed") { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static async Task<int> Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        async Task TestAsync(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS " + name); }

        Test("Movie names: title and year from folders and scene file names", () =>
        {
            void Expect(string name, string title, int? year) { var m = ReleaseNames.Movie(name); Check(m.Title == title && m.Year == year, name + " → " + m); }
            Expect("Inception (2010)", "Inception", 2010);
            Expect("Inception.2010.1080p.BluRay.x264-GROUP", "Inception", 2010);
            Expect("[Group] 你的名字 [2016][1080p]", "你的名字", 2016);
            Expect("Blade Runner 2049 (2017)", "Blade Runner 2049", 2017);
            Expect("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968);
            Expect("千与千寻", "千与千寻", null);
        });

        var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-categories-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var root = Path.Combine(temporary, "downloads");
            var external = Path.Combine(temporary, "nas", "movies");
            Test("Library folders: defaults inside the download directory, others as @id; nested folders and paths are checked", () =>
            {
                var settings = new LibrarySettings();
                settings.Folders.Add(new() { Id = "nas", Path = external, Category = LibraryCategory.Movie });
                settings.Folders.Add(new() { Id = "copy", Path = Path.Combine(root, "movies"), Category = LibraryCategory.Movie });
                var folders = LibraryFolders.Resolve(settings, root);
                Check(folders.Select(f => (f.Id, f.Prefix, f.Category)).SequenceEqual([("downloads", "", LibraryCategory.Anime), ("tvshows", "tvshows", LibraryCategory.Series), ("movies", "movies", LibraryCategory.Movie), ("nas", "@nas", LibraryCategory.Movie)]),
                    string.Join(", ", folders));
                Check(LibraryFolders.Nested(folders[0], folders).SetEquals([Path.Combine(root, "tvshows"), Path.Combine(root, "movies")]) && LibraryFolders.Nested(folders[3], folders).Count == 0);
                Directory.CreateDirectory(Path.Combine(external, "Film"));
                File.WriteAllText(Path.Combine(external, "Film", "a.mkv"), "x");
                Check(LibraryFolders.Locate("@nas/Film/a.mkv", folders, root) == Path.Combine(external, "Film", "a.mkv"));
                Throws<FileNotFoundException>(() => LibraryFolders.Locate("作品/a.mkv", folders, root)); // deleted: 404, not a symlink error
                Directory.CreateDirectory(Path.Combine(root, "作品"));
                File.WriteAllText(Path.Combine(root, "作品", "a.mkv"), "x");
                Check(LibraryFolders.Locate("作品/a.mkv", folders, root) == Path.Combine(root, "作品", "a.mkv"));
                File.Delete(Path.Combine(root, "作品", "a.mkv"));
                Throws<ArgumentException>(() => LibraryFolders.Locate("@nas/../../secret.mkv", folders, root));
                Throws<FileNotFoundException>(() => LibraryFolders.Locate("@gone/a.mkv", folders, root));

                var edited = new LibrarySettings { Folders = [new() { Id = "", Path = " \"" + external + "\" ", Category = LibraryCategory.Movie }, new() { Id = "Bad Id", Path = "tv", Category = LibraryCategory.Series }] };
                LibraryFolders.Validate(edited, root);
                Check(edited.Folders[0].Path == external && edited.Folders[0].Id.StartsWith('f') && edited.Folders[0].Id.Length == 9 && edited.Folders[1].Id != "Bad Id");
                Throws<ArgumentException>(() => LibraryFolders.Validate(new LibrarySettings { Folders = [new() { Path = "movies" }, new() { Path = Path.Combine(root, "movies") }] }, root));
                Throws<ArgumentException>(() => LibraryFolders.Validate(new LibrarySettings { Folders = [new() { Path = "a\nb" }] }, root));
            });

            Test("Scanning folders: nested folders skipped by the outer one, categories, movie folders and loose movies", () =>
            {
                void Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "x"); }
                Touch(Path.Combine(root, "作品", "[G] 作品 - 01.mkv"));
                Touch(Path.Combine(root, "tvshows", "Breaking Bad", "Season 2", "Breaking.Bad.S02E03.1080p.mkv"));
                Touch(Path.Combine(root, "movies", "Inception (2010)", "Inception.2010.1080p.mkv"));
                Touch(Path.Combine(root, "movies", "Inception (2010)", "Featurettes", "Making of.mkv"));
                Touch(Path.Combine(root, "movies", "Interstellar.2014.2160p.mkv"));
                Touch(Path.Combine(root, "movies", "Interstellar.1999.mkv"));
                var settings = new LibrarySettings();
                settings.Folders.Add(new() { Id = "nas", Path = external, Category = LibraryCategory.Movie });
                var folders = LibraryFolders.Resolve(settings, root);
                var files = folders.SelectMany(f => LibraryScanner.Walk(f.FullPath, f.Prefix, f.Id, LibraryFolders.Nested(f, folders), 1000, out _)).ToList();
                Check(files.Count(f => f.Folder == "downloads") == 1, string.Join(", ", files)); // tvshows / movies are not anime
                var built = LibraryScanner.Build(files, [], new Dictionary<string, Subscription>(), new LibraryState(), DateTimeOffset.UtcNow, false, folders.ToDictionary(f => f.Id));
                var bySeries = built.Series.ToDictionary(s => s.Key);
                Check(bySeries["dir:作品#0"] is { Category: LibraryCategory.Anime, FolderId: "downloads" }, string.Join(", ", bySeries.Keys));
                var show = bySeries["dir:tvshows/breaking bad#2"];
                Check(show is { Category: LibraryCategory.Series, Season: 2, Title: "Breaking Bad" } && built.Episodes.Single(e => e.SeriesId == show.Id).Number == 3);
                var inception = bySeries["dir:movies/inception (2010)#0"];
                Check(inception is { Category: LibraryCategory.Movie, Title: "Inception", Year: 2010, Season: 0 });
                var parts = built.Episodes.Where(e => e.SeriesId == inception.Id).ToList();
                Check(parts.Count == 2 && parts.All(e => e.Number == null) && parts.Count(e => e.Kind == LibraryEpisodeKind.Extra) == 1);
                // Loose movies: one work per title and year, so remakes stay apart.
                Check(bySeries["title:movies:interstellar:2014#0"].Year == 2014 && bySeries.ContainsKey("title:movies:interstellar:1999#0"));
                var film = built.Series.Single(s => s.FolderId == "nas");
                Check(film is { Category: LibraryCategory.Movie, Title: "Film", Directory: "@nas/Film" } && built.Episodes.Single(e => e.SeriesId == film.Id).Path == "@nas/Film/a.mkv");

                // A folder that is missing now (an unmounted share) keeps its works and their episodes.
                var again = LibraryScanner.Build(files.Where(f => f.Folder != "nas").ToList(), [], new Dictionary<string, Subscription>(), built, DateTimeOffset.UtcNow, false, folders.ToDictionary(f => f.Id), new HashSet<string> { "nas" });
                Check(again.Series.Any(s => s.Id == film.Id) && again.Episodes.Any(e => e.Path == "@nas/Film/a.mkv"));
                var dropped = LibraryScanner.Build(files.Where(f => f.Folder != "nas").ToList(), [], new Dictionary<string, Subscription>(), built, DateTimeOffset.UtcNow, false, folders.ToDictionary(f => f.Id));
                Check(dropped.Series.All(s => s.FolderId != "nas"));
            });
        }
        finally { Directory.Delete(temporary, true); }

        Test("TMDB movies and seasons; Bangumi subject types per category", () =>
        {
            var movie = TmdbClient.ParseMovie(JsonNode.Parse("""{"id":27205,"title":"盗梦空间","original_title":"Inception","overview":"简介","release_date":"2010-07-15","vote_average":8.4,"poster_path":"/p.jpg"}""")!);
            Check(movie is { Source: "tmdb-movie", SourceId: 27205, Name: "Inception", NameCn: "盗梦空间", AirDate: "2010-07-15", Platform: "电影", PosterUrl: "https://image.tmdb.org/t/p/w342/p.jpg" });
            var show = TmdbClient.Parse(JsonNode.Parse("""{"id":1396,"name":"绝命毒师","original_name":"Breaking Bad","overview":"剧集简介","first_air_date":"2008-01-20","number_of_episodes":62,"poster_path":"/show.jpg"}""")!);
            var season = TmdbClient.WithSeason(show, JsonNode.Parse("""{"air_date":"2009-03-08","overview":"","poster_path":"/s2.jpg","episodes":[{"episode_number":1,"name":"737","air_date":"2009-03-08"},{"episode_number":2,"name":"Grilled","air_date":"2009-03-15"}]}""")!);
            Check(season is { AirDate: "2009-03-08", Summary: "剧集简介", TotalEpisodes: 2, PosterUrl: "https://image.tmdb.org/t/p/w342/s2.jpg" } && season.Episodes[1] is { Number: 2, Name: "Grilled" });
            Check(BangumiClient.Types(LibraryCategory.Anime).SequenceEqual([2]) && BangumiClient.Types(LibraryCategory.Series).SequenceEqual([6, 2]) && BangumiClient.Types(LibraryCategory.Movie).SequenceEqual([2, 6]));
        });

        // A scripted model: each answer depends on what the observations hold so far.
        LibraryScrapeAgent Agent(Func<JsonNode, string> model, Func<string, CancellationToken, Task<IReadOnlyList<AiResearchSource>>>? search = null,
            Func<string, CancellationToken, Task<WebFetchService.FetchResult>>? fetch = null) =>
            new((prompt, instruction, ct) => Task.FromResult(model(JsonNode.Parse(prompt)!)), search == null ? null : (q, p, ct) => search(q, ct), ["keenable"], fetch,
                (keyword, ct) => Task.FromResult(new List<BangumiCandidate> { new(9002, "Search Show", "", "2026-10-01", []), new(1, "Unrelated", "无关", "", []) }),
                (id, ct) => Task.FromResult(new LibraryMetadata("bangumi", id, "Search Show", "", "简介", "2026-10-01", null, 12, "TV") { PosterUrl = "https://lain.bgm.tv/r/400/p.jpg" }),
                null, null);
        var target = new ScrapeTarget("anime", "[G] Search Show S1", 1, null, ["[G] Search Show - 01.mkv"], "", "");
        string Steps(JsonNode prompt) => string.Join(",", (prompt["observations"] as JsonArray ?? []).Select(o => o?["tool"]?.ToString() ?? "error"));

        await TestAsync("AI scraping agent: finishes with a Bangumi ID it looked up, refuses IDs it never saw", async () =>
        {
            var answer = await Agent(p => Steps(p) switch
            {
                "" => """{"action":"bangumi_search","keyword":"Search Show"}""",
                "bangumi_search" => """{"action":"finish","source":"bangumi","id":424242,"confidence":"high"}""",
                "bangumi_search,error" => """{"action":"bangumi_subject","id":9002}""",
                _ => """{"action":"finish","source":"bangumi","id":9002,"confidence":"high","reason":"名称与年份一致"}"""
            }).Run(target, default);
            Check(answer is { Source: "bangumi", Id: 9002, Confidence: "high", Reason: "名称与年份一致" }, answer.ToString());
            // Invalid JSON and tools that are not offered (TMDB without a key) are reported back; without a conclusion the run fails.
            var turns = 0;
            try { await Agent(p => ++turns == 1 ? "不是 JSON" : """{"action":"tmdb_search","query":"x"}""").Run(target, default); throw new Exception("expected no conclusion"); }
            catch (InvalidOperationException) { Check(turns == LibraryScrapeAgent.MaxTurns); }
            var low = await Agent(p => """{"action":"finish","source":"none","confidence":"low","reason":"同名作品太多"}""").Run(target, default);
            Check(low is { Source: "none", Confidence: "low", Reason: "同名作品太多" }, low.ToString());
        });

        await TestAsync("AI scraping agent: web pages only from search results; assembled metadata keeps only URLs it saw", async () =>
        {
            var fetched = new List<string>();
            var answer = await Agent(p => Steps(p) switch
            {
                "" => """{"action":"fetch","url":"https://evil.example/page"}""",
                "error" => """{"action":"search","query":"Search Show 动画"}""",
                "error,search" => """{"action":"fetch","url":"https://example.com/show"}""",
                _ => """{"action":"finish","source":"none","confidence":"medium","title":"测试作品","summary":"网页资料","poster":"https://img.example.com/p.jpg","reference":"https://made-up.example/page"}"""
            }, (q, ct) => Task.FromResult<IReadOnlyList<AiResearchSource>>([new(0, "Show", "https://example.com/show", "official page")]),
            (url, ct) => { fetched.Add(url); return Task.FromResult(new WebFetchService.FetchResult(url, url, 200, "text/html", "Show", "poster: https://img.example.com/p.jpg", false, true, "")); }).Run(target, default);
            Check(fetched.SequenceEqual(["https://example.com/show"]), string.Join(",", fetched));
            Check(answer is { Source: "none", Title: "测试作品", Poster: "https://img.example.com/p.jpg", Reference: "" }, answer.ToString());
        });
        return count;
    }
}
