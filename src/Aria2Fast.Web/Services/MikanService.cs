using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using HtmlAgilityPack;
using Microsoft.Extensions.Caching.Memory;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>Selectors adapted from the desktop MikanManager; no WPF dependency.</summary>
public sealed class MikanService(StateStore store, HttpGateway http, IMemoryCache cache, AiService ai)
{
    private string Root => store.Read().Settings.MikanBaseUrl.TrimEnd('/');
    private string Absolute(string value) => Uri.TryCreate(new Uri(Root), value, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : "";
    private static string Text(HtmlNode? node) => HtmlEntity.DeEntitize(node?.InnerText ?? "").Trim();

    public async Task<List<AnimeCard>> List(int? year, string? season, bool refresh, CancellationToken ct)
    {
        if (year is < 2000 or > 2200 || (season != null && !new[] { "春", "夏", "秋", "冬" }.Contains(season))) throw new ArgumentException("年份或季度无效");
        var key = $"mikan:{Root}:{year}:{season}";
        if (!refresh && cache.TryGetValue<List<AnimeCard>>(key, out var existing)) return existing!;
        var url = Root + (year.HasValue && season != null ? $"/Home/BangumiCoverFlowByDayOfWeek?year={year}&seasonStr={Uri.EscapeDataString(season)}" : "/");
        var doc = new HtmlDocument();
        doc.LoadHtml(await http.GetTextAsync(url, ct));
        var cards = new List<AnimeCard>();
        foreach (var day in doc.DocumentNode.SelectNodes("//div[contains(@class,'sk-bangumi') or contains(@class,'m-home-week-item')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var dayName = Text(day.SelectSingleNode(".//div[starts-with(@id,'data-row-')] | .//div[@class='title']/span"));
            foreach (var item in day.SelectNodes(".//li | .//div[contains(@class,'m-week-square')]") ?? Enumerable.Empty<HtmlNode>())
            {
                var link = item.SelectSingleNode(".//a[contains(@class,'an-text')]") ?? item.SelectSingleNode(".//a[contains(@href,'/Home/Bangumi/')]");
                var href = link?.GetAttributeValue("href", "") ?? "";
                var id = Regex.Match(href, @"/Home/Bangumi/(\d+)").Groups[1].Value;
                var name = Text(link);
                if (name.Length == 0) name = HtmlEntity.DeEntitize(link?.GetAttributeValue("title", "") ?? "");
                var image = item.SelectSingleNode(".//*[@data-src]")?.GetAttributeValue("data-src", "") ?? item.SelectSingleNode(".//img")?.GetAttributeValue("src", "") ?? "";
                if (id.Length > 0 && name.Length > 0) cards.Add(new(id, name, Absolute(href), image.Length > 0 ? Absolute(image) : "", dayName));
            }
        }
        if (cards.Count == 0) throw new InvalidDataException("Mikan 未返回番剧。可能是源站不可用或页面结构改变，请检查源站地址/代理。");
        var result = cards.DistinctBy(x => x.Id).ToList();
        cache.Set(key, result, TimeSpan.FromHours(2));
        return result;
    }

    public async Task<AnimeDetail> Detail(string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, @"^\d{1,12}$")) throw new ArgumentException("番剧 ID 无效");
        var key = $"detail:{Root}:{id}";
        if (cache.TryGetValue<AnimeDetail>(key, out var cached)) return cached!;
        var doc = new HtmlDocument();
        doc.LoadHtml(await http.GetTextAsync(Root + "/Home/Bangumi/" + id, ct));
        var name = Text(doc.DocumentNode.SelectSingleNode("//p[contains(@class,'bangumi-title')] | //div[contains(@class,'bangumi-title')] | //h1"));
        if (name.Length == 0) name = Text(doc.DocumentNode.SelectSingleNode("//title")).Split('-')[0].Trim();
        var summary = Text(doc.DocumentNode.SelectSingleNode("//p[contains(@class,'header2-desc')]"));
        var groups = new List<AnimeGroup>();
        foreach (var group in doc.DocumentNode.SelectNodes("//div[contains(@class,'subgroup-text')]") ?? Enumerable.Empty<HtmlNode>())
        {
            var link = group.SelectSingleNode(".//a[contains(@class,'mikan-rss')]");
            var title = Text(group.SelectSingleNode(".//a[not(contains(@class,'mikan-rss'))]"));
            if (link != null) groups.Add(new(title.Length == 0 ? "全部字幕组" : title, Absolute(link.GetAttributeValue("href", ""))));
        }
        if (groups.Count == 0) groups.Add(new("全部字幕组", Root + "/RSS/Bangumi?bangumiId=" + id));
        TmdbInfo? tmdb = null;
        if (!string.IsNullOrWhiteSpace(store.Read().Settings.TmdbApiKey)) tmdb = await Tmdb(name, ct);
        var settings = store.Read().Settings;
        if (settings.TranslateSummary && !string.IsNullOrWhiteSpace(summary)) summary = await ai.Send(summary, "将动漫简介翻译为自然简体中文，保留原意，不补充事实。只输出译文。", ct);
        AiReview? review = null;
        if (settings.AutoReview) review = await ai.Review(id, name, summary, ct);
        var detail = new AnimeDetail(id, name, summary, groups, tmdb, review);
        cache.Set(key, detail, TimeSpan.FromMinutes(30));
        return detail;
    }

    private async Task<TmdbInfo?> Tmdb(string title, CancellationToken ct)
    {
        var key = store.Read().Settings.TmdbApiKey;
        var url = "https://api.themoviedb.org/3/search/tv?language=zh-CN&query=" + Uri.EscapeDataString(title) + "&api_key=" + Uri.EscapeDataString(key);
        var json = JsonNode.Parse(await http.GetTextAsync(url, ct));
        var result = json?["results"]?.AsArray().FirstOrDefault();
        if (result == null) return null;
        return new(result["id"]!.GetValue<int>(), result["name"]?.ToString() ?? title, result["overview"]?.ToString() ?? "", result["vote_average"]?.GetValue<double>() ?? 0,
            result["vote_count"]?.GetValue<int>() ?? 0, result["popularity"]?.GetValue<double>() ?? 0,
            result["poster_path"] is { } poster ? "https://image.tmdb.org/t/p/w500" + poster : "", result["first_air_date"]?.ToString() ?? "");
    }
}
