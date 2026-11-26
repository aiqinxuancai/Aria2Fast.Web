using System.Text.Json;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Infrastructure;

/// <summary>
/// library.json beside state.json. The index can be rebuilt by scanning, so unlike state.json a corrupt
/// file is set aside rather than blocking startup; only manual matches are lost.
/// </summary>
public sealed class LibraryStore
{
    // Compact: the index is rewritten after each work's metadata arrives.
    private static readonly JsonSerializerOptions Json = new(StateStore.Json) { WriteIndented = false };
    private readonly object gate = new();
    private readonly string file;
    private LibraryState state;
    public string PosterDirectory { get; }

    public LibraryStore(StateStore store)
    {
        var directory = Path.Combine(store.DataDirectory, "library");
        PosterDirectory = Path.Combine(directory, "posters");
        Directory.CreateDirectory(PosterDirectory);
        file = Path.Combine(directory, "library.json");
        state = new LibraryState();
        if (!File.Exists(file)) return;
        try
        {
            state = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(file), Json) ?? throw new JsonException("媒体库索引为空");
            if (state.SchemaVersion != 1) throw new JsonException("不支持此媒体库索引版本");
        }
        catch (JsonException ex)
        {
            var aside = file + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt";
            File.Move(file, aside, true);
            state = new LibraryState();
            store.Log("媒体库索引无法读取，已重新建立；原文件保存为 " + Path.GetFileName(aside), "warning", ex.Message);
        }
    }

    public LibraryState Read()
    {
        lock (gate) return Clone(state);
    }

    public void Update(Action<LibraryState> update) => Replace(s => { update(s); return s; });

    /// <summary>Builds the next state from a private copy of the current one and persists it atomically.</summary>
    public LibraryState Replace(Func<LibraryState, LibraryState> build)
    {
        lock (gate)
        {
            var next = build(Clone(state));
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            File.Move(temp, file, true);
            state = next;
            return Clone(next);
        }
    }

    private static LibraryState Clone(LibraryState value) => JsonSerializer.Deserialize<LibraryState>(JsonSerializer.Serialize(value, Json), Json)!;
}
