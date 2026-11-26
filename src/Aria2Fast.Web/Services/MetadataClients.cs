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

    /// <summary>Bangumi subject types: 2 is anime (including anime films), 6 live action (TV dramas and films).</summary>
    public static int[] Types(LibraryCategory category) => category switch { LibraryCategory.Series => [6, 2], LibraryCategory.Movie => [2, 6], _ => [2] };

    public async Task<int?> Find(IEnumerable<string> terms, int season, CancellationToken ct, int[]? types = null)
    {
        foreach (var term in terms)
            if (Pick(await Search(term, ct, 10, types), term, season) is { } id) return id;
        return null;
    }

    public async Task<List<BangumiCandidate>> Search(string keyword, CancellationToken ct, int limit = 20, int[]? types = null)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync(Validation.HttpUrl(Root + "/v0/search/subjects?limit=" + limit), new { keyword, filter = new { type = types ?? [2] } }, ct);
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
        // 400 px wide: about twice the size posters are shown at. Larger covers shrink four-fold or more on screen and look jagged.
        var poster = new[] { "common", "medium", "large" }.Select(size => JsonText.String(images?[size])).FirstOrDefault(url => url.Length > 0) ?? "";
        var score = JsonText.Number(subject["rating"]?["score"]);
        var total = JsonText.Number(subject["total_episodes"]) ?? JsonText.Number(subject["eps"]);
        // The most used tags without years or numbers ("2023年10月", "TV") read as genres.
        var genres = (subject["tags"] as JsonArray ?? []).OfType<JsonObject>().OrderByDescending(tag => JsonText.Number(tag["count"]) ?? 0)
            .Select(tag => JsonText.String(tag["name"])).Where(name => name.Length is > 0 and <= 10 && !name.Any(char.IsDigit) && name is not ("TV" or "日本" or "日本动画" or "WEB"))
            .Take(6).ToList();
        var studio = new[] { "动画制作", "制作", "製作" }.Select(key => Infobox(subject["infobox"] as JsonArray, key)).FirstOrDefault(value => value.Length > 0) ?? "";
        return new("bangumi", (int)(JsonText.Number(subject["id"]) ?? throw new InvalidDataException("Bangumi 条目缺少 ID")),
            JsonText.String(subject["name"]), JsonText.String(subject["name_cn"]), JsonText.String(subject["summary"]), JsonText.String(subject["date"]),
            score > 0 ? score : null, total > 0 ? (int)total : null, JsonText.String(subject["platform"])) { PosterUrl = poster, Genres = genres, Studio = studio };
    }

    private static string Infobox(JsonArray? infobox, string key)
    {
        var entry = (infobox ?? []).OfType<JsonObject>().FirstOrDefault(e => JsonText.String(e["key"]) == key);
        return entry?["value"] is JsonArray values ? values.Select(v => JsonText.String(v?["v"])).FirstOrDefault(v => v.Length > 0) ?? "" : JsonText.String(entry?["value"]);
    }

    /// <summary>Main episodes (type 0) and specials (type 1); OP / ED / PV entries are left out.</summary>
    public static List<LibraryEpisodeMeta> ParseEpisodes(JsonArray data) => data.OfType<JsonObject>()
        .Select(x => new LibraryEpisodeMeta(JsonText.Number(x["ep"]) ?? JsonText.Number(x["sort"]) ?? 0, JsonText.Number(x["sort"]) ?? 0,
            JsonText.String(x["name"]), JsonText.String(x["name_cn"]), JsonText.String(x["airdate"]), (int)(JsonText.Number(x["type"]) ?? 0), JsonText.String(x["desc"])))
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

/// <summary>TMDB TV and movie lookups, used when a TMDB API key is configured (first for series and movies, after Bangumi for anime).</summary>
public sealed class TmdbClient(StateStore store, HttpGateway http)
{
    private const string Root = "https://api.themoviedb.org/3";
    private const string Images = "https://image.tmdb.org/t/p/w342";
    private const string Backdrops = "https://image.tmdb.org/t/p/w1280";

    private static List<string> Names(JsonNode? list) => (list as JsonArray ?? []).OfType<JsonObject>().Select(x => JsonText.String(x["name"])).Where(n => n.Length > 0).ToList();
    private static string Backdrop(JsonNode item) => JsonText.String(item["backdrop_path"]) is { Length: > 0 } path ? Backdrops + path : "";
    private string Key => store.Read().Settings.TmdbApiKey;
    public bool Configured => !string.IsNullOrWhiteSpace(Key);

    /// <summary>First TV result for any of the terms, with the season's episode titles.</summary>
    public async Task<LibraryMetadata?> Search(IEnumerable<string> terms, CancellationToken ct, int season = 0)
    {
        foreach (var term in terms)
            if ((await Get($"/search/tv?query={Uri.EscapeDataString(term)}", ct))?["results"] is JsonArray { Count: > 0 } results && JsonText.Number(results[0]?["id"]) is double id)
                return await Tv((int)id, ct, season);
        return null;
    }

    /// <summary>A show; for a season above zero also that season's episodes and poster (each season is its own work here).</summary>
    public async Task<LibraryMetadata> Tv(int id, CancellationToken ct, int season = 0)
    {
        if (!Configured) throw new ArgumentException("请先在设置中填写 TMDB API Key");
        var show = Parse(await Get($"/tv/{id}", ct) ?? throw new InvalidDataException("TMDB 未返回剧集信息"));
        if (season <= 0) return show with { UpdatedAt = DateTimeOffset.UtcNow };
        try { return WithSeason(show, await Get($"/tv/{id}/season/{season}", ct) ?? throw new InvalidDataException("TMDB 未返回季信息")) with { UpdatedAt = DateTimeOffset.UtcNow }; }
        catch (FileNotFoundException) { return show with { UpdatedAt = DateTimeOffset.UtcNow }; } // the show has no such season on TMDB
    }

