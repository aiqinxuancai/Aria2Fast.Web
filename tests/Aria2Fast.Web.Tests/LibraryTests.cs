using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class LibraryTests
{
    static void Check(bool condition, string message = "Library assertion failed") { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static int Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }

        Test("Release names: fansub, all-bracket, scene and Chinese styles", () =>
        {
            void Expect(string file, string title, double? number, int? season = null, LibraryEpisodeKind kind = LibraryEpisodeKind.Episode)
            {
                var parsed = ReleaseNames.ParseFile(file);
                Check(parsed.Title == title && parsed.Number == number && parsed.Season == season && parsed.Kind == kind, file + " → " + parsed);
            }
            Expect("[LoliHouse] 葬送的芙莉莲 / Sousou no Frieren - 05 [WebRip 1080p HEVC-10bit AAC][简繁内封字幕].mkv", "葬送的芙莉莲 / Sousou no Frieren", 5);
            Expect("[ANi] Sousou no Frieren - 葬送的芙莉蓮 - 05 [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4", "Sousou no Frieren - 葬送的芙莉蓮", 5);
            Expect("[Nekomoe kissaten][Sousou no Frieren][05][1080p][CHS].mp4", "Sousou no Frieren", 5);
            Expect("【喵萌奶茶屋】★10月新番★[葬送的芙莉莲 / Sousou no Frieren][05][1080p][简日双语][招募翻译].mp4", "葬送的芙莉莲 / Sousou no Frieren", 5);
            Expect("[桜都字幕组][10月新番][葬送的芙莉莲][05][1080P].mp4", "葬送的芙莉莲", 5);
            Expect("[Sakurato] Sousou no Frieren [05][AVC-8bit 1080p AAC][CHS].mp4", "Sousou no Frieren", 5);
            Expect("Frieren.S01E05.1080p.WEB-DL.mkv", "Frieren", 5, 1);
            Expect("葬送的芙莉莲 第05话.mp4", "葬送的芙莉莲", 5);
            Expect("葬送的芙莉莲 第十二話.mp4", "葬送的芙莉莲", 12);
            Expect("[Group] 葬送的芙莉莲 第二季 - 03 [1080p].mkv", "葬送的芙莉莲", 3, 2);
            Expect("[Group] Mushoku Tensei S2 - 13v2 [1080p].mkv", "Mushoku Tensei", 13, 2);
            Expect("[Group] Title - 12.5 [1080p].mkv", "Title", 12.5);
            Expect("[Group] 86 - Eighty Six - 05 [1080p].mkv", "86 - Eighty Six", 5);
            Expect("[Group][86][05][1080p].mkv", "86", 5);
            Expect("[Group] One Piece - 1080 [1080p].mkv", "One Piece", 1080);
            Expect("[Group] Title [2024][05][1080p].mkv", "Title", 5);
            Expect("[Group] Special A - 05 [1080p].mkv", "Special A", 5);
            Expect("[Group] Title 05 [1080p].mkv", "Title", 5);
            Expect("[VCB-Studio] Sousou no Frieren [NCOP1][Ma10p_1080p][x265_flac].mkv", "Sousou no Frieren", null, null, LibraryEpisodeKind.Extra);
            Expect("[VCB-Studio] Sousou no Frieren [SP01][Ma10p_1080p][x265_flac].mkv", "Sousou no Frieren", 1, null, LibraryEpisodeKind.Special);
            Expect("[Group] Title - OVA [1080p].mkv", "Title", null, null, LibraryEpisodeKind.Special);
        });
        Test("Folder seasons and kinds, folder titles, search terms and video extensions", () =>
        {
            Check(ReleaseNames.SeasonFolder("Season 2") == 2 && ReleaseNames.SeasonFolder("S01") == 1 && ReleaseNames.SeasonFolder("第三季") == 3 && ReleaseNames.SeasonFolder("Sousou") == null);
            Check(ReleaseNames.FolderKind("SPs") == LibraryEpisodeKind.Special && ReleaseNames.FolderKind("Season 00") == LibraryEpisodeKind.Special);
            Check(ReleaseNames.FolderKind("CDs") == LibraryEpisodeKind.Extra && ReleaseNames.FolderKind("Season 1") == null);
            Check(ReleaseNames.Title("[Group] 葬送的芙莉莲 [01-28][1080p]") == "葬送的芙莉莲" && ReleaseNames.Title("Fate/stay night") == "Fate/stay night");
            Check(ReleaseNames.SearchTerms("Sousou no Frieren - 葬送的芙莉蓮").SequenceEqual(["葬送的芙莉蓮", "Sousou no Frieren", "Sousou no Frieren - 葬送的芙莉蓮"]));
            Check(ReleaseNames.SearchTerms("Fate/stay night").SequenceEqual(["Fate/stay night"]));
            Check(ReleaseNames.IsVideo("a.MKV") && ReleaseNames.IsVideo("b.mp4") && !ReleaseNames.IsVideo("a.ass") && !ReleaseNames.IsVideo("a.mkv.aria2"));
        });
        Test("Mikan ID comes from the RSS address; bgm.tv subject from the Mikan detail page", () =>
        {
            Check(Validation.MikanId("https://mikanani.me/RSS/Bangumi?bangumiId=3310&subgroupid=1230") == "3310");
            Check(Validation.MikanId("https://mikanani.me/RSS/Bangumi?subgroupid=1&bangumiid=42") == "42");
            Check(Validation.MikanId("https://example.com/rss?bangumiId=12x") == null && Validation.MikanId("https://acg.rip/.xml") == null);
            Check(MikanService.ParseBangumiTvId("<p class=\"bangumi-info\">Bangumi番组计划链接：<br /><a class=\"w-other-c\" href=\"https://bgm.tv/subject/411974\">https://bgm.tv/subject/411974</a></p>") == 411974);
            Check(MikanService.ParseBangumiTvId("<html>no link</html>") == null);
            var state = new AppState();
            var subscription = new Subscription { Url = "https://mikanani.me/RSS/Bangumi?bangumiId=3310&subgroupid=1", MikanId = "999" };
            Validation.Subscription(subscription, state);
            Check(subscription.MikanId == "3310");
            subscription.Url = "https://example.com/feed";
            Validation.Subscription(subscription, state);
            Check(subscription.MikanId == "3310");
            subscription.MikanId = "abc";
            Throws<ArgumentException>(() => Validation.Subscription(subscription, state));
            subscription.MikanId = " ";
            subscription.BgmId = 0;
            Throws<ArgumentException>(() => Validation.Subscription(subscription, state));
        });
        Test("Library groups subscription folders, season folders, batch folders and loose files", () =>
        {
            var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
            var subscription = new Subscription { Id = "sub1", NamePath = "葬送的芙莉莲", Season = 1, MikanId = "3310" };
            var rss = new Subscription { Id = "sub2", Name = "RSS" };
            LibraryScanner.ScannedFile File(string path) => new(path, 100, now.AddHours(-1));
            var files = new[]
            {
                File("葬送的芙莉莲/Season 1/[LoliHouse] 葬送的芙莉莲 - 01 [1080p].mkv"),
                File("葬送的芙莉莲/Season 1/[LoliHouse] 葬送的芙莉莲 - 02 [1080p].mkv"),
                File("葬送的芙莉莲/Season 1/SPs/[VCB] 葬送的芙莉莲 [NCOP1].mkv"),
                File("葬送的芙莉莲/Season 2/[LoliHouse] 葬送的芙莉莲 第二季 - 01 [1080p].mkv"),
                File("[ANi] Loose Show - 05 [1080P].mp4"),
                File("[ANi] Loose Show - 06 [1080P].mp4"),
                File("[Group] Batch Title [01-12][1080p]/[Group] Batch Title [01].mkv")
            };
            var claims = new List<LibraryScanner.Claim> { new("葬送的芙莉莲", subscription) };
            var subscriptions = new Dictionary<string, Subscription> { ["sub1"] = subscription, ["sub2"] = rss };
            var previous = new LibraryState { Origins = { ["[ANi] Loose Show - 05 [1080P].mp4"] = new("sub2", "gid", now), ["gone.mkv"] = new("sub2", "old", now) } };
            var state = LibraryScanner.Build(files, claims, subscriptions, previous, now);
            Check(state.Series.Count == 4 && state.Episodes.Count == 7 && state.Origins.Keys.SequenceEqual(["[ANi] Loose Show - 05 [1080P].mp4"]));
            var first = state.Series.Single(s => s.Key == "dir:葬送的芙莉莲#1");
            Check(first is { SubscriptionId: "sub1", MikanId: "3310", Title: "葬送的芙莉莲", Status: LibraryMetadataStatus.Pending });
            var episodes = state.Episodes.Where(e => e.SeriesId == first.Id).ToList();
            Check(episodes.Select(e => e.Number).SequenceEqual([1.0, 2.0, null]) && episodes[2].Kind == LibraryEpisodeKind.Extra);
            // A season 1 subscription does not claim the season 2 folder next to it.
            Check(state.Series.Single(s => s.Key == "dir:葬送的芙莉莲#2").SubscriptionId == null);
            Check(state.Series.Single(s => s.Directory == "") is { Title: "Loose Show", SubscriptionId: "sub2", Season: 0 });
            Check(state.Series.Single(s => s.Directory.StartsWith("[Group]")).Title == "Batch Title");

            // Rescans keep fetched metadata and first-seen times, and drop files that disappeared.
            first.Status = LibraryMetadataStatus.Matched;
            first.BgmId = 400602;
            first.Metadata = new("bangumi", 400602, "葬送のフリーレン", "葬送的芙莉莲", "", "2023-09-29", 8.9, 28, "TV");
            state.Episodes.Single(e => e.Path == files[1].Path).AddedAt = now.AddDays(-5);
            var again = LibraryScanner.Build(files.Where(f => f != files[0] && f != files[4]).ToArray(), claims, subscriptions, state, now.AddDays(1));
            var kept = again.Series.Single(s => s.Key == first.Key);
            Check(kept.Id == first.Id && kept is { Status: LibraryMetadataStatus.Matched, BgmId: 400602 } && kept.Metadata?.NameCn == "葬送的芙莉莲");
            Check(again.Episodes.Count == 5 && again.Episodes.Single(e => e.Path == files[1].Path).AddedAt == now.AddDays(-5) && again.Origins.Count == 0);
            Check(again.Series.Single(s => s.Directory == "").SubscriptionId == null);

            // A changed subscription identity resets an automatic match, but never a manual one.
            subscription.MikanId = "9999";
            Check(LibraryScanner.Build(files, claims, subscriptions, again, now).Series.Single(s => s.Key == first.Key) is { Status: LibraryMetadataStatus.Pending, Metadata: null, BgmId: null, MikanId: "9999" });
            kept.ManualMatch = true;
            Check(LibraryScanner.Build(files, claims, subscriptions, again, now).Series.Single(s => s.Key == first.Key) is { Status: LibraryMetadataStatus.Matched, BgmId: 400602, ManualMatch: true });
            kept.ManualMatch = false;
            subscription.MikanId = "3310";
            subscription.BgmId = 12345;
            Check(LibraryScanner.Build(files, claims, subscriptions, again, now).Series.Single(s => s.Key == first.Key) is { Status: LibraryMetadataStatus.Pending, BgmId: 12345 });
        });
        Test("Subscription folders inside the download root are claimed; shared folders without a work name are not", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aria2fast-claims", "root");
            var state = new AppState
            {
                Subscriptions =
                [
                    new() { Id = "a", NamePath = "作品A", Season = 1 },
                    new() { Id = "b", Directory = Path.Combine(root, "Anime"), NamePath = "作品B" },
                    new() { Id = "c", Directory = Path.Combine(root, "Shared") },
                    new() { Id = "d", Directory = Path.Combine(root, "Shared") },
                    new() { Id = "e", Directory = Path.Combine(root, "Solo") },
                    new() { Id = "f" },
                    new() { Id = "g", Directory = Path.Combine(root, ".."), NamePath = "outside" },
                    new() { Id = "h", NodeId = "remote", NamePath = "远程" }
                ]
            };
            var claims = LibraryService.Claims(state, root).Select(c => c.Directory + "=" + c.Subscription.Id).ToList();
            Check(claims.SequenceEqual(["作品A=a", "Anime/作品B=b", "Solo=e"]), string.Join(", ", claims));
        });
        Test("Bangumi subject, episode and search parsing; sequels picked by season, unrelated results rejected", () =>
        {
            var meta = BangumiClient.ParseSubject(JsonNode.Parse("""
                {"id":400602,"name":"葬送のフリーレン","name_cn":"葬送的芙莉莲","summary":" 简介 ","date":"2023-09-29","platform":"TV",
                 "images":{"large":"https://lain.bgm.tv/pic/cover/l/a.jpg","medium":"https://lain.bgm.tv/r/800/pic/cover/l/a.jpg","common":"https://lain.bgm.tv/r/400/pic/cover/l/a.jpg"},"rating":{"score":9.1},"total_episodes":28}
                """)!);
            Check(meta is { Source: "bangumi", SourceId: 400602, NameCn: "葬送的芙莉莲", Summary: "简介", Score: 9.1, TotalEpisodes: 28 } && meta.PosterUrl.Contains("/r/400/"));
            Check(BangumiClient.ParseSubject(JsonNode.Parse("""{"id":1,"rating":{"score":0},"eps":0}""")!) is { Score: null, TotalEpisodes: null, PosterUrl: "" });
            var episodes = BangumiClient.ParseEpisodes(JsonNode.Parse("""
                [{"ep":1,"sort":13,"name":"a","name_cn":"甲","airdate":"2026-01-16","type":0},{"ep":1,"sort":1,"name":"op","type":2},{"sort":1.5,"name":"sp","type":1}]
                """)!.AsArray());
            Check(episodes.Count == 2 && episodes[0] is { Number: 1, Sort: 13, NameCn: "甲" } && episodes[1] is { Number: 1.5, Type: 1 });
            var search = BangumiClient.ParseSearch(JsonNode.Parse("""
                {"data":[
                 {"id":400602,"name":"葬送のフリーレン","name_cn":"葬送的芙莉莲","date":"2023-09-29","infobox":[{"key":"别名","value":[{"v":"Frieren: Beyond Journey's End"},{"v":"Sousou no Frieren"},{"v":"葬送的芙莉蓮"}]}]},
                 {"id":515759,"name":"葬送のフリーレン 第2期","name_cn":"葬送的芙莉莲 第二季","date":"2026-01-16","infobox":[{"key":"中文名","value":"葬送的芙莉莲 第二季"}]}]}
                """));
            Check(search.Count == 2 && search[1].Aliases.SequenceEqual(["葬送的芙莉莲 第二季"]));
            Check(BangumiClient.Pick(search, "葬送的芙莉莲", 0) == 400602 && BangumiClient.Pick(search, "葬送的芙莉莲", 2) == 515759);
            Check(BangumiClient.Pick(search, "Sousou no Frieren", 1) == 400602);
            // Only the first season lists the traditional / romaji names; its sequel is still chosen for season 2.
            Check(BangumiClient.Pick(search, "葬送的芙莉蓮", 2) == 515759 && BangumiClient.Pick(search, "Sousou no Frieren", 2) == 515759);
            Check(BangumiClient.Pick(search, "葬送的芙莉蓮", 3) == 400602);
            Check(BangumiClient.Pick(search, "完全无关的作品", 0) == null && BangumiClient.Pick([], "葬送的芙莉莲", 0) == null);
            var tmdb = TmdbClient.Parse(JsonNode.Parse("""{"id":209867,"name":"葬送的芙莉莲","original_name":"葬送のフリーレン","overview":"简介","first_air_date":"2023-09-29","vote_average":8.8,"poster_path":"/p.jpg"}""")!);
            Check(tmdb is { Source: "tmdb", SourceId: 209867, Name: "葬送のフリーレン", NameCn: "葬送的芙莉莲", Score: 8.8, PosterUrl: "https://image.tmdb.org/t/p/w342/p.jpg" });
        });
        Test("Posters are stored only for JPEG, PNG, WebP and GIF data", () =>
        {
            Check(LibraryService.ImageExtension([0xFF, 0xD8, 0xFF, 0xE0]) == ".jpg" && LibraryService.ImageExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]) == ".png");
            Check(LibraryService.ImageExtension("RIFF\0\0\0\0WEBPVP8 "u8) == ".webp" && LibraryService.ImageExtension("GIF89a"u8) == ".gif");
            Check(LibraryService.ImageExtension("<svg xmlns='http://www.w3.org/2000/svg'/>"u8) == null && LibraryService.ImageExtension([]) == null);
        });

        var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ARIA2FAST_DATA_DIR"] = temporary }).Build();
            var store = new StateStore(config);
            Test("Walk skips unfinished downloads, hidden and NAS folders and non-video files", () =>
            {
                var root = Path.Combine(temporary, "walk");
                void Touch(string path) { var full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "x"); }
                foreach (var path in new[] { "作品/Season 1/a - 01.mkv", "作品/Season 1/a - 02.mkv", "作品/Season 1/a - 02.mkv.aria2", "作品/Season 1/a - 01.ass", ".trash/b.mkv", "@eaDir/c.mp4", "._d.mkv", "e.MP4" }) Touch(path);
                var files = LibraryScanner.Walk(root, out var truncated).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
                Check(!truncated && files.SequenceEqual(["e.MP4", "作品/Season 1/a - 01.mkv"]), string.Join(", ", files));
                Check(LibraryScanner.Walk(Path.Combine(temporary, "missing"), out _).Count == 0);
            });
            Test("Corrupt library index is set aside, reported and rebuilt", () =>
            {
                Directory.CreateDirectory(Path.Combine(temporary, "library"));
                File.WriteAllText(Path.Combine(temporary, "library", "library.json"), "{broken");
                var library = new LibraryStore(store);
                Check(library.Read().Series.Count == 0 && Directory.EnumerateFiles(Path.Combine(temporary, "library"), "library.json.*.corrupt").Any());
                Check(store.Read().Notices.Any(n => n.Level == "warning" && n.Message.Contains("媒体库索引")));
                library.Update(l => l.LastScan = DateTimeOffset.UnixEpoch);
                Check(new LibraryStore(store).Read().LastScan == DateTimeOffset.UnixEpoch);
            });
            Test("A completed local download records its subscription, following magnet parents", () =>
            {
                var root = store.Read().Settings.DownloadDirectory;
                store.Update(s => s.Subscriptions.Add(new Subscription { Id = "sub", Url = "https://example.com/rss", History = [new("k", "t", "u", "parent-gid", DateTimeOffset.UtcNow)] }));
                var library = new LibraryStore(store);
                var http = new HttpGateway(store);
                using var memory = new MemoryCache(new MemoryCacheOptions());
                var ai = new AiService(store, http);
                var service = new LibraryService(store, library, new PlaybackStore(store), http, new MikanService(store, http, memory, ai), new BangumiClient(store, http), new TmdbClient(store, http), ai, NullLogger<LibraryService>.Instance);
                var files = new JsonArray(
                    new JsonObject { ["path"] = Path.Combine(root, "作品", "[G] 作品 - 01.mkv"), ["selected"] = "true" },
                    new JsonObject { ["path"] = Path.Combine(root, "作品", "[G] 作品 - 01.ass"), ["selected"] = "true" },
                    new JsonObject { ["path"] = Path.Combine(root, "作品", "[G] 作品 - 02.mkv"), ["selected"] = "false" },
                    new JsonObject { ["path"] = Path.Combine(temporary, "elsewhere.mkv"), ["selected"] = "true" });
                service.Completed("child-gid", new JsonObject { ["following"] = "parent-gid", ["files"] = files });
                var origins = library.Read().Origins;
                Check(origins.Count == 1 && origins["作品/[G] 作品 - 01.mkv"] is { SubscriptionId: "sub", Gid: "child-gid" }, JsonSerializer.Serialize(origins));
                service.Completed("unrelated-gid", new JsonObject { ["files"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(root, "x.mkv") }) });
                Check(library.Read().Origins.Count == 1);
            });
        }
        finally { Directory.Delete(temporary, true); }
        return count;
    }
}
