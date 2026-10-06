using Aria2Fast.Web.Services;
namespace Aria2Fast.Web.Endpoints;

public static class AnimeEndpoints
{
    public static void MapAnime(this RouteGroupBuilder api)
    {
        api.MapGet("/anime", (int? year, string? season, bool? refresh, MikanService mikan, CancellationToken ct) => mikan.List(year, season, refresh ?? false, ct));
        api.MapGet("/anime/{id}", (string id, bool? generateAi, MikanService mikan, CancellationToken ct) => mikan.Detail(id, ct, generateAi ?? true));
        api.MapGet("/anime/{id}/badges", (string id, MikanService mikan, CancellationToken ct) => mikan.Badges(id, ct));
        api.MapPost("/anime/{id}/review", async (string id, bool? refresh, MikanService mikan, AiService ai, CancellationToken ct) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{1,12}$")) throw new ArgumentException("番剧 ID 无效");
            if (refresh != true && ai.CachedReview(id) is { } cached) return cached;
            var detail = await mikan.Detail(id, ct, generateAi: false);
            return await ai.Review(id, detail.Name, detail.OriginalSummary + "\n" + detail.Tmdb?.Overview, ct, refresh ?? false);
        });
        api.MapPost("/anime/translate", async (TranslateRequest input, AiService ai, CancellationToken ct) => new { text = await ai.Translate(input.Text, ct) });
    }
    public sealed record TranslateRequest(string Text);
}
