using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
namespace Aria2Fast.Web.Services;

public sealed class FileService(StateStore store, AriaRpc rpc, AiService ai)
{
    public async Task<string> FileAt(string gid, int index, CancellationToken ct)
    {
        var status = await rpc.Call("tellStatus", [gid], "local", ct);
        if (status["status"]?.ToString() != "complete") throw new ArgumentException("文件尚未下载完成");
        var entries = status["files"]!.AsArray();
        if (index < 0 || index >= entries.Count) throw new FileNotFoundException("文件索引不存在");
        var path = entries[index]!["path"]!.ToString();
        if (!File.Exists(path)) throw new FileNotFoundException("原文件不存在或已被整理移动");
        return Validation.LocalFile(store.Read().Settings.DownloadDirectory, path);
    }
    public async Task<List<string>> Files(string gid, CancellationToken ct)
    {
        var status = await rpc.Call("tellStatus", [gid], "local", ct);
        if (status["status"]?.ToString() != "complete") throw new ArgumentException("仅可整理已完成的本地任务");
        return status["files"]!.AsArray().Select(x => x!["path"]!.ToString()).Where(File.Exists)
            .Select(path => Validation.LocalFile(store.Read().Settings.DownloadDirectory, path)).ToList();
    }
    public async Task<List<RenameItem>> Preview(string gid, CancellationToken ct) => await ai.Rename((await Files(gid, ct)).Select(Path.GetFileName)!, ct);
    public async Task<int> Apply(string gid, List<RenameItem> changes, CancellationToken ct)
    {
        var files = await Files(gid, ct);
        var moves = new List<(string Source, string Target)>();
        foreach (var change in changes)
        {
            var matches = files.Where(x => Path.GetFileName(x) == change.Old).ToArray();
            if (matches.Length != 1 || change.New != Validation.Segment(change.New)) throw new ArgumentException("文件名不唯一或目标名称不合法");
            var target = Path.Combine(Path.GetDirectoryName(matches[0])!, change.New);
            if (target == matches[0]) continue;
            if (File.Exists(target) || moves.Any(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("目标文件已存在或重名");
            moves.Add((matches[0], target));
        }
        var completed = new List<(string Source, string Target)>();
        try { foreach (var move in moves) { File.Move(move.Source, move.Target); completed.Add(move); } }
        catch { foreach (var move in completed.AsEnumerable().Reverse()) File.Move(move.Target, move.Source); throw; }
        store.Log($"本地整理完成：{moves.Count} 个文件（任务 {gid}）");
        return moves.Count;
    }
}
