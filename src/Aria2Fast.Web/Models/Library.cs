namespace Aria2Fast.Web.Models;

/// <summary>Media library index. Rebuilt from the local download directory, so it lives in its own file rather than state.json.</summary>
public sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 1;
    public List<LibrarySeries> Series { get; set; } = [];
    public List<LibraryEpisode> Episodes { get; set; } = [];
    /// <summary>Relative file path → subscription that downloaded it, recorded when the download completes.</summary>
    public Dictionary<string, LibraryOrigin> Origins { get; set; } = [];
    /// <summary>Relative file path → episode number set by hand or by AI recognition; applied on every scan.</summary>
    public Dictionary<string, LibraryEpisodeOverride> Overrides { get; set; } = [];
    public DateTimeOffset? LastScan { get; set; }
    public string? LastScanWarning { get; set; }
}

/// <summary>One season of a work in one folder. Matches one Bangumi subject and usually one subscription.</summary>
public sealed class LibrarySeries
{
    public string Id { get; set; } = "";
    /// <summary>Folder (or parsed title for loose files) plus season; stable across rescans.</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Relative to the download root, '/'-separated; empty for loose files in the root.</summary>
    public string Directory { get; set; } = "";
    public int Season { get; set; }
    public string? SubscriptionId { get; set; }
    public string? MikanId { get; set; }
    public int? BgmId { get; set; }
    public int? TmdbId { get; set; }
    /// <summary>Chosen by the user; scans and subscriptions no longer change the match.</summary>
    public bool ManualMatch { get; set; }
    public LibraryMatchSource? MatchedBy { get; set; }
    public LibraryMetadataStatus Status { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttempt { get; set; }
    public LibraryMetadata? Metadata { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum LibraryMetadataStatus { Pending, Matched, Unmatched, Failed }
public enum LibraryMatchSource { Manual, Subscription, Mikan, Search }
public enum LibraryEpisodeKind { Episode, Special, Extra }

public sealed class LibraryEpisode
{
    public string Id { get; set; } = "";
    public string SeriesId { get; set; } = "";
    /// <summary>Relative to the download root, '/'-separated.</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset Modified { get; set; }
    public double? Number { get; set; }
    public LibraryEpisodeKind Kind { get; set; }
    /// <summary>Null when parsed from the file name, otherwise the override's source ("manual" or "ai").</summary>
    public string? NumberSource { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Source is "bangumi" or "tmdb"; SourceId is that site's subject / TV ID.</summary>
public sealed record LibraryMetadata(string Source, int SourceId, string Name, string NameCn, string Summary, string AirDate, double? Score, int? TotalEpisodes, string Platform)
{
    public string PosterUrl { get; init; } = "";
    /// <summary>File name inside the poster cache; empty until downloaded.</summary>
    public string PosterFile { get; init; } = "";
    public List<LibraryEpisodeMeta> Episodes { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Number is the episode within the subject (Bangumi "ep"); Sort may continue across seasons. Type 0 is a main episode, 1 a special.</summary>
public sealed record LibraryEpisodeMeta(double Number, double Sort, string Name, string NameCn, string AirDate, int Type = 0);

public sealed record LibraryOrigin(string SubscriptionId, string Gid, DateTimeOffset Time);

/// <summary>Source is "manual" or "ai". A null number with kind Episode marks the file as unnumbered on purpose.</summary>
public sealed record LibraryEpisodeOverride(double? Number, LibraryEpisodeKind Kind, string Source);

/// <summary>Playback position per episode ID, kept apart from the index because it changes every few seconds during playback.</summary>
public sealed class PlaybackState
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, PlaybackProgress> Episodes { get; set; } = [];
}

public sealed record PlaybackProgress(double Position, double Duration, bool Watched, DateTimeOffset UpdatedAt);
