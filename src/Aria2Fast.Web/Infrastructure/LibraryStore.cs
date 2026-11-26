using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Infrastructure;

/// <summary>library.json beside state.json. The index can be rebuilt by scanning; a corrupt file loses only manual matches and corrections.</summary>
public sealed class LibraryStore : JsonFileStore<LibraryState>
{
    public string PosterDirectory { get; }
    /// <summary>Per-video ffprobe results, keyframe lists, extracted subtitles and fonts, in a folder named after the episode ID.</summary>
    public string MediaDirectory { get; }
    /// <summary>HLS segments of running playback sessions; emptied at startup.</summary>
    public string TranscodeDirectory { get; }

    public LibraryStore(StateStore store) : base(store, Path.Combine(store.DataDirectory, "library", "library.json"), s => s.SchemaVersion == 1, "媒体库索引")
    {
        PosterDirectory = Path.Combine(store.DataDirectory, "library", "posters");
        MediaDirectory = Path.Combine(store.DataDirectory, "library", "media");
        TranscodeDirectory = Path.Combine(store.DataDirectory, "library", "transcodes");
        Directory.CreateDirectory(PosterDirectory);
    }
}

/// <summary>progress.json: playback positions, written every few seconds while a video plays.</summary>
public sealed class PlaybackStore(StateStore store)
    : JsonFileStore<PlaybackState>(store, Path.Combine(store.DataDirectory, "library", "progress.json"), s => s.SchemaVersion == 1, "播放进度");
