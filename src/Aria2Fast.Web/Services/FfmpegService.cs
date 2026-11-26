using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Optional ffmpeg / ffprobe for remuxing and transcoding. Not bundled: found from the configured path, the
/// application folder or PATH. Without them the media library plays files directly, as before.
/// </summary>
public sealed class FfmpegService(StateStore store)
{
    public static readonly string[] Encoders = ["libx264", "h264_nvenc", "h264_qsv", "h264_vaapi", "h264_videotoolbox", "h264_amf"];
    private readonly SemaphoreSlim gate = new(1);
    private (string Path, FfmpegStatus Status, DateTimeOffset Time)? cached;

    public sealed record FfmpegStatus(bool Available, string Ffmpeg, string Ffprobe, string Version, List<string> Encoders, string? Error);
    public sealed record ProcessResult(int ExitCode, string Output, string Error);

    /// <summary>Detection result, cached for five minutes or until the configured path changes.</summary>
    public async Task<FfmpegStatus> Status(CancellationToken ct, bool refresh = false)
    {
        var configured = store.Read().Settings.Library.FfmpegPath.Trim();
        if (!refresh && cached is { } hit && hit.Path == configured && DateTimeOffset.UtcNow - hit.Time < TimeSpan.FromMinutes(5)) return hit.Status;
        await gate.WaitAsync(ct);
        try
        {
            var status = await Detect(configured, ct);
            cached = (configured, status, DateTimeOffset.UtcNow);
            return status;
        }
        finally { gate.Release(); }
    }

    /// <summary>The tools when transcoding is switched on and both were found; otherwise null.</summary>
    public async Task<FfmpegStatus?> Ready(CancellationToken ct)
    {
        if (!store.Read().Settings.Library.Transcoding) return null;
        var status = await Status(ct);
        return status.Available ? status : null;
    }

    private static async Task<FfmpegStatus> Detect(string configured, CancellationToken ct)
    {
        var (ffmpeg, ffprobe) = Locate(configured, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg == null || ffprobe == null)
            return new(false, "", "", "", [], configured.Length > 0 ? "设置的位置没有同时找到 ffmpeg 和 ffprobe" : "没有找到 ffmpeg 和 ffprobe：请安装后加入 PATH，或在设置中填写所在位置");
        try
        {
            var version = await Run(ffmpeg, ["-hide_banner", "-version"], ct, TimeSpan.FromSeconds(15));
            var encoders = await Run(ffmpeg, ["-hide_banner", "-encoders"], ct, TimeSpan.FromSeconds(15));
            await Run(ffprobe, ["-version"], ct, TimeSpan.FromSeconds(15));
            return new(true, ffmpeg, ffprobe, version.Output.Split('\n')[0].Trim(), AvailableEncoders(encoders.Output), null);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException or IOException)
        {
            return new(false, ffmpeg, ffprobe, "", [], "ffmpeg 无法运行：" + ex.Message);
        }
    }

    /// <summary>H.264 encoders this build offers, from the output of <c>ffmpeg -encoders</c>.</summary>
    public static List<string> AvailableEncoders(string listing) =>
        Encoders.Where(e => Regex.IsMatch(listing, @"^\s*V\S*\s+" + Regex.Escape(e) + @"\s", RegexOptions.Multiline)).ToList();

    /// <summary>The configured file or folder, else beside the application, else PATH (plus the usual Homebrew and /usr/local folders).</summary>
    public static (string? Ffmpeg, string? Ffprobe) Locate(string configured, string applicationDirectory, string? pathVariable)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        IEnumerable<string> folders;
        if (configured.Length > 0)
            folders = [File.Exists(configured) ? Path.GetDirectoryName(Path.GetFullPath(configured))! : configured];
        else
        {
            folders = new[] { Path.Combine(applicationDirectory, "ffmpeg"), Path.Combine(applicationDirectory, "ffmpeg", "bin"), applicationDirectory }
                .Concat((pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Trim('"')));
            if (!OperatingSystem.IsWindows()) folders = folders.Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        }
        foreach (var folder in folders)
        {
            var ffmpeg = Path.Combine(folder, "ffmpeg" + exe);
            var ffprobe = Path.Combine(folder, "ffprobe" + exe);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return (ffmpeg, ffprobe);
        }
        return (null, null);
    }

    /// <summary>Encodes two seconds of a test pattern with the chosen encoder, which tells whether the hardware and drivers work.</summary>
    public async Task<object> Test(string encoder, CancellationToken ct)
    {
        var status = await Status(ct, refresh: true);
        if (!status.Available) throw new ArgumentException(status.Error ?? "没有找到 ffmpeg");
        if (!Encoders.Contains(encoder)) throw new ArgumentException("不支持的编码器");
        if (!status.Encoders.Contains(encoder)) throw new ArgumentException("当前 ffmpeg 没有编译 " + encoder);
        var settings = store.Read().Settings.Library;
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        arguments.AddRange(HlsService.EncoderInput(encoder, settings.VaapiDevice));
        arguments.AddRange(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24:duration=2"]);
        arguments.AddRange(["-vf", string.Join(',', HlsService.EncoderFormat(encoder))]);
        arguments.AddRange(HlsService.VideoEncoding(encoder, 720));
        arguments.AddRange(["-f", "null", "-"]);
        var watch = Stopwatch.StartNew();
        var result = await Run(status.Ffmpeg, arguments, ct, TimeSpan.FromSeconds(90));
        if (result.ExitCode != 0) throw new InvalidOperationException(encoder + " 无法使用：" + Tail(result.Error));
        return new { encoder, seconds = Math.Round(watch.Elapsed.TotalSeconds, 2), status.Version };
    }

    public static async Task<ProcessResult> Run(string file, IEnumerable<string> arguments, CancellationToken ct, TimeSpan timeout, string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = StartInfo(file, arguments, workingDirectory, captureOutput: true) };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(Path.GetFileNameWithoutExtension(file) + " 处理超时");
        }
        return new(process.ExitCode, await output, await error);
    }

    public static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments, string? workingDirectory, bool captureOutput)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = captureOutput,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = captureOutput ? Encoding.UTF8 : null
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Keeps the web server responsive while ffmpeg works.</summary>
    public static void LowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    /// <summary>The last lines of ffmpeg's error output, for messages.</summary>
    public static string Tail(string text, int lines = 3) =>
        string.Join(" / ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines)) is { Length: > 0 } tail ? tail : "未知错误";
}
