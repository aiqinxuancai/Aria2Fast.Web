using System.Text.RegularExpressions;
using Aria2Fast.Web.Models;
using HtmlAgilityPack;

namespace Aria2Fast.Web.Services;

public static class MikanCalendarParser
{
    private static readonly string[] Days = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"];
    private static string Text(HtmlNode? node) => HtmlEntity.DeEntitize(node?.InnerText ?? "").Trim();
    private static string Absolute(string root, string value) =>
        value.Length > 0 && Uri.TryCreate(new Uri(root), HtmlEntity.DeEntitize(value), out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : "";

    public static List<AnimeCard> Parse(string html, string root)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var cards = new List<AnimeCard>();
        foreach (var day in doc.DocumentNode.SelectNodes("//div[contains(@class,'sk-bangumi') or contains(@class,'m-home-week-item')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var title = day.SelectSingleNode(".//div[starts-with(@id,'data-row-')] | .//div[@class='title']/span");
            var dayName = Text(title).Replace("星期天", "星期日");
            if (!Days.Contains(dayName))
            {
                var number = day.GetAttributeValue("data-dayofweek", title?.GetAttributeValue("id", "").Replace("data-row-", "") ?? "");
                dayName = int.TryParse(number, out var index) && index is >= 0 and <= 6 ? Days[(index + 6) % 7] : dayName;
            }
            if (dayName.Length == 0) dayName = "其他";
            foreach (var item in day.SelectNodes(".//li | .//div[contains(@class,'m-week-square')]") ?? Enumerable.Empty<HtmlNode>())
            {
                var link = item.SelectSingleNode(".//a[contains(@class,'an-text')]") ?? item.SelectSingleNode(".//a[contains(@href,'/Home/Bangumi/')]");
                var href = link?.GetAttributeValue("href", "") ?? "";
                var id = Regex.Match(href, @"/Home/Bangumi/(\d+)").Groups[1].Value;
                if (id.Length == 0) id = item.SelectSingleNode(".//*[@data-bangumiid]")?.GetAttributeValue("data-bangumiid", "") ?? "";
                var nameNode = link ?? item.SelectSingleNode(".//*[contains(@class,'date-text') and @title]");
                var name = HtmlEntity.DeEntitize(nameNode?.GetAttributeValue("title", "") ?? "").Trim();
                if (name.Length == 0) name = Text(nameNode);
                if (!Regex.IsMatch(id, @"^\d{1,12}$") || name.Length == 0) continue;
                var image = item.SelectSingleNode(".//*[@data-src]")?.GetAttributeValue("data-src", "") ?? item.SelectSingleNode(".//img")?.GetAttributeValue("src", "") ?? "";
                var hasReleases = link != null && item.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' greyout ')]") == null;
                cards.Add(new(id, name, Absolute(root, "/Home/Bangumi/" + id), Absolute(root, image), dayName, hasReleases));
            }
        }
        return cards.GroupBy(c => c.Id).Select(g => g.OrderByDescending(c => c.HasReleases).First())
            .OrderBy(c => DayOrder(c.Day)).ToList();
    }

    private static int DayOrder(string day)
    {
        var index = Array.IndexOf(Days, day);
        return index < 0 ? 7 : index;
    }
}
