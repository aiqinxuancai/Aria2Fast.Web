using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class FeedService(HttpGateway http)
{
    public async Task<FeedPreview> Fetch(string url, CancellationToken ct = default) => Parse(await http.GetTextAsync(url, ct), url);

    public static FeedPreview Parse(string xml, string baseUrl)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 });
        var doc = XDocument.Load(reader);
        var entries = doc.Descendants().Where(n => n.Name.LocalName is "item" or "entry");
        var result = new List<FeedItem>();
        foreach (var entry in entries.Take(3000))
        {
            string Text(string name) => entry.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
            var enclosure = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "enclosure" || (x.Name.LocalName == "link" && (string?)x.Attribute("rel") == "enclosure"));
            var link = enclosure?.Attribute("url")?.Value ?? enclosure?.Attribute("href")?.Value;
            link ??= entry.Elements().FirstOrDefault(x => x.Name.LocalName == "link")?.Attribute("href")?.Value;
            link ??= Text("link");
            if (string.IsNullOrWhiteSpace(link)) continue;
            if (!Uri.TryCreate(new Uri(baseUrl), link, out var uri) || uri.Scheme is not ("http" or "https" or "magnet" or "ftp")) continue;
            var key = Text("guid");
            if (key.Length == 0) key = Text("id");
            if (key.Length == 0) key = uri.AbsoluteUri;
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var date = Text("pubDate");
            if (date.Length == 0) date = Text("updated");
            result.Add(new(key, Text("title"), uri.AbsoluteUri, DateTimeOffset.TryParse(date, out var time) ? time : null, enclosure?.Attribute("length")?.Value ?? ""));
        }
        var title = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "title")?.Value ?? "RSS";
        return new(title, result.DistinctBy(x => x.Key).ToList());
    }
}
