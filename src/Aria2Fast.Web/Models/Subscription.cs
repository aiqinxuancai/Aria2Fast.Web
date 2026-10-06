namespace Aria2Fast.Web.Models;

public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新订阅";
    public string Url { get; set; } = "";
    public string NodeId { get; set; } = "local";
    public string Directory { get; set; } = "";
    public string NamePath { get; set; } = "";
    public int Season { get; set; }
    public string Filter { get; set; } = "";
    public string ExcludeFilter { get; set; } = "";
    public bool IsFilterRegex { get; set; }
    public bool? IsExcludeFilterRegex { get; set; }
    public bool AutoDir { get; set; }
    public bool Enabled { get; set; } = true;
    public bool SkipExisting { get; set; }
    public bool Initialized { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
    public List<SubscriptionEntry> History { get; set; } = [];
}

public sealed record SubscriptionEntry(string Key, string Title, string Url, string? Gid, DateTimeOffset Time, bool Skipped = false);
public sealed record FeedItem(string Key, string Title, string Url, DateTimeOffset? Published, string Size);
public sealed record FeedPreview(string Title, List<FeedItem> Items);
public sealed record AnimeCard(string Id, string Name, string Url, string Image, string Day, bool HasReleases = true);
public sealed record AnimeGroup(string Name, string Url);
public sealed record AnimeDetail(string Id, string Name, string Summary, List<AnimeGroup> Groups, TmdbInfo? Tmdb, AiReview? Review, string OriginalSummary = "");
public sealed record TmdbInfo(int Id, string Name, string Overview, double Score, int VoteCount, double Popularity, string Poster, string FirstAirDate);
public sealed record RenameItem(string Old, string New);