    public async Task<LibraryMetadata?> SearchMovie(IEnumerable<string> terms, int? year, CancellationToken ct)
    {
        foreach (var term in terms)
        {
            // With a year first, then without: release years differ between countries.
            foreach (var withYear in year == null ? [false] : new[] { true, false })
                if ((await Get($"/search/movie?query={Uri.EscapeDataString(term)}" + (withYear ? $"&year={year}" : ""), ct))?["results"] is JsonArray { Count: > 0 } results && JsonText.Number(results[0]?["id"]) is double id)
                    return await Movie((int)id, ct);
        }
        return null;
    }

    /// <summary>Search results for the AI agent to choose from: movies or TV shows.</summary>
    public async Task<List<TmdbCandidate>> Candidates(string query, bool movie, int? year, CancellationToken ct)
    {
        var path = (movie ? "/search/movie" : "/search/tv") + "?query=" + Uri.EscapeDataString(query) + (year is { } y ? (movie ? "&year=" : "&first_air_date_year=") + y : "");
        return ((await Get(path, ct))?["results"] as JsonArray ?? []).OfType<JsonObject>().Where(r => JsonText.Number(r["id"]) is not null)
            .Select(r => new TmdbCandidate((int)JsonText.Number(r["id"])!, JsonText.String(r[movie ? "title" : "name"]), JsonText.String(r[movie ? "original_title" : "original_name"]),
                JsonText.String(r[movie ? "release_date" : "first_air_date"]), JsonText.String(r["overview"]))).ToList();
    }

    public async Task<LibraryMetadata> Movie(int id, CancellationToken ct)
    {
        if (!Configured) throw new ArgumentException("请先在设置中填写 TMDB API Key");
        return ParseMovie(await Get($"/movie/{id}", ct) ?? throw new InvalidDataException("TMDB 未返回电影信息")) with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>Simplified Chinese details; the key travels in the query string and never appears in errors.</summary>
    private async Task<JsonNode?> Get(string path, CancellationToken ct)
    {
        try { return JsonNode.Parse(await http.GetTextAsync(Root + path + (path.Contains('?') ? "&" : "?") + "language=zh-CN&api_key=" + Uri.EscapeDataString(Key.Trim()), ct)); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { throw new FileNotFoundException("TMDB 条目不存在"); }
        catch (HttpRequestException ex) { throw new HttpRequestException("TMDB 请求失败" + (ex.StatusCode is { } code ? "：HTTP " + (int)code : ""), null, ex.StatusCode); }
    }

    public static LibraryMetadata Parse(JsonNode tv)
    {
        var score = JsonText.Number(tv["vote_average"]);
        var episodes = JsonText.Number(tv["number_of_episodes"]);
        var poster = JsonText.String(tv["poster_path"]);
        return new("tmdb", (int)(JsonText.Number(tv["id"]) ?? throw new InvalidDataException("TMDB 剧集缺少 ID")),
            JsonText.String(tv["original_name"]), JsonText.String(tv["name"]), JsonText.String(tv["overview"]), JsonText.String(tv["first_air_date"]),
            score > 0 ? score : null, episodes > 0 ? (int)episodes : null, "TV")
        {
            PosterUrl = poster.Length > 0 ? Images + poster : "", FanartUrl = Backdrop(tv), Genres = Names(tv["genres"]),
            Studio = Names(tv["production_companies"]).FirstOrDefault() ?? Names(tv["networks"]).FirstOrDefault() ?? ""
        };
    }

    /// <summary>The season's episode titles, episode count, air date and poster (when it has its own) replace the show's.</summary>
    public static LibraryMetadata WithSeason(LibraryMetadata show, JsonNode season)
    {
        var episodes = (season["episodes"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => JsonText.Number(e["episode_number"]) is > 0)
            .Select(e => new LibraryEpisodeMeta(JsonText.Number(e["episode_number"])!.Value, JsonText.Number(e["episode_number"])!.Value, JsonText.String(e["name"]), JsonText.String(e["name"]), JsonText.String(e["air_date"]), 0, JsonText.String(e["overview"])))
            .ToList();
        var poster = JsonText.String(season["poster_path"]);
        var overview = JsonText.String(season["overview"]);
        var air = JsonText.String(season["air_date"]);
        return show with
        {
            Summary = overview.Length > 0 ? overview : show.Summary, AirDate = air.Length > 0 ? air : show.AirDate,
            TotalEpisodes = episodes.Count > 0 ? episodes.Count : show.TotalEpisodes, Episodes = episodes,
            PosterUrl = poster.Length > 0 ? Images + poster : show.PosterUrl
        };
    }

    public static LibraryMetadata ParseMovie(JsonNode movie)
    {
        var score = JsonText.Number(movie["vote_average"]);
        var poster = JsonText.String(movie["poster_path"]);
        return new("tmdb-movie", (int)(JsonText.Number(movie["id"]) ?? throw new InvalidDataException("TMDB 电影缺少 ID")),
            JsonText.String(movie["original_title"]), JsonText.String(movie["title"]), JsonText.String(movie["overview"]), JsonText.String(movie["release_date"]),
            score > 0 ? score : null, null, "电影")
        {
            PosterUrl = poster.Length > 0 ? Images + poster : "", FanartUrl = Backdrop(movie), Genres = Names(movie["genres"]), Studio = Names(movie["production_companies"]).FirstOrDefault() ?? ""
        };
    }
}

internal static class JsonText
{
    public static string String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
    public static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
