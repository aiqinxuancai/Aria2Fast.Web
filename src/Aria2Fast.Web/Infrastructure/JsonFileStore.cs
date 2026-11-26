using System.Text.Json;

namespace Aria2Fast.Web.Infrastructure;

/// <summary>
/// A JSON file beside state.json for data that is rebuilt by scanning or is not essential. Unlike state.json,
/// a corrupt file is set aside rather than blocking startup. Reads return private copies; writes are atomic.
/// </summary>
public abstract class JsonFileStore<T> where T : class, new()
{
    // Compact: these files are rewritten often.
    private static readonly JsonSerializerOptions Json = new(StateStore.Json) { WriteIndented = false };
    private readonly object gate = new();
    private readonly string file;
    private T state;

    protected JsonFileStore(StateStore store, string file, Func<T, bool> supported, string name)
    {
        this.file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        state = new T();
        if (!File.Exists(file)) return;
        try
        {
            state = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) ?? throw new JsonException(name + "为空");
            if (!supported(state)) throw new JsonException("不支持此" + name + "版本");
        }
        catch (JsonException ex)
        {
            var aside = file + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt";
            File.Move(file, aside, true);
            state = new T();
            store.Log(name + "无法读取，已重新建立；原文件保存为 " + Path.GetFileName(aside), "warning", ex.Message);
        }
    }

    public T Read()
    {
        lock (gate) return Clone(state);
    }

    /// <summary>
    /// Reads without copying the whole state. The current state is never modified in place (Replace builds a copy),
    /// so the result may reference it, but callers must not modify what they get.
    /// </summary>
    public TResult Query<TResult>(Func<T, TResult> query)
    {
        lock (gate) return query(state);
    }

    public void Update(Action<T> update) => Replace(s => { update(s); return s; });

    /// <summary>Builds the next state from a private copy of the current one and persists it atomically.</summary>
    public T Replace(Func<T, T> build)
    {
        lock (gate)
        {
            var next = build(Clone(state));
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json));
            AtomicFile.Replace(temp, file);
            state = next;
            return Clone(next);
        }
    }

    private static T Clone(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}

public static class AtomicFile
{
    /// <summary>
    /// Moves a freshly written temp file over the target. On Windows the replace fails with "access denied" while
    /// another process (typically antivirus scanning the previous write) holds the target open, so retry briefly.
    /// </summary>
    public static void Replace(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(temp, target, true); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 8) { Thread.Sleep(25 * attempt); }
        }
    }
}
