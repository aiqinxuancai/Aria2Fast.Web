using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services.Sources;

namespace Aria2Fast.Web.Services;

public sealed partial class FeedService(HttpGateway http, StateStore store)
{
    // Torznab subscription URLs are stored without the API key; it is added only for the outgoing request.
    public async Task<FeedPreview> Fetch(string url, CancellationToken ct = default) =>
        Parse(await http.GetTextAsync(TorznabUrls.WithApiKey(url, store.Read().Settings), ct), url);

    public static FeedPreview Parse(string xml, string baseUrl)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var doc = XDocument.Load(reader);
        // Torznab / Newznab indexers report a bad API key or disabled indexer as an <error> document with HTTP 200.
        if (doc.Root?.Name.LocalName == "error")
            throw new InvalidDataException("索引器返回错误：" + ((string?)doc.Root.Attribute("description") ?? (string?)doc.Root.Attribute("code") ?? "未知错误"));
        var root = new Uri(baseUrl);
        var result = new List<FeedItem>();
        foreach (var entry in doc.Descendants().Where(n => n.Name.LocalName is "item" or "entry").Take(3000))
            if (ParseEntry(entry, root) is { } item) result.Add(item);
        var title = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value ?? "RSS";
        return new(title, result.DistinctBy(x => x.Key).ToList());
    }

    private static FeedItem? ParseEntry(XElement entry, Uri root)
    {
        // Match by local name so Atom, nyaa:* and plain RSS elements are read alike.
        XElement? Child(string name) => entry.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        string Text(string name) => Child(name)?.Value.Trim() ?? "";
        Uri? Resolve(string? value, params string[] schemes) =>
            !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(root, value.Trim(), out var uri) && schemes.Contains(uri.Scheme) ? uri : null;
        string[] downloadSchemes = ["http", "https", "magnet", "ftp"];
        var attrs = entry.Elements().Where(x => x.Name.LocalName == "attr" && x.Attribute("name") != null)
            .GroupBy(x => x.Attribute("name")!.Value.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Attribute("value")?.Value.Trim() ?? "");
        string Attr(string name) => attrs.TryGetValue(name, out var value) ? value : "";

        var title = Text("title");
        var enclosure = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "enclosure" || (x.Name.LocalName == "link" && (string?)x.Attribute("rel") == "enclosure"));
        var enclosureUrl = Resolve(enclosure?.Attribute("url")?.Value ?? enclosure?.Attribute("href")?.Value, downloadSchemes);
        var linkElement = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "link" && (string?)x.Attribute("rel") != "enclosure");
        var linkUrl = Resolve(linkElement?.Attribute("href")?.Value ?? linkElement?.Value, downloadSchemes);
        var linkIsDownload = linkUrl != null && (linkUrl.Scheme == "magnet" || linkUrl.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));
        var hash = InfoHash(Text("infoHash")) ?? InfoHash(Attr("infohash"));
        var showPageHash = linkUrl != null && ShowPage().Match(linkUrl.AbsolutePath) is { Success: true } show ? show.Groups[1].Value.ToLowerInvariant() : null;

        // Priority: enclosure > Torznab magneturl > .torrent / magnet <link> > nyaa / Torznab info hash > Kisssub show page hash > plain <link>.
        var url = enclosureUrl
            ?? Resolve(Attr("magneturl"), "magnet")
            ?? (linkIsDownload ? linkUrl : null)
            ?? Magnet(hash, title)
            ?? Magnet(showPageHash, title)
            ?? linkUrl;
        if (url == null) return null;

        // Keys fall back to the URL picked by earlier versions so existing subscription history keeps deduplicating.
        var key = Text("guid");
        if (key.Length == 0) key = Text("id");
        if (key.Length == 0) key = (enclosureUrl ?? linkUrl ?? url).AbsoluteUri;
        key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

        var date = Text("pubDate");
        if (date.Length == 0) date = Text("updated");
        if (date.Length == 0) date = Text("published");
        var size = ParseSize(enclosure?.Attribute("length")?.Value) ?? ParseSize(Text("size")) ?? ParseSize(Attr("size")) ?? "";
        var seeders = Number(Text("seeders")) ?? Number(Attr("seeders"));
        var leechers = Number(Text("leechers")) ?? Number(Attr("leechers"));
        if (leechers == null && Number(Attr("peers")) is { } peers && seeders is { } known && peers >= known) leechers = peers - known;

        var guid = Child("guid");
        var page = new[]
            {
                Resolve(Text("comments"), "http", "https"),
                linkIsDownload ? null : Resolve(linkUrl?.AbsoluteUri, "http", "https"),
                (string?)guid?.Attribute("isPermaLink") == "false" ? null : Resolve(guid?.Value, "http", "https"),
            }
            .FirstOrDefault(x => x != null && x != url && !x.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));
        hash ??= showPageHash ?? (url.Scheme == "magnet" ? InfoHash(MagnetHash().Match(url.OriginalString).Groups[1].Value) : null);
        var category = Text("category");
        return new(key, title, url.AbsoluteUri, DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, out var time) ? time : null, size,
            hash, seeders, leechers, page?.AbsoluteUri, category.Length > 0 ? category : null);
    }

    private static Uri? Magnet(string? hash, string title) =>
        hash == null ? null : new Uri("magnet:?xt=urn:btih:" + hash + (title.Length > 0 ? "&dn=" + Uri.EscapeDataString(title) : ""));

    private static string? InfoHash(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (HexHash().IsMatch(value)) return value.ToLowerInvariant();
        return Base32Hash().IsMatch(value) ? value.ToUpperInvariant() : null;
    }

    private static int? Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;

    /// <summary>Converts "1.4 GiB", "320.5 MiB", "734003200" or "1,234" to a byte count string; returns null when unknown.</summary>
    public static string? ParseSize(string? value)
    {
        var match = SizeText().Match(value?.Trim() ?? "");
        if (!match.Success) return null;
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var digits = match.Groups["number"].Value;
        double bytes;
        if (unit.Length == 0 || unit.StartsWith("BYTE"))
        {
            if (!long.TryParse(digits.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var whole)) return null;
            bytes = whole;
        }
        else
        {
            var power = "BKMGTP".IndexOf(unit[0]);
            if (power < 0 || !double.TryParse(digits.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return null;
            bytes = amount * Math.Pow(1024, power);
        }
        // Several sites publish placeholder lengths of 0 or 1 byte.
        return bytes is > 1 and < long.MaxValue ? ((long)Math.Round(bytes)).ToString(CultureInfo.InvariantCulture) : null;
    }

    [GeneratedRegex(@"^(?<number>\d[\d.,]*)\s*(?<unit>[KMGTP]?I?B|BYTES?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();
    [GeneratedRegex(@"show-([0-9a-fA-F]{40})\.html$")]
    private static partial Regex ShowPage();
    [GeneratedRegex(@"[?&]xt=urn:btih:([0-9a-zA-Z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetHash();
    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex HexHash();
    [GeneratedRegex("^[A-Za-z2-7]{32}$")]
    private static partial Regex Base32Hash();
}
