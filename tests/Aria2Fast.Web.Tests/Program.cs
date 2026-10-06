using System.Xml;
using Aria2Fast.Service;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Services;
using Microsoft.Extensions.Configuration;

var passed = 0;
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

Test("RSS enclosure wins over page link, preserves Unicode", () =>
{
    var rss = FeedService.Parse("<rss><channel><title>测试</title><item><guid>one</guid><title>剧集 01</title><link>https://example.com/page</link><enclosure url='/a.torrent' length='1024'/></item></channel></rss>", "https://example.com/feed");
    Assert(rss.Title == "测试" && rss.Items.Single().Url == "https://example.com/a.torrent");
});
Test("Atom enclosure parsing", () => Assert(FeedService.Parse("<feed xmlns='http://www.w3.org/2005/Atom'><title>Atom</title><entry><id>x</id><title>E01</title><link rel='enclosure' href='magnet:?xt=urn:btih:abc'/></entry></feed>", "https://example.com").Items.Single().Url.StartsWith("magnet:")));
Test("Feed repeated IDs are deduplicated", () => Assert(FeedService.Parse("<rss><channel><item><guid>x</guid><link>https://example.com/1</link></item><item><guid>x</guid><link>https://example.com/2</link></item></channel></rss>", "https://example.com").Items.Count == 1));
Test("XML external entities prohibited", () => Throws<XmlException>(() => FeedService.Parse("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss><channel><title>&x;</title></channel></rss>", "https://example.com")));
Test("Keyword OR matches desktop behavior", () => { Assert(Validation.Matches("[1080p] 中文", "720p|1080P", false)); Assert(!Validation.Matches("480p", "720p|1080p", false)); });
Test("Invalid regex rejected", () => Throws<ArgumentException>(() => Validation.Matches("test", "[", true)));
Test("Safe directory segment preserves Chinese", () => { Assert(Validation.Segment("魔法/少女:01") == "魔法_少女_01"); Throws<ArgumentException>(() => Validation.Segment("..")); });
Test("HTTP URL excludes userinfo and file scheme", () => { Throws<ArgumentException>(() => Validation.HttpUrl("file:///a")); Throws<ArgumentException>(() => Validation.HttpUrl("http://user:pass@example.com")); });
Test("AI JSON strips fences", () => Assert(AiService.CleanJson("before\n" + '{' + "\"score\":8}" + "\nafter") == "{\"score\":8}"));
Test("AI protocol custom version", () => Assert(AiProtocol.BuildRequestUrl(AiProtocolType.OpenAIChatCompletions, "https://example.com/v1", "test") == "https://example.com/v1/chat/completions"));

