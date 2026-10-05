using Aria2Fast.Web.Services;
namespace Aria2Fast.Web.Endpoints;

public static class AnimeEndpoints
{
    public static void MapAnime(this RouteGroupBuilder api)
    {
        api.MapGet("/anime", (int? year, string? season, bool? refresh, MikanService mikan, CancellationToken ct) => mikan.List(year, season, refresh ?? false, ct));
        api.MapGet("/anime/{id}", (string id, MikanService mikan, CancellationToken ct) => mikan.Detail(id, ct));
        api.MapGet("/anime/{id}/badges", (string id, MikanService mikan, CancellationToken ct) => mikan.Badges(id, ct));
        api.MapPost("/anime/{id}/review", async (string id, MikanService mikan, AiService ai, CancellationToken ct) =>
        {
            var detail = await mikan.Detail(id, ct);
            return await ai.Review(id, detail.Name, detail.Summary + "\n" + detail.Tmdb?.Overview, ct);
        });
        api.MapPost("/anime/translate", async (TranslateRequest input, AiService ai, CancellationToken ct) => new { text = await ai.Send(input.Text, "翻译成自然简体中文，仅输出译文。", ct) });
    }
    public sealed record TranslateRequest(string Text);
}
