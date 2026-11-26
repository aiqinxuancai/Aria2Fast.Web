using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;

namespace Aria2Fast.Web.Endpoints;

public static class LibraryEndpoints
{
    public static void MapLibrary(this RouteGroupBuilder api)
    {
        api.MapGet("/library", (LibraryService library) => library.Overview());
        api.MapGet("/library/status", (LibraryService library) => library.Status());
        api.MapPost("/library/scan", async (LibraryService library, CancellationToken ct) =>
        {
            var status = await library.Scan(ct);
            library.RequestScan(); // Metadata for new works is fetched in the background.
            return status;
        });
        api.MapGet("/library/search", (string? q, LibraryService library, CancellationToken ct) => library.Search(q, ct));
        api.MapGet("/library/{id}", (string id, LibraryService library) => library.Detail(id));
        api.MapGet("/library/{id}/poster", (string id, HttpContext context, LibraryService library) =>
        {
            var path = library.PosterPath(id);
            // The URL carries a version, so the browser may keep posters instead of the API-wide no-store.
            context.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, LibraryService.ContentType(path), lastModified: File.GetLastWriteTimeUtc(path));
        });
        api.MapPost("/library/{id}/match", (string id, MatchRequest input, LibraryService library, CancellationToken ct) => library.Match(id, input.BgmId, input.TmdbId, ct));
        api.MapPost("/library/{id}/refresh", (string id, LibraryService library, CancellationToken ct) => library.Refresh(id, ct));
        api.MapPost("/library/{id}/watched", (string id, WatchedRequest input, PlaybackService playback, LibraryService library) =>
        {
            playback.MarkSeries(id, input.Watched);
            return library.Detail(id);
        });
        api.MapPost("/library/{id}/links", (string id, PlaybackService playback) => playback.SeriesLinks(id));
        api.MapPost("/library/{id}/recognize", (string id, LibraryService library, CancellationToken ct) => library.Recognize(id, ct));
        api.MapDelete("/library/{id}/numbers", (string id, LibraryService library, CancellationToken ct) => library.ResetNumbers(id, null, ct));

        api.MapGet("/library/episodes/{id}", (string id, PlaybackService playback) => playback.Player(id));
        api.MapGet("/library/episodes/{id}/stream", (string id, PlaybackService playback) =>
        {
            var (_, file) = playback.Resolve(id);
            return Results.File(file, PlaybackService.ContentType(file), lastModified: File.GetLastWriteTimeUtc(file), enableRangeProcessing: true);
        });
        api.MapGet("/library/episodes/{id}/subtitles/{index:int}", (string id, int index, PlaybackService playback) =>
            Results.Text(playback.Subtitle(id, index), "text/vtt; charset=utf-8"));
        api.MapPost("/library/episodes/{id}/progress", (string id, ProgressRequest input, PlaybackService playback) => playback.Save(id, input.Position, input.Duration, input.Watched));
        api.MapPost("/library/episodes/{id}/watched", (string id, WatchedRequest input, PlaybackService playback) => { playback.Mark(id, input.Watched); return Results.Ok(); });
        api.MapPost("/library/episodes/{id}/link", (string id, PlaybackService playback) => playback.Link(id));
        api.MapPost("/library/episodes/{id}/number", (string id, NumberRequest input, LibraryService library) => library.SetNumber(id, input.Number, input.Kind));
        api.MapDelete("/library/episodes/{id}/number", (string id, LibraryService library, CancellationToken ct) => library.ResetNumbers(null, id, ct));
    }

    /// <summary>
    /// Signed links for external players, outside the cookie-protected API. The token selects the video; the
    /// file name after it is only for display. Invalid, expired and revoked links all answer 404.
    /// </summary>
    public static void MapMedia(this WebApplication app)
    {
        app.MapMethods("/media/{token}/{name}", ["GET", "HEAD"], (string token, PlaybackService playback) =>
            playback.Open(token) is { } file
                ? Results.File(file, PlaybackService.ContentType(file), lastModified: File.GetLastWriteTimeUtc(file), enableRangeProcessing: true)
                : Results.NotFound());
    }

    public sealed record MatchRequest(int? BgmId, int? TmdbId);
    public sealed record WatchedRequest(bool Watched);
    public sealed record ProgressRequest(double Position, double Duration, bool? Watched);
    public sealed record NumberRequest(double? Number, LibraryEpisodeKind Kind = LibraryEpisodeKind.Episode);
}
