using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Infrastructure;

/// <summary>library.json beside state.json. The index can be rebuilt by scanning; a corrupt file loses only manual matches and corrections.</summary>
public sealed class LibraryStore : JsonFileStore<LibraryState>
{
    public string PosterDirectory { get; }

    public LibraryStore(StateStore store) : base(store, Path.Combine(store.DataDirectory, "library", "library.json"), s => s.SchemaVersion == 1, "媒体库索引")
    {
        PosterDirectory = Path.Combine(store.DataDirectory, "library", "posters");
        Directory.CreateDirectory(PosterDirectory);
    }
}

/// <summary>progress.json: playback positions, written every few seconds while a video plays.</summary>
public sealed class PlaybackStore(StateStore store)
    : JsonFileStore<PlaybackState>(store, Path.Combine(store.DataDirectory, "library", "progress.json"), s => s.SchemaVersion == 1, "播放进度");
