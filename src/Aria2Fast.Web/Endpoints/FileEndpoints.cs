using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;
namespace Aria2Fast.Web.Endpoints;

public static class FileEndpoints
{
    public static void MapFiles(this RouteGroupBuilder api)
    {
        api.MapGet("/files/{gid}/{index:int}", async (string gid, int index, FileService files, CancellationToken ct) =>
        {
            var path = await files.FileAt(gid, index, ct);
            return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
        });
        api.MapPost("/files/{gid}/rename/preview", (string gid, FileService files, CancellationToken ct) => files.Preview(gid, ct));
        api.MapPost("/files/{gid}/rename", async (string gid, List<RenameItem> changes, FileService files, CancellationToken ct) => new { renamed = await files.Apply(gid, changes, ct) });
    }
}
