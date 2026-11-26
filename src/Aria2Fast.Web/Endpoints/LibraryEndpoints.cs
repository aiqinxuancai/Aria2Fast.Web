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
    }

    public sealed record MatchRequest(int? BgmId, int? TmdbId);
}
