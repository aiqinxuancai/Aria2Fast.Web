using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed record BangumiCandidate(int Id, string Name, string NameCn, string Date, List<string> Aliases, string Image = "", int? Episodes = null);

/// <summary>Bangumi (bgm.tv) public API, https://bangumi.github.io/api/. Anime subjects are type 2; each season is its own subject.</summary>
public sealed class BangumiClient(StateStore store, HttpGateway http)
{
    // Bangumi asks API clients to identify themselves.
    private const string UserAgent = "aiqinxuancai/Aria2Fast.Web (https://github.com/aiqinxuancai/Aria2Fast.Web)";
    private string Root => store.Read().Settings.Library.BangumiApiUrl.TrimEnd('/');

    public async Task<int?> Find(IEnumerable<string> terms, int season, CancellationToken ct)
    {
        foreach (var term in terms)
            if (Pick(await Search(term, ct, 10), term, season) is { } id) return id;
        return null;
    }

    public async Task<List<BangumiCandidate>> Search(string keyword, CancellationToken ct, int limit = 20)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync(Validation.HttpUrl(Root + "/v0/search/subjects?limit=" + limit), new { keyword, filter = new { type = new[] { 2 } } }, ct);
        response.EnsureSuccessStatusCode();
        return ParseSearch(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    public async Task<LibraryMetadata> Subject(int id, CancellationToken ct)
    {
        var subject = await Get($"/v0/subjects/{id}", ct) ?? throw new InvalidDataException("Bangumi 未返回条目信息");
        var episodes = new List<LibraryEpisodeMeta>();
        for (var offset = 0; offset < 1000; offset += 100)
        {
            var page = (await Get($"/v0/episodes?subject_id={id}&limit=100&offset={offset}", ct))?["data"] as JsonArray ?? [];
            episodes.AddRange(ParseEpisodes(page));
            if (page.Count < 100) break;
        }
        return ParseSubject(subject) with { Episodes = episodes, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public static LibraryMetadata ParseSubject(JsonNode subject)
    {
        var images = subject["images"] as JsonObject;
        var poster = new[] { "medium", "large", "common" }.Select(size => JsonText.String(images?[size])).FirstOrDefault(url => url.Length > 0) ?? "";
        var score = JsonText.Number(subject["rating"]?["score"]);
        var total = JsonText.Number(subject["total_episodes"]) ?? JsonText.Number(subject["eps"]);
        return new("bangumi", (int)(JsonText.Number(subject["id"]) ?? throw new InvalidDataException("Bangumi 条目缺少 ID")),
            JsonText.String(subject["name"]), JsonText.String(subject["name_cn"]), JsonText.String(subject["summary"]), JsonText.String(subject["date"]),
            score > 0 ? score : null, total > 0 ? (int)total : null, JsonText.String(subject["platform"])) { PosterUrl = poster };
    }

    /// <summary>Main episodes (type 0) and specials (type 1); OP / ED / PV entries are left out.</summary>
    public static List<LibraryEpisodeMeta> ParseEpisodes(JsonArray data) => data.OfType<JsonObject>()
        .Select(x => new LibraryEpisodeMeta(JsonText.Number(x["ep"]) ?? JsonText.Number(x["sort"]) ?? 0, JsonText.Number(x["sort"]) ?? 0,
            JsonText.String(x["name"]), JsonText.String(x["name_cn"]), JsonText.String(x["airdate"]), (int)(JsonText.Number(x["type"]) ?? 0)))
        .Where(x => x.Type is 0 or 1).ToList();

    public static List<BangumiCandidate> ParseSearch(JsonNode? json) => (json?["data"] as JsonArray ?? []).OfType<JsonObject>()
        .Where(x => JsonText.Number(x["id"]) is not null)
        .Select(x => new BangumiCandidate((int)JsonText.Number(x["id"])!, JsonText.String(x["name"]), JsonText.String(x["name_cn"]), JsonText.String(x["date"]), Aliases(x["infobox"] as JsonArray),
            new[] { "common", "medium", "large", "small" }.Select(size => JsonText.String(x["images"]?[size])).FirstOrDefault(url => url.Length > 0) ?? "",
            JsonText.Number(x["eps"]) is double eps && eps > 0 ? (int)eps : null))
        .ToList();

    /// <summary>
    /// Bangumi search is fuzzy and returns unrelated works for unknown terms, so a result must have a name
    /// (or alias) that contains the term or is contained by it. Season markers decide between sequels and
    /// outweigh an exact name, since a sequel's own names often lack the spelling the release used.
    /// </summary>
    public static int? Pick(IReadOnlyList<BangumiCandidate> candidates, string term, int season)
    {
        var wanted = ReleaseNames.Key(term);
        if (wanted.Length < 2) return null;
        var wantedSeason = Math.Max(season, 1);
        var scored = candidates.Select((candidate, index) =>
        {
            var names = new[] { candidate.Name, candidate.NameCn }.Concat(candidate.Aliases).Where(n => n.Length > 0).ToList();
            var keys = names.Select(n => ReleaseNames.Key(ReleaseNames.Title(n))).Where(k => k.Length >= 2).ToHashSet();
            var found = names.Select(ReleaseNames.Season).FirstOrDefault(s => s != null) ?? 1;
            return (candidate.Id, Keys: keys, Season: found, Index: index, Related: keys.Any(k => k.Contains(wanted) || wanted.Contains(k)), Exact: keys.Contains(wanted));
        }).ToList();
        // e.g. traditional "葬送的芙莉蓮" only appears among the first season's aliases; the sequel shares that season's base names.
        var related = scored.Where(x => x.Related).SelectMany(x => x.Keys).ToHashSet();
        return scored.Where(x => x.Related || (wantedSeason > 1 && x.Season == wantedSeason && x.Keys.Overlaps(related)))
            .OrderByDescending(x => (x.Season == wantedSeason ? 5 : 0) + (x.Exact ? 4 : 0)).ThenBy(x => x.Index)
            .Select(x => (int?)x.Id).FirstOrDefault();
    }

    private static List<string> Aliases(JsonArray? infobox)
    {
        var result = new List<string>();
        foreach (var entry in (infobox ?? []).OfType<JsonObject>())
        {
            if (JsonText.String(entry["key"]) is not ("中文名" or "别名")) continue;
            if (entry["value"] is JsonArray values) result.AddRange(values.Select(v => JsonText.String(v?["v"])).Where(v => v.Length > 0));
            else if (JsonText.String(entry["value"]) is { Length: > 0 } value) result.Add(value);
        }
        return result;
    }

    private HttpClient Client()
    {
        var client = http.Create();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private async Task<JsonNode?> Get(string path, CancellationToken ct)
    {
        using var client = Client();
        using var response = await client.GetAsync(Validation.HttpUrl(Root + path), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new FileNotFoundException("Bangumi 条目不存在");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>TMDB TV search, used when Bangumi has no match and a TMDB API key is configured.</summary>
public sealed class TmdbClient(StateStore store, HttpGateway http)
{
    private const string Root = "https://api.themoviedb.org/3";
    private string Key => store.Read().Settings.TmdbApiKey;
    public bool Configured => !string.IsNullOrWhiteSpace(Key);

    public async Task<LibraryMetadata?> Search(IEnumerable<string> terms, CancellationToken ct)
    {
        foreach (var term in terms)
        {
            var json = JsonNode.Parse(await http.GetTextAsync($"{Root}/search/tv?language=zh-CN&query={Uri.EscapeDataString(term)}&api_key={Uri.EscapeDataString(Key)}", ct));
            if ((json?["results"] as JsonArray)?.FirstOrDefault() is JsonObject result) return Parse(result);
        }
        return null;
    }

    public async Task<LibraryMetadata> Tv(int id, CancellationToken ct)
    {
        if (!Configured) throw new ArgumentException("请先在设置中填写 TMDB API Key");
        return Parse(JsonNode.Parse(await http.GetTextAsync($"{Root}/tv/{id}?language=zh-CN&api_key={Uri.EscapeDataString(Key)}", ct)) ?? throw new InvalidDataException("TMDB 未返回剧集信息"));
    }

    public static LibraryMetadata Parse(JsonNode tv)
    {
        var score = JsonText.Number(tv["vote_average"]);
        var episodes = JsonText.Number(tv["number_of_episodes"]);
        var poster = JsonText.String(tv["poster_path"]);
        return new("tmdb", (int)(JsonText.Number(tv["id"]) ?? throw new InvalidDataException("TMDB 剧集缺少 ID")),
            JsonText.String(tv["original_name"]), JsonText.String(tv["name"]), JsonText.String(tv["overview"]), JsonText.String(tv["first_air_date"]),
            score > 0 ? score : null, episodes > 0 ? (int)episodes : null, "TV") { PosterUrl = poster.Length > 0 ? "https://image.tmdb.org/t/p/w500" + poster : "" };
    }
}

internal static class JsonText
{
    public static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
    public static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
