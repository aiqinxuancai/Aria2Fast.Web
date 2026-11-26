using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>Remux keeps the picture (Copy); transcode encodes H.264. Paths of burned-in subtitles are relative to the session folder.</summary>
public sealed record HlsPlan(string File, bool Copy, int VideoIndex, string VideoCodec, int SourceHeight, bool Interlaced, int? AudioIndex, bool AudioCopy, int Height,
    string? SubtitleFile, string? FontsDirectory, int? ImageSubtitle, double[] Starts, double Duration);

/// <summary>Start is where the player begins (seconds), so the first job starts there rather than at the beginning.</summary>
public sealed record HlsRequest(string Mode, int? Audio = null, bool AudioCopy = true, int Height = 0, string? Burn = null, double Start = 0);

/// <summary>
/// HLS (fMP4 segments) for videos the browser cannot play directly. Segments are produced on demand by one ffmpeg job per
/// session; a request outside what the job is about to produce restarts it there. Segment boundaries are fixed in advance so a
/// restarted job's segments line up with the playlist: transcoding forces a keyframe every few seconds, and remuxing cuts at every
/// source keyframe (one GOP per segment), which keeps the numbering independent of where the job started.
/// </summary>
public sealed partial class HlsService(StateStore store, LibraryStore library, FfmpegService ffmpeg, MediaAnalyzer media, PlaybackService playback, ILogger<HlsService> logger) : BackgroundService
{
    public const double SegmentSeconds = 4;
    private const double AheadSeconds = 240;      // stop a job this far ahead of the player…
    private const double LowWaterSeconds = 60;    // …and restart it when the player is this close to the end of what exists
    private const double KeepBehindSeconds = 600; // segments further behind are deleted to bound disk use
    private const double WaitAheadSeconds = 12;   // a request this close ahead of a running job waits instead of restarting it
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Session> sessions = new();

    private sealed class Session(string id, string episodeId, HlsPlan plan, string directory, string ffmpeg, string encoder)
    {
        public string Id { get; } = id;
        public string EpisodeId { get; } = episodeId;
        public HlsPlan Plan { get; } = plan;
        public string Directory { get; } = directory;
        public string Ffmpeg { get; } = ffmpeg;
        public string Encoder { get; } = encoder;
        public object Gate { get; } = new();
        public Job? Job { get; set; }
        public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.UtcNow;
        public int LastRequested { get; set; }
        public int PrunedBelow { get; set; }
        public string? Init { get; set; }
    }

    private sealed class Job(Process process, int start)
    {
        private readonly Queue<string> errors = new();
        public Process Process { get; } = process;
        public int Start { get; } = start;
        public int Frontier { get; set; } = start - 1;
        public volatile bool Exited;
        public DateTimeOffset ExitTime { get; set; }
        public void Error(string line) { lock (errors) { errors.Enqueue(line); if (errors.Count > 20) errors.Dequeue(); } }
        public string Errors() { lock (errors) return string.Join('\n', errors); }
    }

