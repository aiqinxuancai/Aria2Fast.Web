using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed record MediaInfo(string Container, double Duration, long BitRate, MediaVideo? Video, List<MediaAudio> Audio, List<MediaSubtitle> Subtitles, List<MediaAttachment> Fonts);
/// <summary>CodecString is the RFC 6381 name the browser is asked about (MediaSource.isTypeSupported); empty when no browser decodes it.</summary>
public sealed record MediaVideo(int Index, string Codec, string Profile, int Level, string PixelFormat, int BitDepth, int Width, int Height, double FrameRate, bool Interlaced, string CodecString);
public sealed record MediaAudio(int Index, string Codec, string Profile, int Channels, string Language, string Title, bool Default, string CodecString, string Label);
/// <summary>Text subtitles convert to WebVTT; styled (ASS / SSA) and image subtitles (PGS, DVD) show fully only when burned in.</summary>
public sealed record MediaSubtitle(int Index, string Codec, string Language, string Title, bool Default, bool Forced, bool Text, bool Styled, bool Image, string Label);
public sealed record MediaAttachment(int Index, string FileName, string MimeType);

/// <summary>
/// ffprobe results, keyframe lists and extracted subtitles per video, kept in data/library/media/{episode ID}
/// and reused while the file's size and modification time are unchanged.
/// </summary>
public sealed class MediaAnalyzer(LibraryStore library, FfmpegService ffmpeg, PlaybackService playback, IHostApplicationLifetime lifetime)
{
    private static readonly JsonSerializerOptions Json = new(StateStore.Json) { WriteIndented = false };
    private static readonly string[] TextCodecs = ["ass", "ssa", "subrip", "srt", "webvtt", "mov_text", "text", "subviewer", "subviewer1", "sami", "microdvd", "mpl2", "realtext", "jacosub", "pjs", "vplayer", "stl"];
    private static readonly string[] ImageCodecs = ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"];
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];
    // Shared work per video and kind: a request that gives up does not cancel what others wait for.
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> running = new();

    private sealed record Cached<T>(long Size, DateTimeOffset Modified, T Value);
    public sealed record ExtractedSubtitles(Dictionary<int, string> Files, string? Fonts);

    /// <summary>Media details, or null when transcoding is off or ffmpeg is missing.</summary>
    public async Task<MediaInfo?> Info(string episodeId, CancellationToken ct)
    {
        if (await ffmpeg.Ready(ct) is not { } tools) return null;
        var (episode, file) = playback.Resolve(episodeId);
        return await Shared(episode.Id, "info", file, () => Probe(tools.Ffprobe, file), ct);
    }

    /// <summary>Video keyframe times in seconds, read from every packet of the file (cues can be sparse).</summary>
    public async Task<double[]> Keyframes(string episodeId, CancellationToken ct)
    {
        var tools = await ffmpeg.Ready(ct) ?? throw new ArgumentException("未启用转码或没有找到 ffmpeg");
        var (episode, file) = playback.Resolve(episodeId);
        return await Shared(episode.Id, "keyframes", file, async () =>
        {
            var result = await FfmpegService.Run(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "-i", file], lifetime.ApplicationStopping, TimeSpan.FromMinutes(10));
            if (result.ExitCode != 0) throw new InvalidOperationException("读取关键帧失败：" + FfmpegService.Tail(result.Error));
            return ParseKeyframes(result.Output);
        }, ct);
    }

    /// <summary>
    /// Text subtitle streams copied out of the file (ASS stays ASS, others become SRT) plus its font attachments, in one pass:
    /// subtitle packets are spread through the whole file, so this reads all of it once.
    /// </summary>
    public async Task<ExtractedSubtitles> Subtitles(string episodeId, CancellationToken ct)
    {
        var tools = await ffmpeg.Ready(ct) ?? throw new ArgumentException("未启用转码或没有找到 ffmpeg");
        var info = await Info(episodeId, ct) ?? throw new ArgumentException("未启用转码或没有找到 ffmpeg");
        var (episode, file) = playback.Resolve(episodeId);
        return await Shared(episode.Id, "subtitles", file, async () =>
        {
            var folder = Folder(episode.Id);
            var texts = info.Subtitles.Where(s => s.Text).ToList();
            var files = texts.ToDictionary(s => s.Index, s => $"sub-{s.Index}" + (s.Styled ? ".ass" : s.Codec == "webvtt" ? ".vtt" : ".srt"));
            var fonts = texts.Any(s => s.Styled) && info.Fonts.Count > 0 ? "fonts" : null;
            if (files.Count == 0) return new ExtractedSubtitles(files, null);
            if (fonts != null) Directory.CreateDirectory(Path.Combine(folder, fonts));
            var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
            // Attachments are written under our own names: the names stored in the file are not trusted as paths.
            if (fonts != null) foreach (var font in info.Fonts) arguments.AddRange([$"-dump_attachment:{font.Index}", $"{fonts}/font-{font.Index}{FontExtension(font)}"]);
            arguments.AddRange(["-i", file]);
            foreach (var s in texts) arguments.AddRange(["-map", $"0:{s.Index}", "-c:s", s.Styled ? "copy" : s.Codec == "webvtt" ? "webvtt" : "srt", files[s.Index]]);
            var result = await FfmpegService.Run(tools.Ffmpeg, arguments, lifetime.ApplicationStopping, TimeSpan.FromMinutes(10), folder);
            if (result.ExitCode != 0 && files.Values.All(f => !File.Exists(Path.Combine(folder, f)))) throw new InvalidOperationException("提取内封字幕失败：" + FfmpegService.Tail(result.Error));
            return new ExtractedSubtitles(files.Where(f => File.Exists(Path.Combine(folder, f.Value))).ToDictionary(), fonts);
        }, ct);
    }

    /// <summary>One embedded text subtitle as WebVTT.</summary>
    public async Task<string> Subtitle(string episodeId, int streamIndex, CancellationToken ct)
    {
        var extracted = await Subtitles(episodeId, ct);
        if (!extracted.Files.TryGetValue(streamIndex, out var name)) throw new FileNotFoundException("字幕不存在");
        var path = Path.Combine(Folder(playback.Resolve(episodeId).Episode.Id), name);
        return Services.Subtitles.ToVtt(await File.ReadAllBytesAsync(path, ct), Path.GetExtension(path));
    }

    public string Folder(string episodeId) => Path.Combine(library.MediaDirectory, episodeId);

    private async Task<T> Shared<T>(string episodeId, string kind, string file, Func<Task<T>> compute, CancellationToken ct) where T : class
    {
        var source = new FileInfo(file);
        var cache = Path.Combine(Folder(episodeId), kind + ".json");
        if (Read<T>(cache, source) is { } hit) return hit;
        var key = episodeId + ":" + kind;
        var work = running.GetOrAdd(key, name => new Lazy<Task<object>>(async () =>
        {
            try
            {
                Directory.CreateDirectory(Folder(episodeId));
                var value = await compute();
                var temp = cache + ".tmp";
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Cached<T>(source.Length, source.LastWriteTimeUtc, value), Json));
                AtomicFile.Replace(temp, cache);
                return value;
            }
            finally { running.TryRemove(key, out var _); }
        }));
        return (T)await work.Value.WaitAsync(ct);
    }

    private static T? Read<T>(string cache, FileInfo source) where T : class
    {
        try
        {
            if (!File.Exists(cache)) return null;
            var entry = JsonSerializer.Deserialize<Cached<T>>(File.ReadAllText(cache), Json);
            return entry != null && entry.Size == source.Length && entry.Modified == new DateTimeOffset(source.LastWriteTimeUtc) ? entry.Value : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static async Task<MediaInfo> Probe(string ffprobe, string file)
    {
        var result = await FfmpegService.Run(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", file], CancellationToken.None, TimeSpan.FromMinutes(2));
        if (result.ExitCode != 0) throw new InvalidOperationException("ffprobe 无法读取这个视频：" + FfmpegService.Tail(result.Error));
        return ParseProbe(JsonNode.Parse(result.Output) ?? throw new InvalidDataException("ffprobe 没有输出"), Path.GetExtension(file));
    }

    public static MediaInfo ParseProbe(JsonNode json, string extension)
    {
        var streams = (json["streams"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var format = json["format"];
        string Text(JsonNode? node) => JsonText.String(node);
        bool Flag(JsonObject s, string name) => JsonText.Number(s["disposition"]?[name]) == 1;
        MediaVideo? video = null;
        // Cover art in MP4 / MKV is a video stream too; skip it.
        if (streams.FirstOrDefault(s => Text(s["codec_type"]) == "video" && !Flag(s, "attached_pic")) is { } v)
        {
            var codec = Text(v["codec_name"]);
            var profile = Text(v["profile"]);
            var level = (int)(JsonText.Number(v["level"]) ?? 0);
            var pixel = Text(v["pix_fmt"]);
            var depth = pixel.Contains("12") ? 12 : pixel.Contains("10") ? 10 : int.TryParse(Text(v["bits_per_raw_sample"]), out var bits) && bits > 8 ? bits : 8;
            video = new((int)(JsonText.Number(v["index"]) ?? 0), codec, profile, level, pixel, depth, (int)(JsonText.Number(v["width"]) ?? 0), (int)(JsonText.Number(v["height"]) ?? 0),
                Rate(Text(v["avg_frame_rate"])) is > 0 and var avg ? avg : Rate(Text(v["r_frame_rate"])), Text(v["field_order"]) is "tt" or "bb" or "tb" or "bt",
                VideoCodecString(codec, profile, level, depth));
        }
        var audio = streams.Where(s => Text(s["codec_type"]) == "audio").Select(s =>
        {
            var codec = Text(s["codec_name"]);
            var language = Text(s["tags"]?["language"]);
            var title = Text(s["tags"]?["title"]);
            return new MediaAudio((int)(JsonText.Number(s["index"]) ?? 0), codec, Text(s["profile"]), (int)(JsonText.Number(s["channels"]) ?? 2), language, title, Flag(s, "default"),
                AudioCodecString(codec, Text(s["profile"])), title.Length > 0 ? title : LanguageName(language));
        }).ToList();
        var subtitles = streams.Where(s => Text(s["codec_type"]) == "subtitle").Select(s =>
        {
            var codec = Text(s["codec_name"]);
            var language = Text(s["tags"]?["language"]);
            var title = Text(s["tags"]?["title"]);
            var described = Services.Subtitles.Describe(title);
            var label = title.Length == 0 ? LanguageName(language) : described.Rank < 4 ? described.Label : title;
            return new MediaSubtitle((int)(JsonText.Number(s["index"]) ?? 0), codec, language, title, Flag(s, "default"), Flag(s, "forced"),
                TextCodecs.Contains(codec), codec is "ass" or "ssa", ImageCodecs.Contains(codec), label);
        }).ToList();
        var fonts = streams.Where(s => Text(s["codec_type"]) == "attachment").Select(s => new MediaAttachment((int)(JsonText.Number(s["index"]) ?? 0), Text(s["tags"]?["filename"]), Text(s["tags"]?["mimetype"])))
            .Where(IsFont).ToList();
        var formatName = Text(format?["format_name"]);
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var container = formatName.Split(',')[0] switch
        {
            "matroska" => ext == "webm" ? "webm" : "mkv",
            "mov" => ext is "mov" or "m4v" ? ext : "mp4",
            "mpegts" => "ts",
            "asf" => "wmv",
            "rm" => "rmvb",
            var other => other
        };
        return new(container, double.TryParse(Text(format?["duration"]), CultureInfo.InvariantCulture, out var duration) ? duration : 0,
            long.TryParse(Text(format?["bit_rate"]), out var rate) ? rate : 0, video, audio, subtitles, fonts);
    }

    private static double Rate(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && b > 0 ? Math.Round(a / b, 3) : 0;
    }

    private static bool IsFont(MediaAttachment a) =>
        a.MimeType.Contains("font", StringComparison.OrdinalIgnoreCase) || a.MimeType.Contains("opentype", StringComparison.OrdinalIgnoreCase) || FontExtensions.Contains(Path.GetExtension(a.FileName).ToLowerInvariant());

    private static string FontExtension(MediaAttachment a) =>
        FontExtensions.Contains(Path.GetExtension(a.FileName).ToLowerInvariant()) ? Path.GetExtension(a.FileName).ToLowerInvariant() : a.MimeType.Contains("otf") || a.MimeType.Contains("opentype") ? ".otf" : ".ttf";

    /// <summary>RFC 6381 codec names for H.264, HEVC, AV1 and VP9; other codecs (MPEG-2, VC-1, RealVideo…) always need transcoding.</summary>
    public static string VideoCodecString(string codec, string profile, int level, int depth) => codec switch
    {
        "h264" => "avc1." + (profile switch { "Constrained Baseline" => "42E0", "Baseline" => "4200", "Main" => "4D00", "Extended" => "5800", "High" => "6400", "High 10" or "High 10 Intra" => "6E00", "High 4:2:2" or "High 4:2:2 Intra" => "7A00", _ => "F400" })
            + Math.Clamp(level > 0 ? level : 40, 10, 62).ToString("X2"),
        // Profile Main 10 also decodes Main; "hvc1" stores the parameter sets in the sample entry, as Safari requires.
        "hevc" => profile switch { "Main" => "hvc1.1.6", "Main 10" => "hvc1.2.4", "Main Still Picture" => "hvc1.3.E", _ => "hvc1.4.10" } + ".L" + (level > 0 ? level : 120) + ".B0",
        "av1" => "av01." + (profile switch { "High" => 1, "Professional" => 2, _ => 0 }) + "." + Math.Clamp(level, 0, 31).ToString("00") + "M." + depth.ToString("00"),
        "vp9" => "vp09." + (profile.StartsWith("Profile ") && int.TryParse(profile[8..], out var p) ? p : depth > 8 ? 2 : 0).ToString("00") + ".40." + depth.ToString("00"),
        _ => ""
    };

    /// <summary>Audio codecs browsers accept in MP4; DTS, TrueHD, PCM and Vorbis are transcoded to AAC.</summary>
    public static string AudioCodecString(string codec, string profile) => codec switch
    {
        "aac" => profile switch { "HE-AAC" => "mp4a.40.5", "HE-AACv2" => "mp4a.40.29", _ => "mp4a.40.2" },
        "mp3" => "mp4a.6B",
        "opus" => "opus",
        "flac" => "flac",
        "ac3" => "ac-3",
        "eac3" => "ec-3",
        "alac" => "alac",
        _ => ""
    };

    public static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "chi" or "zho" or "chs" or "zh" => "中文",
        "jpn" or "ja" => "日本語",
        "eng" or "en" => "English",
        "kor" or "ko" => "한국어",
        "" or "und" => "未标注语言",
        var other => other
    };

    /// <summary>ffprobe packet listing ("pts_time,flags" per line) → sorted distinct keyframe times.</summary>
    public static double[] ParseKeyframes(string csv) => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(','))
        .Where(parts => parts.Length >= 2 && parts[1].Contains('K') && double.TryParse(parts[0], CultureInfo.InvariantCulture, out _))
        .Select(parts => double.Parse(parts[0], CultureInfo.InvariantCulture))
        .Distinct().Order().ToArray();
}
