using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>Number is null for extras and for names without a recognisable episode.</summary>
public sealed record ParsedRelease(string Title, int? Season, double? Number, LibraryEpisodeKind Kind);

/// <summary>Reads the work title, season and episode from fansub, scene and plain release names.</summary>
public static partial class ReleaseNames
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".m4v", ".webm", ".mov", ".avi", ".ts", ".m2ts", ".flv", ".wmv", ".rmvb" };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Not Path.GetFileNameWithoutExtension: release titles contain " / " between alternative names.</summary>
    public static ParsedRelease ParseFile(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var extension = dot > 0 && fileName.Length - dot <= 6 && !fileName.AsSpan(dot + 1).ContainsAnyExcept(Alphanumerics);
        return Parse(extension ? fileName[..dot] : fileName);
    }

    private static readonly System.Buffers.SearchValues<char> Alphanumerics = System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    public static ParsedRelease Parse(string name)
    {
        var text = Normalize(name);
        var body = StripGroup(text);
        // In "[Group][Title][05]" the first bracket left is the title, even a numeric one such as [86].
        var episodesFrom = body.StartsWith('[') ? body.IndexOf(']') + 1 : 0;
        var season = Season(body);
        double? number = null;
        var kind = LibraryEpisodeKind.Episode;
        Match match;
        // Explicit forms first; bare numbers only when nothing else matches.
        if ((match = SeasonEpisode().Match(body)).Success) { season = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); number = Number(match.Groups[2].Value); }
        else if ((match = ChineseEpisode().Match(body)).Success) number = Number(match.Groups[1].Value);
        else if ((match = Last(DashEpisode().Matches(body))).Success) number = Number(match.Groups[1].Value);
        else if ((match = BracketEpisode().Matches(body, episodesFrom).FirstOrDefault(m => !IsYear(m.Groups[1].Value)) ?? Match.Empty).Success) number = Number(match.Groups[1].Value);
        else if ((match = ExtraToken().Match(body)).Success) kind = LibraryEpisodeKind.Extra;
        else if ((match = SpecialToken().Match(body)).Success) { kind = LibraryEpisodeKind.Special; number = match.Groups[1].Success ? Number(match.Groups[1].Value) : null; }
        else if ((match = PrefixedEpisode().Match(body, episodesFrom)).Success) number = Number(match.Groups[1].Value);
        else if ((match = Last(BareEpisode().Matches(body))).Success) number = Number(match.Groups[1].Value);
        return new(TitleOf(text, body, match.Success ? match.Index : -1), season, number, kind);
    }

    /// <summary>Work title from a folder name such as "[Group] Title [01-12][1080p]" or "作品 第二季".</summary>
    /// <summary>
    /// A movie's title and year from its folder or file name: "Inception (2010)", "Inception.2010.1080p.BluRay",
    /// "[Group] 你的名字 [2016][1080p]". The rightmost year wins, so "Blade Runner 2049 (2017)" keeps 2049 in the title.
    /// </summary>
    public static (string Title, int? Year) Movie(string name)
    {
        var body = StripGroup(Normalize(name));
        if (MovieYear().Matches(body).LastOrDefault(m => m.Index > 0) is { } year)
        {
            var title = Title(body[..year.Index].TrimEnd(' ', '-', '(', '[', '.'));
            if (title.Length > 0) return (title, int.Parse(year.Groups[1].Value));
        }
        return (Title(name), null);
    }

    public static string Title(string name)
    {
        var text = Normalize(name);
        var title = TitleOf(text, StripGroup(text), -1);
        return title.Length > 0 ? title : name.Trim();
    }

    public static int? Season(string text)
    {
        var value = Normalize(text);
        foreach (var pattern in new[] { SeasonWord(), SeasonOrdinal(), SeasonChinese(), SeasonShort() })
            if (pattern.Match(value) is { Success: true } match && (int?)Number(match.Groups[1].Value) is int season and > 0 and < 100) return season;
        return null;
    }

    /// <summary>Season of a folder named only "Season 2", "S02" or "第二季".</summary>
    public static int? SeasonFolder(string name) =>
        SeasonFolderName().Match(Normalize(name)) is { Success: true } match ? (int?)Number(match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value) : null;

    /// <summary>Episodes inside SPs / Extras style folders are not part of the main run.</summary>
    public static LibraryEpisodeKind? FolderKind(string name)
    {
        var value = Normalize(name);
        return SpecialFolderName().IsMatch(value) ? LibraryEpisodeKind.Special : ExtraFolderName().IsMatch(value) ? LibraryEpisodeKind.Extra : null;
    }

    /// <summary>Alternative names joined by "/" or " - " in release titles. CJK names first: Bangumi indexes Chinese and Japanese names best.</summary>
    public static List<string> SearchTerms(string title) =>
        AlternativeNames().Split(title).Select(p => p.Trim()).Where(p => p.Length > 0)
            .OrderBy(p => p.Any(IsCjk) ? 0 : 1).Append(title.Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

    /// <summary>Comparison key: width-folded, lower case, letters and digits only.</summary>
    public static string Key(string value) => new(Normalize(value).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Normalize(string value)
    {
        // NFKC folds full-width forms such as ［０５］ and ／.
        var text = value.Normalize(NormalizationForm.FormKC).Replace('【', '[').Replace('】', ']').Replace('_', ' ');
        text = Stars().Replace(text, " ");
        if (!text.Contains(' ') && text.Count(c => c == '.') >= 2) text = SceneDots().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    private static string StripGroup(string text)
    {
        if (!text.StartsWith('[')) return text;
        var end = text.IndexOf(']');
        return end > 0 && end < text.Length - 1 ? text[(end + 1)..].TrimStart() : text;
    }

    private static string TitleOf(string text, string body, int cut)
    {
        string title;
        if (body.StartsWith('['))
        {
            // A numeric first bracket followed by the episode is a title such as [86].
            var first = Brackets().Match(body);
            title = first.Success && cut > first.Index && first.Groups[1].Value.Trim().All(char.IsAsciiDigit) ? first.Groups[1].Value : FirstTitleBracket(body);
        }
        else
        {
            var end = cut >= 0 ? cut : body.Length;
            var bracket = body.IndexOfAny(['[', '(']);
            if (bracket > 0 && bracket < end) end = bracket;
            title = body[..end];
        }
        title = CleanTitle(title);
        // "[Title][05]" has no group: the bracket taken as the group was the title.
        return title.Length > 0 ? title : CleanTitle(FirstTitleBracket(text));
    }

    private static string FirstTitleBracket(string text) => Brackets().Matches(text).Select(m => m.Groups[1].Value).FirstOrDefault(v => !TagLike().IsMatch(v) && v.Any(char.IsLetter)) ?? "";

    private static string CleanTitle(string value) => Spaces().Replace(SeasonMarker().Replace(value, " "), " ").Trim(' ', '-', '–', '—', '~', ':', '|', '/', '.', ',', '&', '+');

    private static Match Last(MatchCollection matches) => matches.Count > 0 ? matches[^1] : Match.Empty;

    private static bool IsYear(string value) => value.Length == 4 && int.TryParse(value, out var year) && year is >= 1950 and <= 2099;

    // Hiragana through CJK Unified Ideographs, plus CJK Compatibility Ideographs.
    private static bool IsCjk(char c) => c is >= '぀' and <= '鿿' or >= '豈' and <= '﫿';

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : Chinese(value);

    /// <summary>一 to 九百九十九, enough for seasons and episodes.</summary>
    private static int? Chinese(string value)
    {
        const string digits = "零一二三四五六七八九";
        int total = 0, current = 0;
        foreach (var c in value.Replace('〇', '零').Replace('两', '二'))
        {
            var digit = digits.IndexOf(c);
            if (digit >= 0) current = digit;
            else if (c == '十') { total += (current == 0 ? 1 : current) * 10; current = 0; }
            else if (c == '百') { total += (current == 0 ? 1 : current) * 100; current = 0; }
            else return null;
        }
        return total + current is > 0 and var result ? result : null;
    }

    [GeneratedRegex(@"★[^★]*★")] private static partial Regex Stars();
    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)")] private static partial Regex SceneDots();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\[([^\[\]]+)\]")] private static partial Regex Brackets();
    // Spaced separators only, so "Fate/stay night" stays whole.
    [GeneratedRegex(@"\s+/\s+|\s+[-–—|]\s+")] private static partial Regex AlternativeNames();
    [GeneratedRegex(@"^\s*(?:\d{1,4}(?:\s?[-~]\s?\d{1,4})?|\d{3,4}p|v\d+|CHS|CHT|GB|BIG5|JPN?|SC|TC|.*新番.*|.*字幕.*|简.*|繁.*|(?:HEVC|AVC|x26[45]|WEB|BD|AAC|FLAC|MP4|MKV|\d+bit).*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TagLike();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,2})\s?E(\d{1,4}(?:\.\d)?)(?!\d)", RegexOptions.IgnoreCase)] private static partial Regex SeasonEpisode();
    [GeneratedRegex(@"第\s*(\d{1,4}(?:\.\d)?|[零〇一二三四五六七八九十百两]{1,6})\s*[话話集回]")] private static partial Regex ChineseEpisode();
    [GeneratedRegex(@"\s[-–—]\s(\d{1,4}(?:\.\d)?)(?:v\d{1,2})?(?:\s?(?:END|Fin|完))?(?=$|[\s\[\(])", RegexOptions.IgnoreCase)] private static partial Regex DashEpisode();
    [GeneratedRegex(@"\[\s*(\d{1,4}(?:\.\d)?)\s*(?:v\d{1,2})?\s*(?:END|Fin|完)?\s*\]", RegexOptions.IgnoreCase)] private static partial Regex BracketEpisode();
    [GeneratedRegex(@"(?:#|(?<![A-Za-z0-9])(?:EP?|Episode|Ep\.)\s?)(\d{1,4}(?:\.\d)?)(?:v\d{1,2})?(?!\d)", RegexOptions.IgnoreCase)] private static partial Regex PrefixedEpisode();
    [GeneratedRegex(@"(?<=\s)(?<!(?:Season|Part|Vol\.?|Volume|No\.?)\s)(\d{1,3}(?:\.5)?)(?:v\d)?(?=\s*(?:\[|\(|$))", RegexOptions.IgnoreCase)] private static partial Regex BareEpisode();
    // OP / ED only in capitals: as lower-case words they are too common in titles.
    [GeneratedRegex(@"(?:^|[\[\s])(?:NC\s?(?:OP|ED)|(?-i:OP|ED)|PV|CM|SPOT|Menu|Preview|Trailer|Teaser)\s?\d{0,2}(?:v\d)?(?=$|[\]\s\[\(])", RegexOptions.IgnoreCase)] private static partial Regex ExtraToken();
    [GeneratedRegex(@"(?:^|[\[\s])(?:SPs?|OVA|OAD|Specials?|特典|番外|特别篇|特別篇|总集篇|総集編)\s?(\d{1,2}(?:\.\d)?)?(?:v\d)?(?=$|[\]\s\[\(])", RegexOptions.IgnoreCase)] private static partial Regex SpecialToken();

    [GeneratedRegex(@"(?<![A-Za-z])Season\s?(\d{1,2})(?!\d)", RegexOptions.IgnoreCase)] private static partial Regex SeasonWord();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})(?:st|nd|rd|th)\s+Season", RegexOptions.IgnoreCase)] private static partial Regex SeasonOrdinal();
    [GeneratedRegex(@"第\s*(\d{1,2}|[一二三四五六七八九十]{1,3})\s*[季期]")] private static partial Regex SeasonChinese();
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,2})(?=E\d|[^A-Za-z0-9.]|$)")] private static partial Regex SeasonShort();
    [GeneratedRegex(@"(?<![A-Za-z])Season\s?\d{1,2}(?!\d)|(?<!\d)\d{1,2}(?:st|nd|rd|th)\s+Season|第\s*(?:\d{1,2}|[一二三四五六七八九十]{1,3})\s*[季期]|(?<![A-Za-z0-9])S\d{1,2}(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonMarker();
    [GeneratedRegex(@"^(?:Season\s?(\d{1,3})|S(\d{1,2})|第\s*(\d{1,2}|[一二三四五六七八九十]{1,3})\s*[季期])$", RegexOptions.IgnoreCase)] private static partial Regex SeasonFolderName();
    [GeneratedRegex(@"^(?:SPs?|Specials?|OVAs?|OADs?|特典|映像特典|番外|特别篇|特別篇|Season\s?0+)$", RegexOptions.IgnoreCase)] private static partial Regex SpecialFolderName();
    [GeneratedRegex(@"^(?:Extras?|Bonus|NC(?:OP|ED)?s?|Menus?|CDs?|Scans?|PVs?|CMs?|Trailers?|Others?|Featurettes?)$", RegexOptions.IgnoreCase)] private static partial Regex ExtraFolderName();
    [GeneratedRegex(@"(?:^|[\s\[(.])((?:19|20)\d{2})(?=$|[\s\]).])")] private static partial Regex MovieYear();
}