var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-tests-" + Guid.NewGuid().ToString("N"));
Test("Subscription paths match default, sanitized name, AI and season order", () =>
{
    Assert(DownloadPaths.Compose("D:\\Downloads\\", "作品:名称", 2, "识别名") == "D:\\Downloads/作品_名称/识别名/Season 2");
    Assert(DownloadPaths.Compose("/downloads/", "", 0) == "/downloads/");
});
Test("Desktop Hot thresholds and latest first release per group", () =>
{
    var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(8));
    string Group(int episode, string date) => $"<div class='subgroup-text'><a class='mikan-rss'>RSS</a></div><div class='episode-table'><table><tbody><tr><td></td><td><a class='magnet-link-wrap'>作品 - {episode:00}</a></td><td>1 GB</td><td>{date}</td></tr><tr><td></td><td><a class='magnet-link-wrap'>作品 - 99</a></td></tr></tbody></table></div>";
    var html = string.Concat(Enumerable.Range(1, 5).Select(i => Group(i, "2026/10/05 08:00")));
    var badges = MikanBadges.Parse(html, now);
    Assert(badges.Hot == "pink" && badges.LatestEpisode == 5 && badges.UpdatedGroups == 5);
    badges = MikanBadges.Parse(html + Group(6, "2026/10/04 08:00") + Group(12, "2026/10/06 08:00"), now);
    Assert(badges.Hot == "purple" && badges.LatestEpisode == 12 && badges.UpdatedGroups == 5);
    Assert(new AnimeBadges(4, 0, 0).Hot == "" && new AnimeBadges(6, 0, 0).Hot == "pink");
});
Directory.CreateDirectory(temporary);
try
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ARIA2FAST_DATA_DIR"] = temporary }).Build();
    var store = new StateStore(config);
    Test("AI results survive restart and enrich an already cached anime detail", () =>
    {
        const string original = "Original summary";
        const string translated = "Cached translation";
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original)));
        var review = new Aria2Fast.Web.Models.AiReview(8, "Cached review", DateTimeOffset.UtcNow, "Source link");
        store.Update(s => { s.Settings.MikanBaseUrl = "https://example.com"; s.Translations[key] = translated; s.Reviews["123:"] = review; });
        var restarted = new StateStore(config);
        var ai = new AiService(restarted, new HttpGateway(restarted));
        Assert(ai.Translate(original, CancellationToken.None).GetAwaiter().GetResult() == translated);
        Assert(ai.CachedTranslation("Changed summary") is null);
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        using (var entry = memory.CreateEntry("detail:https://example.com:123"))
            entry.Value = new Aria2Fast.Web.Models.AnimeDetail("123", "Title", original, [], null, null);
        var mikan = new MikanService(restarted, new HttpGateway(restarted), memory, ai);
        var detail = mikan.Detail("123", CancellationToken.None).GetAwaiter().GetResult();
        Assert(detail.Summary == translated && detail.Review == review);
    });
    Test("Atomic persistence and snapshot isolation", () => { store.Update(s => s.Settings.MikanBaseUrl = "https://example.com"); var copy = store.Read(); copy.Settings.MikanBaseUrl = "changed"; Assert(new StateStore(config).Read().Settings.MikanBaseUrl == "https://example.com"); });
    Test("Failed state update does not change persisted data", () => { Throws<InvalidOperationException>(() => store.Update(s => { s.SelectedNodeId = "bad"; throw new InvalidOperationException(); })); Assert(store.Read().SelectedNodeId == "local"); });
    Test("Desktop backup imports and deduplicates", () => { var backup = new BackupService(store); const string json = "[{\"Url\":\"https://example.com/rss\",\"Name\":\"测试\",\"Path\":\"/downloads\",\"AlreadyAddedDownloadModel\":[{\"Url\":\"https://example.com/old\",\"Name\":\"旧集\"}]}]"; Assert(backup.Import(json) == 1); Assert(backup.Import(json) == 0); var item = store.Read().Subscriptions.Single(); Assert(item.Initialized && item.Directory == "/downloads" && item.History.Count == 1); });
    Test("Backup merge retains local rules and merges remote history", () =>
    {
        var backup = new BackupService(store);
        var imported = backup.Import("[{\"url\":\"https://example.com/rss\",\"name\":\"remote-name\",\"history\":[{\"key\":\"new-key\",\"title\":\"new\",\"url\":\"https://example.com/new\",\"time\":\"2026-01-01T00:00:00Z\"}]}]");
        var merged = store.Read().Subscriptions.Single();
        Assert(imported == 0 && merged.Name == "测试" && merged.History.Count == 2);
    });
    Test("Traversal outside download root rejected", () => Throws<ArgumentException>(() => Validation.LocalFile(temporary, Path.Combine(temporary, "..", "outside"))));
    Test("Local existing file allowed", () => { var path = Path.Combine(temporary, "电影.mkv"); File.WriteAllText(path, "test"); Assert(Validation.LocalFile(temporary, path) == path); });
    Test("Corrupt state fails rather than resets", () => { File.WriteAllText(Path.Combine(temporary, "state.json"), "invalid-json"); Throws<System.Text.Json.JsonException>(() => new StateStore(config)); });
}
finally { Directory.Delete(temporary, true); }
Console.WriteLine($"{passed} tests passed.");
