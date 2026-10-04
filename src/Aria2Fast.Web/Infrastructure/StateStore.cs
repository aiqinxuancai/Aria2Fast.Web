using System.Text.Json;
using System.Text.Json.Serialization;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Infrastructure;

/// <summary>Persist snapshots atomically; callers never receive mutable shared state.</summary>
public sealed class StateStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object gate = new();
    private readonly string file;
    private AppState state;
    public string DataDirectory { get; }

    public StateStore(IConfiguration configuration)
    {
        DataDirectory = Path.GetFullPath(configuration["ARIA2FAST_DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
        Directory.CreateDirectory(DataDirectory);
        file = Path.Combine(DataDirectory, "state.json");
        // Fail visibly on corrupt data rather than overwrite subscriptions or credentials.
        state = File.Exists(file)
            ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("配置文件为空")
            : new AppState();
        if (state.SchemaVersion != 1) throw new InvalidDataException("不支持此配置文件版本");
        if (string.IsNullOrWhiteSpace(state.Settings.DownloadDirectory))
            state.Settings.DownloadDirectory = Path.GetFullPath(configuration["ARIA2FAST_DOWNLOAD_DIR"] ?? Path.Combine(DataDirectory, "downloads"));
        if (bool.TryParse(configuration["ARIA2FAST_LOCAL_ENABLED"], out var enabled)) state.Settings.LocalEnabled = enabled;
        Directory.CreateDirectory(state.Settings.DownloadDirectory);
    }

    public AppState Read()
    {
        lock (gate) return Clone(state);
    }

    public T Update<T>(Func<AppState, T> update)
    {
        lock (gate)
        {
            var copy = Clone(state);
            var result = update(copy);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, file, true);
            state = copy;
            return result;
        }
    }

    public void Update(Action<AppState> update) => Update(s => { update(s); return true; });
    public void Log(string message, string level = "info") => Update(s =>
    {
        s.Notices.Insert(0, new(DateTimeOffset.UtcNow, level, message));
        if (s.Notices.Count > 400) s.Notices.RemoveRange(400, s.Notices.Count - 400);
    });
    private static AppState Clone(AppState value) => JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(value, Json), Json)!;
}
