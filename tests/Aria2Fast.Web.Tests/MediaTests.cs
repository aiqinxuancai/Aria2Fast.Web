using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;

static class MediaTests
{
    static void Check(bool condition, string message = "Media assertion failed") { if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

    // Shaped like `ffprobe -print_format json -show_format -show_streams` for a fansub MKV: cover art, HEVC Main 10,
    // two audio tracks, ASS and PGS subtitles and a font attachment.
    const string Probe = """
        {"streams":[
          {"index":0,"codec_name":"mjpeg","codec_type":"video","width":600,"height":800,"disposition":{"default":0,"attached_pic":1}},
          {"index":1,"codec_name":"hevc","profile":"Main 10","codec_type":"video","width":1920,"height":1080,"pix_fmt":"yuv420p10le","level":120,"field_order":"progressive","r_frame_rate":"24000/1001","avg_frame_rate":"24000/1001","disposition":{"default":1,"attached_pic":0},"tags":{"language":"jpn"}},
          {"index":2,"codec_name":"flac","codec_type":"audio","channels":2,"disposition":{"default":1},"tags":{"language":"jpn"}},
          {"index":3,"codec_name":"aac","profile":"LC","codec_type":"audio","channels":6,"disposition":{"default":0},"tags":{"language":"chi","title":"国语"}},
          {"index":4,"codec_name":"ass","codec_type":"subtitle","disposition":{"default":1,"forced":0},"tags":{"language":"chi","title":"CHS&JPN"}},
          {"index":5,"codec_name":"subrip","codec_type":"subtitle","disposition":{"default":0},"tags":{"language":"eng"}},
          {"index":6,"codec_name":"hdmv_pgs_subtitle","codec_type":"subtitle","disposition":{"default":0,"forced":1},"tags":{"language":"jpn"}},
          {"index":7,"codec_name":"ttf","codec_type":"attachment","tags":{"filename":"FOT-RodinPro.otf","mimetype":"application/vnd.ms-opentype"}},
          {"index":8,"codec_name":"none","codec_type":"attachment","tags":{"filename":"cover.jpg","mimetype":"image/jpeg"}}],
         "format":{"format_name":"matroska,webm","duration":"1420.5","bit_rate":"4800000"}}
        """;

    public static int Run()
    {
        var count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }

        Test("ffprobe details: cover art skipped, codecs, bit depth, tracks, subtitle kinds and fonts", () =>
        {
            var info = MediaAnalyzer.ParseProbe(JsonNode.Parse(Probe)!, ".mkv");
            Check(info.Container == "mkv" && info.Duration == 1420.5 && info.BitRate == 4800000);
            Check(info.Video is { Index: 1, Codec: "hevc", BitDepth: 10, Width: 1920, Height: 1080, FrameRate: 23.976, Interlaced: false, CodecString: "hvc1.2.4.L120.B0" }, info.Video?.ToString() ?? "");
            Check(info.Audio.Select(a => (a.Index, a.CodecString, a.Default, a.Label)).SequenceEqual([(2, "flac", true, "日本語"), (3, "mp4a.40.2", false, "国语")]));
            var subs = info.Subtitles;
            Check(subs[0] is { Index: 4, Text: true, Styled: true, Image: false, Label: "简日双语" } && subs[1] is { Text: true, Styled: false, Label: "English" } && subs[2] is { Text: false, Image: true, Forced: true, Label: "日本語" }, string.Join(" | ", subs));
            Check(info.Fonts.Count == 1 && info.Fonts[0].Index == 7);
            Check(MediaAnalyzer.ParseProbe(JsonNode.Parse("""{"streams":[],"format":{"format_name":"matroska,webm","duration":"N/A"}}""")!, ".webm") is { Container: "webm", Duration: 0, Video: null });
            Check(MediaAnalyzer.ParseProbe(JsonNode.Parse("""{"streams":[],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2"}}""")!, ".mp4").Container == "mp4");
            Check(MediaAnalyzer.ParseProbe(JsonNode.Parse("""{"streams":[{"index":0,"codec_type":"video","codec_name":"mpeg2video","field_order":"tt","height":1080}],"format":{"format_name":"mpegts"}}""")!, ".ts") is { Container: "ts", Video: { Interlaced: true, CodecString: "" } });
        });

        Test("RFC 6381 codec names for browser capability checks", () =>
        {
            Check(MediaAnalyzer.VideoCodecString("h264", "High", 41, 8) == "avc1.640029");
            Check(MediaAnalyzer.VideoCodecString("h264", "Constrained Baseline", 30, 8) == "avc1.42E01E");
            Check(MediaAnalyzer.VideoCodecString("h264", "High 10", 51, 10) == "avc1.6E0033");
            Check(MediaAnalyzer.VideoCodecString("hevc", "Main", 153, 8) == "hvc1.1.6.L153.B0");
            Check(MediaAnalyzer.VideoCodecString("av1", "Main", 8, 10) == "av01.0.08M.10");
            Check(MediaAnalyzer.VideoCodecString("vp9", "Profile 0", -99, 8) == "vp09.00.40.08");
            Check(MediaAnalyzer.VideoCodecString("mpeg2video", "Main", 8, 8) == "");
            Check(MediaAnalyzer.AudioCodecString("aac", "HE-AAC") == "mp4a.40.5" && MediaAnalyzer.AudioCodecString("eac3", "") == "ec-3" && MediaAnalyzer.AudioCodecString("dts", "DTS-HD MA") == "");
        });

        Test("Keyframes from ffprobe packets; segment boundaries for remux and transcode", () =>
        {
            var keyframes = MediaAnalyzer.ParseKeyframes("0.042000,K__\n0.083000,___\n2.000000,K_\n2.000000,K_\nN/A,K__\n4.500000,K__\n9.990000,K__\n");
            Check(keyframes.SequenceEqual([0.042, 2.0, 4.5, 9.99]), string.Join(",", keyframes));
            // The first keyframe opens segment 0 wherever it is; each later keyframe starts a segment.
            Check(HlsService.Segments(true, keyframes, 10).SequenceEqual([0, 2.0, 4.5, 9.99]));
            Check(HlsService.Segments(true, [0], 10).SequenceEqual([0d]));
            Check(HlsService.Segments(false, [], 10.5).SequenceEqual([0d, 4, 8]));
            Check(HlsService.Segments(false, [], 8).SequenceEqual([0d, 4]));
            Check(HlsService.Playlist([0, 2, 4.5], 10) == "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MAP:URI=\"init.mp4\"\n"
                + "#EXTINF:2.000000,\n0.m4s\n#EXTINF:2.500000,\n1.m4s\n#EXTINF:5.500000,\n2.m4s\n#EXT-X-ENDLIST\n");
        });

        Test("ffmpeg arguments: remux seeks to the keyframe, transcode forces aligned keyframes, burn-in and hardware encoders", () =>
        {
            HlsPlan Plan(bool copy, string codec = "hevc", int height = 0, string? subtitle = null, string? fonts = null, int? image = null, bool interlaced = false, bool audioCopy = true) =>
                new("/media/a b.mkv", copy, 1, codec, 1080, interlaced, 2, audioCopy, height, subtitle, fonts, image, copy ? [0, 2.0, 4.5] : [0, 4, 8], 10);
            string Join(List<string> a) => string.Join(" ", a);

            var first = Join(HlsService.Arguments(Plan(true), 0, "libx264", "/dev/dri/renderD128"));
            Check(!first.Contains("-ss") && first.Contains("-i /media/a b.mkv -map 0:1 -c:v copy -tag:v hvc1 -map 0:2 -c:a copy") && first.Contains("-hls_time 0.001") && first.Contains("-start_number 0"), first);
            var seek = HlsService.Arguments(Plan(true, "h264", audioCopy: false), 2, "libx264", "");
            var text = Join(seek);
            Check(text.StartsWith("-hide_banner -nostdin -v error -ss 4.5 -i /media/a b.mkv -ss 4.499 -output_ts_offset 4.499 -map 0:1 -c:v copy -map 0:2 -c:a aac -ac 2 -b:a 192k") && !text.Contains("hvc1"), text);
            Check(text.Contains("-copyts -avoid_negative_ts make_non_negative") && text.Contains("-hls_segment_type fmp4 -hls_fmp4_init_filename init-2.mp4 -start_number 2 -hls_segment_filename %d.m4s") && text.Contains("-hls_segment_options movflags=+frag_discont") && seek[^1] == "job-2.m3u8");
            Check(seek.Contains("/media/a b.mkv") && !seek.Contains("/media/a"), "the input path stays one argument");

            var encode = Join(HlsService.Arguments(Plan(false, height: 720), 1, "libx264", ""));
            Check(encode.Contains("-ss 4 -i /media/a b.mkv -map 0:1 -vf scale=-2:720,format=yuv420p -c:v libx264 -preset veryfast") && encode.Contains("-maxrate 3000k") && encode.Contains("-force_key_frames expr:gte(t,4+n_forced*4)") && encode.Contains("-hls_time 4 "), encode);
            Check(Join(HlsService.Arguments(Plan(false), 0, "libx264", "")).Contains("-vf scale=-2:1080,format=yuv420p"));
            var burned = Join(HlsService.Arguments(Plan(false, subtitle: "../../media/abc/sub-4.ass", fonts: "../../media/abc/fonts", interlaced: true), 0, "libx264", ""));
            Check(burned.Contains("-vf bwdif=mode=send_frame:deint=interlaced,subtitles=filename=../../media/abc/sub-4.ass:fontsdir=../../media/abc/fonts,scale=-2:1080,format=yuv420p"), burned);
            var overlay = HlsService.Arguments(Plan(false, image: 6), 0, "libx264", "");
            Check(Join(overlay).Contains("-filter_complex [0:1][0:6]overlay=eof_action=pass,scale=-2:1080,format=yuv420p[v] -map [v]") && !overlay.Contains("-vf"), Join(overlay));
            var vaapi = Join(HlsService.Arguments(Plan(false), 0, "h264_vaapi", "/dev/dri/renderD129"));
            Check(vaapi.StartsWith("-hide_banner -nostdin -v error -vaapi_device /dev/dri/renderD129 -i") && vaapi.Contains("format=nv12,hwupload -c:v h264_vaapi"), vaapi);
            Check(Join(HlsService.Arguments(Plan(false), 0, "h264_nvenc", "")).Contains("-c:v h264_nvenc") && Join(HlsService.Arguments(Plan(false), 0, "h264_qsv", "")).Contains("format=nv12 -c:v h264_qsv"));
            Check(HlsService.OutputHeight(Plan(false, height: 2160)) == 1080 && HlsService.OutputHeight(Plan(false, height: 480)) == 480);
        });

        var temporary = Path.Combine(Path.GetTempPath(), "aria2fast-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Test("ffmpeg is found from the configured file or folder, the application folder, then PATH", () =>
            {
                var exe = OperatingSystem.IsWindows() ? ".exe" : "";
                string Tools(string folder) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "ffmpeg" + exe), ""); File.WriteAllText(Path.Combine(folder, "ffprobe" + exe), ""); return folder; }
                var configured = Tools(Path.Combine(temporary, "custom"));
                var app = Path.Combine(temporary, "app");
                var onPath = Tools(Path.Combine(temporary, "path"));
                Directory.CreateDirectory(app);
                Check(FfmpegService.Locate(configured, app, onPath).Ffmpeg == Path.Combine(configured, "ffmpeg" + exe));
                Check(FfmpegService.Locate(Path.Combine(configured, "ffmpeg" + exe), app, onPath).Ffprobe == Path.Combine(configured, "ffprobe" + exe));
                Check(FfmpegService.Locate(Path.Combine(temporary, "missing"), app, onPath).Ffmpeg == null); // a configured location is not second-guessed
                Check(FfmpegService.Locate("", app, "\"" + onPath + "\"").Ffmpeg == Path.Combine(onPath, "ffmpeg" + exe));
                Tools(Path.Combine(app, "ffmpeg"));
                Check(FfmpegService.Locate("", app, onPath).Ffmpeg == Path.Combine(app, "ffmpeg", "ffmpeg" + exe));
                var half = Path.Combine(temporary, "half");
                Directory.CreateDirectory(half);
                File.WriteAllText(Path.Combine(half, "ffmpeg" + exe), "");
                Check(FfmpegService.Locate(half, app, null).Ffmpeg == null); // ffprobe is needed too
            });
        }
        finally { Directory.Delete(temporary, true); }

        Test("Encoder list parsing and transcoding settings validation", () =>
        {
            var listing = " V....D libx264              libx264 H.264 / AVC (codec h264)\n V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)\n A....D aac                  AAC (Advanced Audio Coding)\n V....D h264_vaapi_extra      not a real one\n";
            Check(FfmpegService.AvailableEncoders(listing).SequenceEqual(["libx264", "h264_nvenc"]));
            var settings = new LibrarySettings { FfmpegPath = "  \"C:\\Tools\\ffmpeg\\bin\"  ", VaapiDevice = " " };
            Validation.Transcoding(settings);
            Check(settings.FfmpegPath == "C:\\Tools\\ffmpeg\\bin" && settings.VaapiDevice == "/dev/dri/renderD128");
            Throws<ArgumentException>(() => Validation.Transcoding(new LibrarySettings { VideoEncoder = "libx265" }));
            Throws<ArgumentException>(() => Validation.Transcoding(new LibrarySettings { MaxHeight = 900 }));
            Throws<ArgumentException>(() => Validation.Transcoding(new LibrarySettings { MaxTranscodes = 0 }));
            Throws<ArgumentException>(() => Validation.Transcoding(new LibrarySettings { VaapiDevice = "/dev/../etc/passwd" }));
            Throws<ArgumentException>(() => Validation.Transcoding(new LibrarySettings { FfmpegPath = "a\nb" }));
        });
        return count;
    }
}
