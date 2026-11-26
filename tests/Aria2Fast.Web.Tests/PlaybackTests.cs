using System.Text;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static class PlaybackTests
{
    static void Check(bool condition, string message = "Playback assertion failed") { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    public static int Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }

        Test("Subtitle language tags from fansub file names", () =>
        {
            void Expect(string suffix, string label, string language) { var d = Subtitles.Describe(suffix); Check(d.Label == label && d.Language == language, suffix + " → " + d); }
            Expect("sc", "简体中文", "zh-Hans");
            Expect("chs", "简体中文", "zh-Hans");
            Expect("chs&jpn", "简日双语", "zh-Hans");
            Expect("jpsc", "简日双语", "zh-Hans");
            Expect("简日", "简日双语", "zh-Hans");
            Expect("zh-Hans", "简体中文", "zh-Hans");
            Expect("tc", "繁體中文", "zh-Hant");
            Expect("cht_jp", "繁日双语", "zh-Hant");
            Expect("zh-Hant", "繁體中文", "zh-Hant");
            Expect("繁体", "繁體中文", "zh-Hant");
            Expect("zh", "中文", "zh");
            Expect("ja", "日本語", "ja");
            Expect("eng", "English", "en");
            Expect("", "字幕", "und");
            Expect("signs", "signs", "und");
        });

        var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Test("Subtitles beside the video, in a Subs folder, and by episode when named differently", () =>
            {
                var dir = Path.Combine(temporary, "subs-test");
                Directory.CreateDirectory(Path.Combine(dir, "Subs"));
                var video = Path.Combine(dir, "[G] Work - 01 [1080p].mkv");
                foreach (var name in new[] { "[G] Work - 01 [1080p].mkv", "[G] Work - 01 [1080p].tc.ass", "[G] Work - 01 [1080p].sc.ass", "Subs/[G] Work - 01 [1080p].jp.srt",
                             "[G] Work - 02 [1080p].sc.ass", "[G] Work - 01 [1080p]v2.sc.ass", "[G] Work - 01 [1080p].txt" })
                    File.WriteAllText(Path.Combine(dir, name), "x");
                var tracks = Subtitles.Find(video, 1);
                Check(tracks.Select(t => t.Label).SequenceEqual(["简体中文", "繁體中文", "日本語"]), string.Join(",", tracks));
                Check(tracks[0].Format == "ASS" && tracks[2].Format == "SRT" && tracks[2].Path.Contains("Subs"));

                var other = Path.Combine(temporary, "subs-fallback");
                Directory.CreateDirectory(other);
                foreach (var name in new[] { "[G] Show [03][1080p][HEVC].mkv", "[G] Show [03].chs.ass", "[G] Show [04].chs.ass", "[H] Other [03].chs.ass" })
                    File.WriteAllText(Path.Combine(other, name), "x");
                var fallback = Subtitles.Find(Path.Combine(other, "[G] Show [03][1080p][HEVC].mkv"), 3);
                Check(fallback.Count == 1 && fallback[0].Path.EndsWith("[G] Show [03].chs.ass") && fallback[0].Label == "简体中文", string.Join(",", fallback));
                Check(Subtitles.Find(Path.Combine(temporary, "missing", "x.mkv"), 1).Count == 0);
            });
        }
        finally { Directory.Delete(temporary, true); }

        Test("SRT and ASS convert to WebVTT; UTF-16 and GBK files decode", () =>
        {
            var srt = Subtitles.ToVtt(Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,500\r\n<i>Hello</i> & <font color=\"red\">world</font>\r\n{\\an8}line two\r\n\r\n2\r\n00:01:02,5 --> 00:01:03,000\r\n\r\n3\r\nbroken\r\n"), ".srt");
            Check(srt == "WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n<i>Hello</i> &amp; world\nline two\n\n", srt);

            var ass = string.Join("\n",
                "[Script Info]", "Title: test", "", "[V4+ Styles]", "Format: Name, Fontname", "Style: Default,Arial", "",
                "[Events]", "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
                "Dialogue: 0,0:00:05.00,0:00:06.50,Default,,0,0,0,,{\\fad(100,100)}第二句，带逗号\\N下一行",
                "Comment: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,注释",
                "Dialogue: 0,0:00:01.00,0:00:02.00,Sign,,0,0,0,,{\\an8\\pos(10,10)}顶部 <标签>",
                "Dialogue: 0,0:00:01.00,0:00:02.00,Draw,,0,0,0,,{\\p1}m 0 0 l 100 0 100 100{\\p0}",
                "Dialogue: 0,0:00:03.00,0:00:02.00,Default,,0,0,0,,倒序时间");
            var vtt = Subtitles.ToVtt(Encoding.UTF8.GetBytes(ass), ".ass");
            Check(vtt == "WEBVTT\n\n00:00:01.000 --> 00:00:02.000 line:0\n顶部 &lt;标签&gt;\n\n00:00:05.000 --> 00:00:06.500\n第二句，带逗号\n下一行\n\n", vtt);

            Check(Subtitles.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("字幕")]) == "字幕");
            Check(Subtitles.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("字幕")]) == "字幕");
            Check(Subtitles.Decode(Encoding.GetEncoding("GB18030").GetBytes("简体中文字幕")) == "简体中文字幕");
            Check(Subtitles.ToVtt(Encoding.UTF8.GetBytes("WEBVTT\n\n00:00.000 --> 00:01.000\nx\n"), ".vtt").StartsWith("WEBVTT\n\n00:00.000"));
            Check(Subtitles.ToVtt(Encoding.UTF8.GetBytes("00:00.000 --> 00:01.000\nx\n"), ".vtt").StartsWith("WEBVTT\n\n"));
        });

        Test("Resume continues the last unfinished video, else the next unwatched episode", () =>
        {
            LibraryEpisode Ep(string id, double? number, LibraryEpisodeKind kind = LibraryEpisodeKind.Episode) => new() { Id = id, Number = number, Kind = kind, Path = id };
            var list = LibraryService.Ordered([Ep("sp", 1, LibraryEpisodeKind.Special), Ep("e3", 3), Ep("e1", 1), Ep("e2", 2), Ep("op", null, LibraryEpisodeKind.Extra)]).ToList();
            Check(list.Select(e => e.Id).SequenceEqual(["e1", "e2", "e3", "sp", "op"]));
            var t = DateTimeOffset.UtcNow;
            PlaybackProgress P(double position, bool watched, int minutes) => new(position, 1440, watched, t.AddMinutes(minutes));
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress>())?.Id == "e1");
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress> { ["e1"] = P(0, true, 1) })?.Id == "e2");
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress> { ["e1"] = P(0, true, 1), ["e2"] = P(300, false, 2) })?.Id == "e2");
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress> { ["e2"] = P(300, false, 1), ["e3"] = P(1440, true, 2) })?.Id == "e1");
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress> { ["sp"] = P(600, false, 3), ["e1"] = P(0, true, 1) })?.Id == "sp");
            Check(LibraryService.Resume(list, new Dictionary<string, PlaybackProgress> { ["e1"] = P(0, true, 1), ["e2"] = P(0, true, 1), ["e3"] = P(0, true, 1) }) == null);
            Check(!LibraryService.InProgress(P(5, false, 0)) && LibraryService.InProgress(P(600, false, 0)) && !LibraryService.InProgress(P(1300, false, 0)) && LibraryService.InProgress(new(600, 0, false, t)));
        });

        Test("Scans keep episode corrections and drop those of removed files", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var files = new[] { new LibraryScanner.ScannedFile("Work/Work Special.mkv", 1, now), new LibraryScanner.ScannedFile("Work/[G] Work - 02.mkv", 1, now) };
            var previous = new LibraryState
            {
                Overrides = new()
                {
                    ["Work/Work Special.mkv"] = new(7, LibraryEpisodeKind.Episode, "ai"),
                    ["Work/[G] Work - 02.mkv"] = new(null, LibraryEpisodeKind.Extra, "manual"),
                    ["Work/gone.mkv"] = new(1, LibraryEpisodeKind.Episode, "manual")
                }
            };
            var built = LibraryScanner.Build(files, [], new Dictionary<string, Subscription>(), previous, now);
            var special = built.Episodes.Single(e => e.Path == "Work/Work Special.mkv");
            var second = built.Episodes.Single(e => e.Path == "Work/[G] Work - 02.mkv");
            Check(special is { Number: 7, Kind: LibraryEpisodeKind.Episode, NumberSource: "ai" } && second is { Number: null, Kind: LibraryEpisodeKind.Extra, NumberSource: "manual" });
            Check(built.Overrides.Count == 2 && !built.Overrides.ContainsKey("Work/gone.mkv"));
            Check(ReleaseNames.ParseFile("[G] 作品 特别篇.mkv").Kind == LibraryEpisodeKind.Special && ReleaseNames.ParseFile("作品 総集編.mp4").Kind == LibraryEpisodeKind.Special);
        });

        temporary = Path.Combine(Path.GetTempPath(), "aria2fast-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ARIA2FAST_DATA_DIR"] = temporary }).Build();
            var store = new StateStore(config);
            var root = Path.Combine(temporary, "downloads");
            store.Update(s => { s.Settings.DownloadDirectory = root; s.PasswordHash = "first-hash"; });
            var library = new LibraryStore(store);
            var playback = new PlaybackStore(store);
            var links = new MediaLinks(new EphemeralDataProtectionProvider(), store);
            var service = new PlaybackService(store, library, playback, links);
            Directory.CreateDirectory(Path.Combine(root, "Work"));
            foreach (var name in new[] { "[G] Work - 01.mkv", "[G] Work - 01.sc.srt", "[G] Work - 02.mp4", "[G] Work - 03.webm" })
                File.WriteAllText(Path.Combine(root, "Work", name), name.EndsWith(".srt") ? "1\n00:00:01,000 --> 00:00:02,000\n你好\n" : "video");
            var files = Directory.EnumerateFiles(Path.Combine(root, "Work")).Where(ReleaseNames.IsVideo)
                .Select(f => new LibraryScanner.ScannedFile(Path.GetRelativePath(root, f).Replace('\\', '/'), 5, DateTimeOffset.UtcNow)).ToList();
            library.Replace(current => LibraryScanner.Build(files, [], new Dictionary<string, Subscription>(), current, DateTimeOffset.UtcNow));
            string Id(int n) => LibraryScanner.Hash($"Work/[G] Work - {n:00}." + (n == 1 ? "mkv" : n == 2 ? "mp4" : "webm"));

            Test("Player page lists subtitles, neighbours and the stream type", () =>
            {
                var page = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(service.Player(Id(2)), StateStore.Json))!;
                Check(page["previous"]!.ToString() == Id(1) && page["next"]!.ToString() == Id(3) && page["type"]!.ToString() == "video/mp4", page.ToJsonString());
                var first = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(service.Player(Id(1)), StateStore.Json))!;
                Check(first["previous"] == null && first["subtitles"]!.AsArray().Count == 1 && first["subtitles"]![0]!["label"]!.ToString() == "简体中文" && first["type"]!.ToString() == "video/x-matroska");
                Check(service.Subtitle(Id(1), 0).Contains("00:00:01.000 --> 00:00:02.000\n你好"));
                Throws<FileNotFoundException>(() => service.Subtitle(Id(1), 1));
                Throws<FileNotFoundException>(() => service.Player("0000000000000000"));
            });

            Test("Progress: watched past 90 %, kept when replayed, cleared by unmarking", () =>
            {
                Check(!service.Save(Id(1), 100, 1400, null).Watched);
                Check(service.Save(Id(1), 1300, 1400, null).Watched);
                Check(service.Save(Id(1), 20, 0, null) is { Watched: true, Duration: 1400 });
                Check(service.Save(Id(1), 5000, 1400, null).Position == 1400);
                service.Mark(Id(1), false);
                Check(!playback.Read().Episodes.ContainsKey(Id(1)));
                Check(service.MarkSeries(library.Read().Series.Single().Id, true) == 3 && playback.Read().Episodes.Values.All(p => p.Watched && p.Position == 0));
                Throws<ArgumentException>(() => service.Save(Id(1), double.NaN, 10, null));
                Throws<ArgumentException>(() => service.Save(Id(1), -1, 10, null));
                Throws<FileNotFoundException>(() => service.Save("0000000000000000", 1, 10, null));
            });

            Test("External links: signed per video and revoked by a password change", () =>
            {
                var link = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(service.Link(Id(1)), StateStore.Json))!["url"]!.ToString();
                Check(link.StartsWith("/media/") && link.EndsWith("/" + Uri.EscapeDataString("[G] Work - 01.mkv")), link);
                var token = link.Split('/')[2];
                Check(service.Open(token) == Path.Combine(root, "Work", "[G] Work - 01.mkv"));
                Check(service.Open(token[..^2] + "AA") == null && service.Open("not-a-token") == null);
                var other = new MediaLinks(new EphemeralDataProtectionProvider(), store);
                Check(other.EpisodeId(token) == null); // another key ring
                store.Update(s => s.PasswordHash = "second-hash");
                Check(service.Open(token) == null);
                var fresh = links.Token(Id(2));
                File.Delete(Path.Combine(root, "Work", "[G] Work - 02.mp4"));
                Check(links.EpisodeId(fresh) == Id(2) && service.Open(fresh) == null); // file gone
            });

            Test("Notifications link to the player once a public address is set", () =>
            {
                var http = new HttpGateway(store);
                var ai = new AiService(store, http);
                using var memory = new MemoryCache(new MemoryCacheOptions());
                var libraryService = new LibraryService(store, library, playback, http, new MikanService(store, http, memory, ai), new BangumiClient(store, http), new TmdbClient(store, http), ai, new NfoWriter(store, library, NullLogger<NfoWriter>.Instance), NullLogger<LibraryService>.Instance);
                var task = new JsonObject { ["files"] = new JsonArray(
                    new JsonObject { ["path"] = Path.Combine(root, "Work", "[G] Work - 03.webm"), ["selected"] = "true" },
                    new JsonObject { ["path"] = Path.Combine(root, "Work", "[G] Work - 01.mkv"), ["selected"] = "true" },
                    new JsonObject { ["path"] = Path.Combine(root, "Work", "notes.txt"), ["selected"] = "true" }) };
                Check(libraryService.PlayLink(task) == null);
                store.Update(s => s.Settings.PublicUrl = "https://media.example/aria2/");
                Check(libraryService.PlayLink(task) == "https://media.example/aria2/#play/" + Id(1), libraryService.PlayLink(task) ?? "no link");
                store.Update(s => s.Settings.Library.Enabled = false);
                Check(libraryService.PlayLink(task) == null);
                store.Update(s => s.Settings.Library.Enabled = true);

                // A missing or empty download directory (an unmounted share) must not empty the index.
                var before = library.Read().Episodes.Count;
                Directory.Delete(root, true);
                libraryService.Scan(CancellationToken.None).GetAwaiter().GetResult();
                Check(library.Read().Episodes.Count == before && library.Read().LastScanWarning != null);
                Directory.CreateDirectory(root);
                libraryService.Scan(CancellationToken.None).GetAwaiter().GetResult();
                Check(library.Read().Episodes.Count == before);
                File.WriteAllText(Path.Combine(root, "other.txt"), "x");
                libraryService.Scan(CancellationToken.None).GetAwaiter().GetResult();
                Check(library.Read().Episodes.Count == 0 && library.Read().LastScanWarning == null);
            });
        }
        finally { Directory.Delete(temporary, true); }
        return count;
    }
}
