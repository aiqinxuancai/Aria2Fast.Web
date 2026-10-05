using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class DownloadPaths(StateStore store, AriaRpc rpc)
{
    public async Task<string> Default(string? nodeId, CancellationToken ct = default)
    {
        var node = rpc.Node(nodeId);
        if (node.Id == "local") return store.Read().Settings.DownloadDirectory;
        if (!string.IsNullOrWhiteSpace(node.DownloadDirectory)) return node.DownloadDirectory;
        var options = await rpc.Call("getGlobalOption", nodeId: node.Id, ct: ct);
        return options["dir"]?.ToString() ?? "";
    }

    public static string Compose(string root, string name, int season, string? aiName = null)
    {
        var directory = root;
        if (!string.IsNullOrWhiteSpace(name)) directory = Join(directory, Validation.Segment(name));
        if (!string.IsNullOrWhiteSpace(aiName)) directory = Join(directory, Validation.Segment(aiName));
        if (season > 0) directory = Join(directory, $"Season {season}");
        return directory;
    }

    private static string Join(string root, string name) => string.IsNullOrWhiteSpace(root) ? name : root.TrimEnd('/', '\\') + "/" + name;
}
