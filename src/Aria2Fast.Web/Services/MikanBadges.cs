using System.Globalization;
using Aria2Fast.Utils;
using HtmlAgilityPack;

namespace Aria2Fast.Web.Services;

public sealed record AnimeBadges(int GroupCount, int UpdatedGroups, int LatestEpisode)
{
    public string Hot => GroupCount is >= 7 and <= 100 ? "purple" : GroupCount is >= 5 and <= 6 ? "pink" : "";
}

public static class MikanBadges
{
    // Same inputs as desktop: each group's first release, not calendar resource counts.
    public static AnimeBadges Parse(string html, DateTimeOffset now)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        int groups = 0, updated = 0, episode = 0;
        foreach (var group in doc.DocumentNode.SelectNodes("//div[contains(@class,'subgroup-text')]") ?? Enumerable.Empty<HtmlNode>())
        {
            if (group.SelectSingleNode(".//a[contains(@class,'mikan-rss')]") == null) continue;
            groups++;
            var table = group.SelectSingleNode("following-sibling::div[contains(@class,'episode-table')][1]");
            var row = table?.SelectSingleNode(".//tr[not(ancestor::thead)][.//td[2]/a[contains(@class,'magnet-link-wrap')]][1]");
            var title = HtmlEntity.DeEntitize(row?.SelectSingleNode(".//td[2]/a[contains(@class,'magnet-link-wrap')]")?.InnerText ?? "");
            if (int.TryParse(MatchUtils.ExtractEpisodeNumber(title), out var number)) episode = Math.Max(episode, number);
            var date = HtmlEntity.DeEntitize(row?.SelectSingleNode(".//td[4]")?.InnerText ?? "").Trim();
            if (DateTime.TryParseExact(date, "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                var age = now - new DateTimeOffset(time, TimeSpan.FromHours(8));
                if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(1)) updated++;
            }
        }
        return new(groups, updated, episode);
    }
}
