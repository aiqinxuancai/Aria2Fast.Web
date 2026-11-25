using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
using Aria2Fast.Web.Services.Sources;

static class AnimeSourcesTests
{
    static void Check(bool condition, string message = "Anime source assertion failed") { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    const string Hash = "0123456789abcdef0123456789abcdef01234567";

    public static int Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }

        Test("Nyaa feed: .torrent link, GiB size, info hash, seeders and view page", () =>
        {
            var item = FeedService.Parse($"""
                <rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" version="2.0"><channel><title>Nyaa</title><item>
                <title>[Group] Show - 01 [1080p]</title><link>https://nyaa.si/download/1.torrent</link>
                <guid isPermaLink="true">https://nyaa.si/view/1</guid><pubDate>Mon, 05 Oct 2026 12:00:00 -0000</pubDate>
                <nyaa:seeders>120</nyaa:seeders><nyaa:leechers>8</nyaa:leechers><nyaa:infoHash>{Hash.ToUpperInvariant()}</nyaa:infoHash>
                <nyaa:category>Anime - Non-English-translated</nyaa:category><nyaa:size>1.4 GiB</nyaa:size></item></channel></rss>
                """, "https://nyaa.si/?page=rss").Items.Single();
            Check(item.Url == "https://nyaa.si/download/1.torrent" && item.PageUrl == "https://nyaa.si/view/1");
            Check(item.Size == "1503238554" && item.InfoHash == Hash && item.Seeders == 120 && item.Leechers == 8);
            Check(item.Category == "Anime - Non-English-translated" && item.Published?.Day == 5);
        });
        Test("DMHY feed: magnet enclosure, page link and hash from magnet; placeholder length ignored", () =>
        {
            var item = FeedService.Parse($"""
                <rss><channel><item><title>动画 01</title><link>https://share.dmhy.org/topics/view/1_a.html</link>
                <enclosure url="magnet:?xt=urn:btih:{Hash}&amp;tr=https://t.example/announce" length="1" type="application/x-bittorrent"/>
                <category domain="https://share.dmhy.org/topics/list/sort_id/2">動畫</category></item></channel></rss>
                """, "https://share.dmhy.org/topics/rss/rss.xml").Items.Single();
            Check(item.Url.StartsWith("magnet:?xt=urn:btih:" + Hash) && item.PageUrl == "https://share.dmhy.org/topics/view/1_a.html");
            Check(item.InfoHash == Hash && item.Size == "" && item.Category == "動畫");
        });
        Test("ACG.RIP feed: relative .torrent enclosure with byte length", () =>
        {
            var item = FeedService.Parse("<rss><channel><item><title>作品 02</title><link>https://acg.rip/t/2</link><guid>https://acg.rip/t/2</guid><enclosure url='/t/2.torrent' length='734003200' type='application/x-bittorrent'/></item></channel></rss>",
                "https://acg.rip/.xml?term=x").Items.Single();
            Check(item.Url == "https://acg.rip/t/2.torrent" && item.Size == "734003200" && item.PageUrl == "https://acg.rip/t/2");
        });
        Test("Torznab feed: enclosure download link, attributes, peers to leechers and comments page", () =>
        {
            var item = FeedService.Parse($"""
                <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><item><title>Show S01E03</title>
                <guid>https://indexer.example/details/3</guid><link>http://prowlarr:9696/1/download?apikey=k&amp;link=x</link>
                <comments>https://indexer.example/details/3</comments><size>2147483648</size>
                <enclosure url="http://prowlarr:9696/1/download?apikey=k&amp;link=x" length="0" type="application/x-bittorrent"/>
                <torznab:attr name="seeders" value="40"/><torznab:attr name="peers" value="55"/><torznab:attr name="infohash" value="{Hash}"/>
                <torznab:attr name="magneturl" value="magnet:?xt=urn:btih:{Hash}"/></item></channel></rss>
                """, "http://prowlarr:9696/1/api").Items.Single();
            Check(item.Url.StartsWith("http://prowlarr:9696/1/download") && item.PageUrl == "https://indexer.example/details/3");
            Check(item.Size == "2147483648" && item.Seeders == 40 && item.Leechers == 15 && item.InfoHash == Hash);
            var magnet = FeedService.Parse($"<rss><channel><item><title>T</title><link>https://indexer.example/details/4</link><attr name='magneturl' value='magnet:?xt=urn:btih:{Hash}'/><attr name='size' value='1048576'/></item></channel></rss>", "http://jackett:9117/api").Items.Single();
            Check(magnet.Url.StartsWith("magnet:") && magnet.Size == "1048576" && magnet.PageUrl == "https://indexer.example/details/4");
        });
        Test("Kisssub show page becomes a magnet while keeping the legacy dedupe key", () =>
        {
            var page = $"https://www.kisssub.org/show-{Hash.ToUpperInvariant()}.html";
            var item = FeedService.Parse($"<rss><channel><item><title>番剧 03</title><link>{page}</link></item></channel></rss>", "https://www.kisssub.org/rss.xml").Items.Single();
            Check(item.Url.StartsWith("magnet:?xt=urn:btih:" + Hash + "&dn=") && item.PageUrl == page && item.InfoHash == Hash);
            Check(item.Key == Key(page), "Existing subscription history must keep matching");
        });
        Test("Info hash without any link synthesizes a magnet", () =>
        {
            var item = FeedService.Parse($"<rss><channel><item><guid>x</guid><title>Raw</title><infoHash>{Hash}</infoHash></item></channel></rss>", "https://example.com").Items.Single();
            Check(item.Url == $"magnet:?xt=urn:btih:{Hash}&dn=Raw");
        });
        Test("Plain feeds are unchanged: enclosure URL, key and size", () =>
        {
            var item = FeedService.Parse("<rss><channel><item><title>E01</title><link>https://example.com/page</link><enclosure url='https://example.com/file.bin' length='4096'/></item></channel></rss>", "https://example.com").Items.Single();
            Check(item.Url == "https://example.com/file.bin" && item.Size == "4096" && item.Key == Key("https://example.com/file.bin") && item.PageUrl == "https://example.com/page");
            Check(FeedService.Parse("<rss><channel><item><title>x</title><link>javascript:alert(1)</link></item></channel></rss>", "https://example.com").Items.Count == 0);
        });
        Test("Torznab error document is reported instead of an empty feed", () =>
            Throws<InvalidDataException>(() => FeedService.Parse("<?xml version='1.0'?><error code='100' description='Invalid API Key'/>", "http://prowlarr:9696/1/api")));
        Test("Size parser handles binary units, separators and placeholders", () =>
        {
            Check(FeedService.ParseSize("320.5 MiB") == "336068608" && FeedService.ParseSize("2 TB") == "2199023255552" && FeedService.ParseSize("1,5 GB") == "1610612736");
            Check(FeedService.ParseSize("1,234,567") == "1234567" && FeedService.ParseSize("512 bytes") == "512" && FeedService.ParseSize("1") == null);
            Check(FeedService.ParseSize("abc") == null && FeedService.ParseSize("") == null && FeedService.ParseSize("1 iB") == null);
        });

