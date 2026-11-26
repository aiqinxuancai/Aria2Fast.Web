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
        api.MapGet("/library/search", (string? q, LibraryCategory? category, LibraryService library, CancellationToken ct) => library.Search(q, category ?? LibraryCategory.Anime, ct));
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
        api.MapPost("/library/{id}/ai-scrape", (string id, LibraryService library) => library.StartAiScrape(id));
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

        // Optional ffmpeg: media details, embedded subtitles and HLS remux / transcode sessions.
        api.MapGet("/library/ffmpeg", async (bool? refresh, FfmpegService ffmpeg, Infrastructure.StateStore store, CancellationToken ct) =>
        {
            var status = await ffmpeg.Status(ct, refresh == true);
            return new { status.Available, status.Ffmpeg, status.Ffprobe, status.Version, status.Encoders, status.Error, enabled = store.Read().Settings.Library.Transcoding };
        });
        api.MapPost("/library/ffmpeg/test", (EncoderRequest input, FfmpegService ffmpeg, CancellationToken ct) => ffmpeg.Test(input.Encoder, ct));
        api.MapGet("/library/episodes/{id}/media", async (string id, MediaAnalyzer media, FfmpegService ffmpeg, Infrastructure.StateStore store, CancellationToken ct) =>
        {
            var settings = store.Read().Settings.Library;
            if (!settings.Transcoding) return Results.Ok(new { available = false, reason = "未启用转码" });
            var status = await ffmpeg.Status(ct);
            if (!status.Available) return Results.Ok(new { available = false, reason = status.Error });
            return Results.Ok(new { available = true, info = await media.Info(id, ct), settings.MaxHeight, encoder = settings.VideoEncoder });
        });
        api.MapGet("/library/episodes/{id}/subtitles/embedded/{index:int}", async (string id, int index, MediaAnalyzer media, CancellationToken ct) =>
            Results.Text(await media.Subtitle(id, index, ct), "text/vtt; charset=utf-8"));
        api.MapPost("/library/episodes/{id}/hls", (string id, HlsRequest input, HlsService hls, CancellationToken ct) => hls.Create(id, input, ct));
        api.MapGet("/library/hls/{session}/index.m3u8", (string session, HlsService hls) => Results.Text(hls.Playlist(session), "application/vnd.apple.mpegurl"));
        api.MapGet("/library/hls/{session}/init.mp4", async (string session, HlsService hls, CancellationToken ct) => Segment(await hls.Init(session, ct)));
        api.MapGet("/library/hls/{session}/{index:int}.m4s", async (string session, int index, HlsService hls, CancellationToken ct) => Segment(await hls.Segment(session, index, ct)));
        api.MapDelete("/library/hls/{session}", (string session, HlsService hls) => { hls.Stop(session); return Results.Ok(); });
    }

    /// <summary>Opened with delete sharing: a restarted job may replace the file, and old segments are pruned, while a player still reads it.</summary>
    private static IResult Segment(string path) =>
        Results.Stream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous), "video/mp4");

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
    public sealed record EncoderRequest(string Encoder);
}