    public async Task<object> Create(string episodeId, HlsRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("copy" or "transcode")) throw new ArgumentException("播放方式无效");
        var tools = await ffmpeg.Ready(ct) ?? throw new ArgumentException("未启用转码，或没有找到 ffmpeg");
        var info = await media.Info(episodeId, ct) ?? throw new ArgumentException("未启用转码，或没有找到 ffmpeg");
        var (episode, file) = playback.Resolve(episodeId);
        if (info.Video == null) throw new ArgumentException("这个文件没有视频画面");
        if (info.Duration <= 0) throw new ArgumentException("读不出视频时长，无法转码播放");
        var copy = request.Mode == "copy";
        if (copy && info.Video.CodecString.Length == 0) throw new ArgumentException("浏览器无法解码这种画面编码，请使用转码");
        var audio = request.Audio is int wanted ? info.Audio.FirstOrDefault(a => a.Index == wanted) ?? throw new ArgumentException("音轨不存在")
            : info.Audio.FirstOrDefault(a => a.Default) ?? info.Audio.FirstOrDefault();
        var settings = store.Read().Settings.Library;
        if (!copy && !tools.Encoders.Contains(settings.VideoEncoder)) throw new ArgumentException("当前 ffmpeg 没有编码器 " + settings.VideoEncoder + "，请在设置中更换");
        if (request.Height is < 0 or > 4320) throw new ArgumentException("分辨率无效");
        var id = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..16];
        var directory = Path.Combine(library.TranscodeDirectory, id);
        Directory.CreateDirectory(directory);
        try
        {
            string? subtitle = null, fonts = null;
            int? image = null;
            if (request.Burn is { } burn)
            {
                if (copy) throw new ArgumentException("烧录字幕需要转码");
                (subtitle, fonts, image) = await Burn(burn, episode, file, info, directory, ct);
            }
            var limit = settings.MaxHeight > 0 ? settings.MaxHeight : int.MaxValue;
            var height = request.Height > 0 ? Math.Min(request.Height, limit) : settings.MaxHeight;
            var starts = Segments(copy, copy ? await media.Keyframes(episodeId, ct) : [], info.Duration);
            var plan = new HlsPlan(file, copy, info.Video.Index, info.Video.Codec, info.Video.Height, info.Video.Interlaced, audio?.Index,
                copy && request.AudioCopy && audio is { CodecString.Length: > 0 }, height, subtitle, fonts, image, starts, info.Duration);
            sessions[id] = new Session(id, episode.Id, plan, directory, tools.Ffmpeg, settings.VideoEncoder)
            {
                LastRequested = double.IsFinite(request.Start) ? Math.Max(0, Array.FindLastIndex(starts, s => s <= request.Start + 0.001)) : 0
            };
            logger.LogInformation("HLS 会话 {Id}：{Mode} {File}", id, request.Mode, Path.GetFileName(file));
            return new
            {
                id, playlist = $"/api/library/hls/{id}/index.m3u8", mode = request.Mode, segments = starts.Length, duration = info.Duration,
                height = copy ? info.Video.Height : OutputHeight(plan), encoder = copy ? "copy" : settings.VideoEncoder, audioCopy = plan.AudioCopy, audio = audio?.Index
            };
        }
        catch { TryDelete(directory); throw; }
    }

    /// <summary>Embedded ASS / SRT is burned from the extracted copy (with the file's fonts), image subtitles with an overlay, sidecar files from a copy in the session folder.</summary>
    private async Task<(string? Subtitle, string? Fonts, int? Image)> Burn(string burn, LibraryEpisode episode, string file, MediaInfo info, string directory, CancellationToken ct)
    {
        var parts = burn.Split(':');
        if (parts is ["embedded", var value] && int.TryParse(value, out var index))
        {
            var stream = info.Subtitles.FirstOrDefault(s => s.Index == index) ?? throw new ArgumentException("字幕不存在");
            if (stream.Image) return (null, null, stream.Index);
            if (!stream.Text) throw new ArgumentException("不支持烧录这种字幕");
            var extracted = await media.Subtitles(episode.Id, ct);
            if (!extracted.Files.TryGetValue(index, out var name)) throw new InvalidOperationException("内封字幕提取失败");
            var folder = media.Folder(episode.Id);
            return (Relative(directory, Path.Combine(folder, name)), extracted.Fonts == null ? null : Relative(directory, Path.Combine(folder, extracted.Fonts)), null);
        }
        if (parts is ["external", var number] && int.TryParse(number, out var position))
        {
            var tracks = Subtitles.Find(file, episode.Number);
            if (position < 0 || position >= tracks.Count) throw new ArgumentException("字幕不存在");
            var name = "external" + Path.GetExtension(tracks[position].Path).ToLowerInvariant();
            File.Copy(tracks[position].Path, Path.Combine(directory, name), true);
            return (name, null, null);
        }
        throw new ArgumentException("烧录字幕参数无效");
    }

    /// <summary>
    /// ffmpeg runs in the session folder, so filter arguments only hold relative paths made of safe characters and need no
    /// filtergraph escaping. Anything else (another drive, unusual characters in the data folder) is refused.
    /// </summary>
    private static string Relative(string from, string to)
    {
        var relative = Path.GetRelativePath(from, to).Replace('\\', '/');
        return SafePath().IsMatch(relative) ? relative : throw new InvalidOperationException("数据目录路径包含特殊字符，无法烧录内封字幕");
    }

    public string Playlist(string id) => Playlist(Get(id).Plan.Starts, Get(id).Plan.Duration);

    public async Task<string> Init(string id, CancellationToken ct)
    {
        var session = Get(id);
        session.LastAccess = DateTimeOffset.UtcNow;
        if (session.Init is { } ready && File.Exists(ready)) return ready;
        lock (session.Gate) if (session.Job is not { Exited: false }) StartJob(session, session.LastRequested);
        // The init file is complete once the job has finished its first segment.
        string? Ready() => Directory.EnumerateFiles(session.Directory, "init-*.mp4").FirstOrDefault() is { } init && Directory.EnumerateFiles(session.Directory, "*.m4s").Any() ? init : null;
        await Wait(session, () => Ready() != null, session.LastRequested, ct);
        return session.Init = Ready()!;
    }

    public async Task<string> Segment(string id, int index, CancellationToken ct)
    {
        var session = Get(id);
        if (index < 0 || index >= session.Plan.Starts.Length) throw new FileNotFoundException("分段不存在");
        session.LastAccess = DateTimeOffset.UtcNow;
        session.LastRequested = index;
        var path = SegmentPath(session, index);
        if (!File.Exists(path)) lock (session.Gate) Ensure(session, index);
        await Wait(session, () => File.Exists(path), index, ct);
        lock (session.Gate) Prepare(session, index);
        return path;
    }

    /// <summary>
    /// Polls until ready. A job stopped meanwhile (another player needed the slot, or it ran far ahead) is restarted;
    /// a failed job, or one that ended without producing the segment, is reported with ffmpeg's last error lines.
    /// </summary>
    private async Task Wait(Session session, Func<bool> ready, int index, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var action = session.Plan.Copy ? "转封装" : "转码";
        while (!ready())
        {
            lock (session.Gate)
            {
                var job = session.Job;
                if (job == null || (job.Exited && job.Start > index)) Ensure(session, index);
                else if (job.Exited && !ready())
                {
                    if (job.Process.ExitCode != 0) throw new InvalidOperationException(action + "失败：" + FfmpegService.Tail(job.Errors()));
                    if (DateTimeOffset.UtcNow - job.ExitTime > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(action + "提前结束，没有生成所需的分段：" + FfmpegService.Tail(job.Errors()));
                }
            }
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(action + "超时，设备可能太慢，可以降低分辨率或改用外部播放器");
            await Task.Delay(100, ct);
        }
    }

    public void Stop(string id)
    {
        if (!sessions.TryRemove(id, out var session)) return;
        lock (session.Gate) StopJob(session);
        TryDelete(session.Directory);
        logger.LogInformation("HLS 会话 {Id} 结束", id);
    }

    private Session Get(string id) => sessions.TryGetValue(id, out var session) ? session : throw new FileNotFoundException("播放会话已结束，请重新打开视频");

    private static string SegmentPath(Session session, int index) => Path.Combine(session.Directory, index.ToString(CultureInfo.InvariantCulture) + ".m4s");

    /// <summary>Called under the session lock when a requested segment does not exist yet.</summary>
    private void Ensure(Session session, int index)
    {
        if (session.Job is { Exited: false } job && job.Start <= index)
        {
            var frontier = Frontier(session, job);
            if (index <= frontier + 1 || session.Plan.Starts[index] - session.Plan.Starts[Math.Min(frontier + 1, index)] <= WaitAheadSeconds) return;
        }
        StartJob(session, index);
    }

    /// <summary>After serving a segment: delete segments far behind, and keep a job running when the player nears the end of what exists.</summary>
    private void Prepare(Session session, int index)
    {
        var starts = session.Plan.Starts;
        for (var i = session.PrunedBelow; i < index && starts[index] - starts[i] > KeepBehindSeconds; i++)
        {
            try { File.Delete(SegmentPath(session, i)); } catch (IOException) { }
            session.PrunedBelow = i + 1;
        }
        for (var next = index + 1; next < starts.Length && starts[next] - starts[index] <= LowWaterSeconds; next++)
        {
            if (File.Exists(SegmentPath(session, next))) continue;
            if (session.Job is not { Exited: false } job || job.Start > next) StartJob(session, next);
            return;
        }
    }

    private static int Frontier(Session session, Job job)
    {
        while (job.Frontier + 1 < session.Plan.Starts.Length && File.Exists(SegmentPath(session, job.Frontier + 1))) job.Frontier++;
        return job.Frontier;
    }

    private void StartJob(Session session, int start)
    {
        StopJob(session);
        var settings = store.Read().Settings.Library;
        if (!session.Plan.Copy)
        {
            // Only transcodes count against the limit; remuxing costs little CPU.
            var busy = sessions.Values.Where(s => s != session && !s.Plan.Copy && s.Job is { Exited: false }).ToList();
            if (busy.Count >= Math.Max(1, settings.MaxTranscodes))
            {
                var idle = busy.Where(s => DateTimeOffset.UtcNow - s.LastAccess > TimeSpan.FromSeconds(30)).MinBy(s => s.LastAccess)
                    ?? throw new InvalidOperationException($"同时转码的视频已达上限（{settings.MaxTranscodes} 个），请先关闭其他正在转码的播放");
                lock (idle.Gate) StopJob(idle);
            }
        }
        var process = new Process
        {
            StartInfo = FfmpegService.StartInfo(session.Ffmpeg, Arguments(session.Plan, start, session.Encoder, settings.VaapiDevice), session.Directory, captureOutput: false),
            EnableRaisingEvents = true
        };
        var job = new Job(process, start);
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) job.Error(e.Data); };
        process.Exited += (_, _) => { job.ExitTime = DateTimeOffset.UtcNow; job.Exited = true; };
        process.Start();
        process.BeginErrorReadLine();
        FfmpegService.LowerPriority(process);
        session.Job = job;
        logger.LogDebug("HLS 会话 {Id} 从分段 {Start} 开始", session.Id, start);
    }

    private static void StopJob(Session session)
    {
        if (session.Job is not { } job) return;
        session.Job = null;
        FfmpegService.Kill(job.Process);
        job.Process.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leftovers of sessions from before a restart.
        if (Directory.Exists(library.TranscodeDirectory)) foreach (var folder in Directory.EnumerateDirectories(library.TranscodeDirectory)) TryDelete(folder);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
            foreach (var session in sessions.Values)
            {
                if (DateTimeOffset.UtcNow - session.LastAccess > IdleTimeout) { Stop(session.Id); continue; }
                lock (session.Gate)
                {
                    // Far enough ahead of the player: stop and let Prepare restart it later, instead of converting the whole video at once.
                    if (session.Job is { Exited: false } job && Frontier(session, job) is var frontier && frontier > session.LastRequested
                        && session.Plan.Starts[frontier] - session.Plan.Starts[session.LastRequested] > AheadSeconds) StopJob(session);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var id in sessions.Keys) Stop(id);
        await base.StopAsync(cancellationToken);
    }

    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    /// <summary>
    /// Segment start times. Transcoding: every SegmentSeconds. Remuxing: every keyframe after the first (ffmpeg starts the first
    /// segment at the first keyframe, wherever it is), matching ffmpeg's hls muxer with a tiny hls_time, which splits at each one.
    /// </summary>
    public static double[] Segments(bool copy, IReadOnlyList<double> keyframes, double duration)
    {
        if (!copy) return Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling(duration / SegmentSeconds - 1e-6))).Select(i => i * SegmentSeconds).ToArray();
        var starts = new List<double> { 0 };
        foreach (var keyframe in keyframes.Skip(1)) if (keyframe > starts[^1] + 0.0005 && keyframe < duration) starts.Add(keyframe);
        return starts.ToArray();
    }

    public static string Playlist(double[] starts, double duration)
    {
        var lengths = starts.Select((start, i) => Math.Max(0.001, (i + 1 < starts.Length ? starts[i + 1] : duration) - start)).ToArray();
        var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n");
        text.Append("#EXT-X-TARGETDURATION:").Append((int)Math.Ceiling(lengths.Max())).Append('\n');
        text.Append("#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MAP:URI=\"init.mp4\"\n");
        for (var i = 0; i < lengths.Length; i++) text.Append("#EXTINF:").Append(lengths[i].ToString("0.000000", CultureInfo.InvariantCulture)).Append(",\n").Append(i).Append(".m4s\n");
        return text.Append("#EXT-X-ENDLIST\n").ToString();
    }

    /// <summary>
    /// <para>The job writes "{n}.m4s" (renamed from a temp file when complete) and "init-{start}.mp4" into its working folder.</para>
    /// <para>-copyts keeps the source timeline (shifted only if it would start below zero, as AAC priming does), so burned-in subtitles stay in sync after a seek, and the MP4 muxer's frag_discont
    /// writes that timeline into each fragment (without it a restarted job's segments would start again at zero). Remuxing seeks
    /// to the segment's keyframe on input and drops anything earlier on output, in case the container index lands before it; the
    /// output seek shifts timestamps, which -output_ts_offset undoes. Transcoding seeks accurately and forces a keyframe at each
    /// segment start.</para>
    /// </summary>
    public static List<string> Arguments(HlsPlan plan, int start, string encoder, string vaapiDevice)
    {
        var at = plan.Starts[start];
        var a = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (!plan.Copy) a.AddRange(EncoderInput(encoder, vaapiDevice));
        if (start > 0) a.AddRange(["-ss", Seconds(at)]);
        a.AddRange(["-i", plan.File]);
        if (plan.Copy)
        {
            if (start > 0) a.AddRange(["-ss", Seconds(Math.Max(0, at - 0.001)), "-output_ts_offset", Seconds(Math.Max(0, at - 0.001))]);
            a.AddRange(["-map", $"0:{plan.VideoIndex}", "-c:v", "copy"]);
            if (plan.VideoCodec == "hevc") a.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var chain = string.Join(',', VideoFilters(plan, encoder));
            if (plan.ImageSubtitle is int subtitle) a.AddRange(["-filter_complex", $"[0:{plan.VideoIndex}][0:{subtitle}]overlay=eof_action=pass,{chain}[v]", "-map", "[v]"]);
            else a.AddRange(["-map", $"0:{plan.VideoIndex}", "-vf", chain]);
            a.AddRange(VideoEncoding(encoder, OutputHeight(plan)));
            a.AddRange(["-force_key_frames", $"expr:gte(t,{Seconds(at)}+n_forced*{Seconds(SegmentSeconds)})"]);
        }
        if (plan.AudioIndex is int audio)
        {
            a.AddRange(["-map", $"0:{audio}"]);
            a.AddRange(plan.AudioCopy ? ["-c:a", "copy"] : ["-c:a", "aac", "-ac", "2", "-b:a", "192k"]);
        }
        a.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-copyts", "-avoid_negative_ts", "make_non_negative", "-max_muxing_queue_size", "4096",
            "-f", "hls", "-hls_time", plan.Copy ? "0.001" : Seconds(SegmentSeconds), "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", $"init-{start}.mp4",
            "-start_number", start.ToString(CultureInfo.InvariantCulture), "-hls_segment_filename", "%d.m4s", "-hls_flags", "temp_file+independent_segments",
            "-hls_segment_options", "movflags=+frag_discont", "-hls_playlist_type", "vod", "-hls_list_size", "0", $"job-{start}.m3u8"]);
        return a;
    }

    public static int OutputHeight(HlsPlan plan)
    {
        var height = plan.Height > 0 && plan.SourceHeight > plan.Height ? plan.Height : plan.SourceHeight > 0 ? plan.SourceHeight : 1080;
        return height / 2 * 2;
    }

    private static List<string> VideoFilters(HlsPlan plan, string encoder)
    {
        var filters = new List<string>();
        if (plan.Interlaced) filters.Add("bwdif=mode=send_frame:deint=interlaced");
        // Rendered at the source size, before scaling, for sharper text.
        if (plan.SubtitleFile != null) filters.Add("subtitles=filename=" + plan.SubtitleFile + (plan.FontsDirectory != null ? ":fontsdir=" + plan.FontsDirectory : ""));
        filters.Add($"scale=-2:{OutputHeight(plan)}");
        filters.AddRange(EncoderFormat(encoder));
        return filters;
    }

    public static string[] EncoderInput(string encoder, string vaapiDevice) => encoder == "h264_vaapi" ? ["-vaapi_device", vaapiDevice] : [];

    /// <summary>Frames are decoded in software; hardware encoders take NV12 (uploaded for VAAPI), libx264 8-bit 4:2:0.</summary>
    public static string[] EncoderFormat(string encoder) => encoder switch
    {
        "h264_vaapi" => ["format=nv12", "hwupload"],
        "h264_qsv" => ["format=nv12"],
        _ => ["format=yuv420p"]
    };

    public static string[] VideoEncoding(string encoder, int height)
    {
        var rate = height <= 480 ? 1500 : height <= 720 ? 3000 : height <= 1080 ? 6000 : 12000;
        string K(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + "k";
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", K(rate / 2), "-maxrate", K(rate), "-bufsize", K(rate * 2), "-profile:v", "high", "-forced-idr", "1"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "23", "-maxrate", K(rate), "-bufsize", K(rate * 2), "-profile:v", "high"],
            "h264_vaapi" => ["-c:v", "h264_vaapi", "-rc_mode", "VBR", "-b:v", K(rate / 2), "-maxrate", K(rate), "-profile:v", "high"],
            "h264_videotoolbox" => ["-c:v", "h264_videotoolbox", "-b:v", K(rate * 2 / 3), "-profile:v", "high", "-realtime", "1"],
            "h264_amf" => ["-c:v", "h264_amf", "-quality", "speed", "-rc", "vbr_peak", "-b:v", K(rate / 2), "-maxrate", K(rate), "-profile:v", "high"],
            _ => ["-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-maxrate", K(rate), "-bufsize", K(rate * 2), "-profile:v", "high", "-forced-idr", "1"]
        };
    }

    private static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Za-z0-9._/\-]+$")] private static partial Regex SafePath();
}
