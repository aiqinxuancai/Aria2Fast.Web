using System.Text;
using System.Text.RegularExpressions;

namespace Aria2Fast.Web.Services;

/// <summary>External subtitle files beside a video, converted to WebVTT, the only format the browser's &lt;track&gt; reads.</summary>
public static partial class Subtitles
{
    public const int SizeLimit = 10 * 1024 * 1024;
    public sealed record Track(string Path, string Label, string Language, string Format);
    private static readonly string[] Extensions = [".ass", ".ssa", ".srt", ".vtt"];
    private static readonly string[] Folders = ["subs", "sub", "subtitles", "字幕"];
    private static readonly string[] Simplified = ["chs", "sc", "gb", "gbk", "hans", "cn", "简", "简体", "简中"];
    private static readonly string[] Traditional = ["cht", "tc", "big5", "hant", "tw", "hk", "繁", "繁体", "繁體", "繁中"];
    private static readonly string[] Japanese = ["jp", "ja", "jpn", "jap", "日", "日语", "日文"];
    private static readonly string[] English = ["en", "eng", "english", "英"];
    private static readonly string[] Codes = ["sc", "tc", "jp", "ja", "en", "zh", "cn", "tw", "hk"];

    static Subtitles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Subtitles named after the video ("Ep01.sc.ass" for "Ep01.mkv") in its folder or a Subs folder beside it.
    /// Fansubs sometimes drop the video's quality tags from the name; then a subtitle of the same work and episode counts.
    /// Simplified Chinese comes first, then Traditional, then the rest.
    /// </summary>
    public static List<Track> Find(string video, double? number)
    {
        var directory = Path.GetDirectoryName(video)!;
        var stem = Path.GetFileNameWithoutExtension(video);
        var files = new List<string>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        try
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*", options));
            foreach (var folder in Directory.EnumerateDirectories(directory, "*", options).Where(d => Folders.Contains(Path.GetFileName(d).ToLowerInvariant())))
                files.AddRange(Directory.EnumerateFiles(folder, "*", options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        var subtitles = files.Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.Ordinal).ToList();
        var found = subtitles.Select(f => (File: f, Suffix: Suffix(Path.GetFileNameWithoutExtension(f), stem))).Where(x => x.Suffix != null).ToList();
        if (found.Count == 0 && number != null)
        {
            var own = ReleaseNames.ParseFile(Path.GetFileName(video));
            found = subtitles.Where(f => ReleaseNames.ParseFile(Path.GetFileName(f)) is var parsed && parsed.Number == number && parsed.Kind == own.Kind
                    && ReleaseNames.Key(parsed.Title) is { Length: > 0 } key && key == ReleaseNames.Key(own.Title))
                .Select(f => (File: f, Suffix: (string?)LastSegment(Path.GetFileNameWithoutExtension(f)))).ToList();
        }
        return found.Select(x =>
            {
                var (label, language, rank) = Describe(x.Suffix!);
                return (Track: new Track(x.File, label, language, Path.GetExtension(x.File).TrimStart('.').ToUpperInvariant()), Rank: rank);
            })
            .OrderBy(x => x.Rank).Select(x => x.Track).ToList();
    }

    /// <summary>The language part after the video's name, "" when the names are equal, null when unrelated.</summary>
    private static string? Suffix(string subtitle, string video)
    {
        if (subtitle.Equals(video, StringComparison.OrdinalIgnoreCase)) return "";
        if (!subtitle.StartsWith(video, StringComparison.OrdinalIgnoreCase) || subtitle[video.Length] is not ('.' or '_' or '-' or ' ')) return null;
        return subtitle[(video.Length + 1)..].Trim('.', '_', '-', ' ');
    }

    private static string LastSegment(string name)
    {
        var dot = name.LastIndexOf('.');
        var last = dot < 0 ? "" : name[(dot + 1)..];
        return last.Length is > 0 and <= 16 && !last.Any(c => c is ' ' or '[' or ']' or '(' or ')') ? last : "";
    }

    /// <summary>Reads fansub language tags such as "sc", "chs&amp;jpn", "jpsc", "zh-Hant" or "简日".</summary>
    public static (string Label, string Language, int Rank) Describe(string suffix)
    {
        var tokens = TokenSplit().Split(suffix.ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        // Glued pairs like "jpsc" or "scjp".
        foreach (var token in tokens.ToList())
            if (token.Length == 4 && Codes.Contains(token[..2]) && Codes.Contains(token[2..])) tokens.AddRange([token[..2], token[2..]]);
        bool Has(string[] names) => tokens.Any(names.Contains) || names.Any(n => n.Length == 1 && suffix.Contains(n));
        var simplified = Has(Simplified);
        var traditional = !simplified && Has(Traditional);
        var japanese = Has(Japanese);
        var chinese = simplified || traditional || tokens.Contains("zh") || tokens.Contains("chi") || suffix.Contains('中');
        if (simplified) return (japanese ? "简日双语" : "简体中文", "zh-Hans", 0);
        if (traditional) return (japanese ? "繁日双语" : "繁體中文", "zh-Hant", 1);
        if (chinese) return ("中文", "zh", 2);
        if (japanese) return ("日本語", "ja", 3);
        if (Has(English)) return ("English", "en", 3);
        return (suffix.Length is > 0 and <= 20 ? suffix : "字幕", "und", 4);
    }

    public static string ToVtt(byte[] data, string extension)
    {
        var text = Decode(data).Replace("\r\n", "\n").Replace('\r', '\n');
        return extension.ToLowerInvariant() switch
        {
            ".srt" => FromSrt(text),
            ".ass" or ".ssa" => FromAss(text),
            _ => text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal) ? text : "WEBVTT\n\n" + text
        };
    }

    /// <summary>Byte order marks first; fansub ASS files are often UTF-16, and older ones GBK.</summary>
    public static string Decode(byte[] data)
    {
        if (data is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("GB18030").GetString(data); }
    }

    private static string FromSrt(string text)
    {
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var block in BlankLines().Split(text.Trim()))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timing < 0 || SrtTiming().Match(lines[timing]) is not { Success: true } match) continue;
            TimeSpan At(int group) => new TimeSpan(0, int.Parse(match.Groups[group].Value), int.Parse(match.Groups[group + 1].Value), int.Parse(match.Groups[group + 2].Value))
                + TimeSpan.FromMilliseconds(int.Parse(match.Groups[group + 3].Value.PadRight(3, '0')));
            AppendCue(output, At(1), At(5), lines.Skip(timing + 1).Select(l => CueText(l, html: true)), "");
        }
        return output.ToString();
    }

    /// <summary>Dialogue lines only: styles, positioning (other than top alignment), karaoke and effects are dropped, and vector drawings skipped.</summary>
    private static string FromAss(string text)
    {
        var cues = new List<(TimeSpan Start, TimeSpan End, string Text, bool Top)>();
        var events = false;
        string[] format = ["layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text"];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!events) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)) { format = line[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToArray(); continue; }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line[9..].Split(',', format.Length);
            int start = Array.IndexOf(format, "start"), end = Array.IndexOf(format, "end");
            if (fields.Length < format.Length || start < 0 || end < 0 || format[^1] != "text") continue;
            if (AssTime(fields[start]) is not { } from || AssTime(fields[end]) is not { } to || to <= from) continue;
            var body = fields[^1];
            if (Drawing().IsMatch(body)) continue;
            var top = TopAlignment().IsMatch(body);
            body = Override().Replace(body, "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
            cues.Add((from, to, body, top));
        }
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues.Distinct().OrderBy(c => c.Start))
            AppendCue(output, cue.Start, cue.End, cue.Text.Split('\n').Select(l => CueText(l, html: false)), cue.Top ? " line:0" : "");
        return output.ToString();
    }

    private static void AppendCue(StringBuilder output, TimeSpan start, TimeSpan end, IEnumerable<string> lines, string settings)
    {
        // A blank line would end the cue early.
        var text = string.Join("\n", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
        if (text.Length == 0 || end <= start) return;
        output.Append(Time(start)).Append(" --> ").Append(Time(end)).Append(settings).Append('\n').Append(text).Append("\n\n");
    }

    private static string Time(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    private static TimeSpan? AssTime(string value) =>
        AssTiming().Match(value.Trim()) is { Success: true } m
            ? new TimeSpan(0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) + TimeSpan.FromMilliseconds(int.Parse(m.Groups[4].Value.PadRight(3, '0')[..3]))
            : null;

    /// <summary>Escapes text for WebVTT. SRT's &lt;i&gt;, &lt;b&gt; and &lt;u&gt; are valid WebVTT and kept; other markup such as &lt;font&gt; is dropped.</summary>
    private static string CueText(string text, bool html)
    {
        text = Override().Replace(text, "");
        if (!html) return Escape(text);
        var output = new StringBuilder();
        foreach (var part in Markup().Split(text))
        {
            if (!Markup().IsMatch(part)) output.Append(Escape(part));
            else if (SimpleTag().IsMatch(part)) output.Append(part.ToLowerInvariant());
        }
        return output.ToString();
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex TokenSplit();
    [GeneratedRegex(@"\n[ \t]*\n")] private static partial Regex BlankLines();
    [GeneratedRegex(@"(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})")] private static partial Regex SrtTiming();
    [GeneratedRegex(@"^(\d+):(\d{1,2}):(\d{1,2})[.,](\d{1,3})$")] private static partial Regex AssTiming();
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex Override();
    [GeneratedRegex(@"\{[^}]*\\p[1-9][^}]*\}")] private static partial Regex Drawing();
    [GeneratedRegex(@"\{[^}]*\\an[789][^}]*\}")] private static partial Regex TopAlignment();
    [GeneratedRegex(@"(</?[a-zA-Z][^<>]*>)")] private static partial Regex Markup();
    [GeneratedRegex(@"^</?[ibu]>$", RegexOptions.IgnoreCase)] private static partial Regex SimpleTag();
}