        var settings = new WebSettings();
        settings.AnimeSources.Torznab.Add(new() { Id = "p", Name = "Prowlarr", Url = "http://prowlarr:9696/1/api", ApiKey = "secret key" });
        Test("Torznab API key is added only to requests for the configured endpoint", () =>
        {
            Check(TorznabUrls.WithApiKey("http://prowlarr:9696/1/api?t=search&q=x", settings) == "http://prowlarr:9696/1/api?t=search&q=x&apikey=secret%20key");
            Check(TorznabUrls.WithApiKey("http://PROWLARR:9696/1/api/?q=x", settings).EndsWith("apikey=secret%20key"));
            Check(TorznabUrls.WithApiKey("http://prowlarr:9696/2/api?q=x", settings) == "http://prowlarr:9696/2/api?q=x");
            Check(TorznabUrls.WithApiKey("http://prowlarr:9696/1/api?apikey=own", settings) == "http://prowlarr:9696/1/api?apikey=own");
            Check(TorznabUrls.WithApiKey("https://acg.rip/.xml?term=x", settings) == "https://acg.rip/.xml?term=x");
        });
        Test("API keys are masked for display and backups", () =>
        {
            Check(TorznabUrls.Mask("http://p/1/download?apikey=abc&link=x#f") == "http://p/1/download?apikey=***&link=x#f");
            Check(TorznabUrls.Mask("http://j/dl/a/?jackett_apikey=abc&path=y") == "http://j/dl/a/?jackett_apikey=***&path=y");
            Check(TorznabUrls.Mask("https://example.com/a.torrent") == "https://example.com/a.torrent");
        });
        Test("Search URLs: escaping, mirrors with path prefixes, categories and secret-free Torznab subscriptions", () =>
        {
            var sources = new AnimeSourcesSettings { Acgnx = new() { Enabled = true, BaseUrl = "" }, Dmhy = new() { Enabled = true, BaseUrl = "https://mirror.example/dmhy/" } };
            sources.Torznab.Add(new() { Id = "p", Name = "Prowlarr", Url = "http://prowlarr:9696/1/api", ApiKey = "k", Categories = "5070,5000" });
            sources.Torznab.Add(new() { Id = "off", Name = "Off", Url = "http://off/api", Enabled = false });
            var enabled = AnimeSearchService.Enabled(sources).ToDictionary(s => s.Id);
            Check(enabled.Keys.SequenceEqual(["acgrip", "nyaa", "dmhy", "acgnx", "torznab:p"]));
            var query = new AnimeSearchQuery("葬送 & 01");
            Check(enabled["acgrip"].FeedUrl(query) == "https://acg.rip/.xml?term=%E8%91%AC%E9%80%81%20%26%2001");
            Check(enabled["acgrip"].FeedUrl(query with { Category = "1" }).StartsWith("https://acg.rip/1.xml?term="));
            Check(enabled["nyaa"].FeedUrl(query with { Category = "1_3", Filter = "2" }).EndsWith("&c=1_3&f=2"));
            Check(enabled["nyaa"].FeedUrl(query).EndsWith("&c=1_0&f=0"));
            Check(enabled["dmhy"].FeedUrl(query with { Category = "2" }) == "https://mirror.example/dmhy/topics/rss/rss.xml?keyword=%E8%91%AC%E9%80%81%20%26%2001&sort_id=2");
            Check(enabled["acgnx"].FeedUrl(query).StartsWith("https://share.acgnx.se/rss.xml?keyword="));
            Throws<ArgumentException>(() => enabled["nyaa"].FeedUrl(query with { Category = "1_0&x=y" }));
            Throws<ArgumentException>(() => enabled["acgnx"].FeedUrl(query with { Category = "2" }));
            var torznab = enabled["torznab:p"].FeedUrl(query);
            Check(torznab == "http://prowlarr:9696/1/api?t=search&cat=5070%2C5000&q=%E8%91%AC%E9%80%81%20%26%2001" && !torznab.Contains("apikey"));
        });
        Test("Prefetch: Mikan always, other .torrent and Torznab links follow the switch", () =>
        {
            Check(TorrentDownloadService.Matches(new Uri("https://mikanani.me/Download/a.torrent"), settings));
            Check(TorrentDownloadService.Matches(new Uri("https://nyaa.si/download/1.torrent"), settings));
            Check(TorrentDownloadService.Matches(new Uri("http://prowlarr:9696/1/download?link=x"), settings));
            Check(!TorrentDownloadService.Matches(new Uri("https://example.com/file.zip"), settings));
            Check(!TorrentDownloadService.Matches(new Uri("magnet:?xt=urn:btih:" + Hash), settings));
            settings.ProxyTorrentDownloads = false;
            Check(TorrentDownloadService.Matches(new Uri("https://mikanani.me/Download/a.torrent"), settings));
            Check(!TorrentDownloadService.Matches(new Uri("https://nyaa.si/download/1.torrent"), settings));
            Check(!TorrentDownloadService.Matches(new Uri("http://prowlarr:9696/1/download?link=x"), settings));
            settings.ProxyTorrentDownloads = true;
        });
        Test("Torznab endpoint validation keeps keys out of URLs and normalizes categories", () =>
        {
            var endpoint = new TorznabEndpoint { Name = " Jackett ", Url = " http://jackett:9117/api/v2.0/indexers/all/results/torznab/api ", Categories = " 5070, 5000 " };
            Validation.Torznab(endpoint);
            Check(endpoint.Name == "Jackett" && endpoint.Categories == "5070,5000" && endpoint.Url.StartsWith("http://jackett"));
            Throws<ArgumentException>(() => Validation.Torznab(new TorznabEndpoint { Url = "http://p/api?apikey=x" }));
            Throws<ArgumentException>(() => Validation.Torznab(new TorznabEndpoint { Url = "http://p/api", Categories = "anime" }));
            Throws<ArgumentException>(() => Validation.Torznab(new TorznabEndpoint { Url = "ftp://p/api" }));
            Throws<ArgumentException>(() => Validation.AnimeSources(new AnimeSourcesSettings { Nyaa = new() { Enabled = true, BaseUrl = "javascript:alert(1)" } }));
        });
        Test("Old and null source settings deserialize to defaults", () =>
        {
            var old = JsonSerializer.Deserialize<WebSettings>("{\"proxyUrl\":\"\"}", StateStore.Json)!;
            Check(old.ProxyTorrentDownloads && old.AnimeSources.AcgRip.Enabled && !old.AnimeSources.Acgnx.Enabled && old.AnimeSources.Torznab.Count == 0);
            var nulls = JsonSerializer.Deserialize<WebSettings>("{\"animeSources\":{\"nyaa\":null,\"torznab\":null}}", StateStore.Json)!;
            Check(nulls.AnimeSources.Torznab.Count == 0 && !nulls.AnimeSources.Nyaa.Enabled && nulls.AnimeSources.Dmhy.Enabled);
            Check(JsonSerializer.Deserialize<WebSettings>("{\"animeSources\":null}", StateStore.Json)!.AnimeSources.Nyaa.Enabled);
        });
        return count;
    }
}
