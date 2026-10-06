using System.Diagnostics;
using System.Security.Cryptography;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class LocalAriaService(StateStore store, ILogger<LocalAriaService> logger) : IHostedService, IDisposable
{
    private Process? process;
    private readonly SemaphoreSlim gate = new(1);
    public string? AppliedStartupOptions { get; private set; }
    public static string StartupSignature(Aria2Fast.Web.Models.WebSettings settings) => System.Text.Json.JsonSerializer.Serialize(new { settings.LocalRpcPort, settings.LocalBtPort, settings.LocalDhtPort, settings.DownloadDirectory, settings.Aria2Executable, settings.LocalOptions, settings.TrackerAutoUpdate });
    public string? Error { get; private set; }
    public bool Running => process is { HasExited: false };

    public Task StartAsync(CancellationToken ct) => Restart(ct);
    public async Task Restart(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await StopAsync(ct);
            AppliedStartupOptions = null;
            var state = store.Read();
            var settings = state.Settings;
            LocalAriaOptions.Validate(settings);
            Error = null;
            if (!settings.LocalEnabled) return;
            var executable = settings.Aria2Executable;
            if (string.IsNullOrWhiteSpace(executable))
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "aria2", OperatingSystem.IsWindows() ? "aria2c.exe" : "aria2c");
                executable = File.Exists(bundled) ? bundled : "aria2c";
            }
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var session = Path.Combine(store.DataDirectory, "aria2.session");
            if (!File.Exists(session)) File.WriteAllText(session, "");
            Directory.CreateDirectory(settings.DownloadDirectory);
            var config = Path.Combine(store.DataDirectory, "aria2.conf");
            File.WriteAllLines(config, ["enable-rpc=true", "rpc-listen-all=false", "rpc-allow-origin-all=false", $"rpc-listen-port={settings.LocalRpcPort}", $"rpc-secret={secret}", $"dir={settings.DownloadDirectory}", $"input-file={session}", $"save-session={session}", "save-session-interval=30", "continue=true", "seed-time=0", "bt-save-metadata=true", "follow-torrent=true", "check-certificate=true", "summary-interval=0", "console-log-level=warn", $"dht-file-path={Path.Combine(store.DataDirectory, "dht.dat")}", $"dht-file-path6={Path.Combine(store.DataDirectory, "dht6.dat")}"]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            store.Update(s =>
            {
                var local = s.Nodes.First(n => n.Id == "local");
                local.Secret = secret;
                local.Url = $"http://127.0.0.1:{settings.LocalRpcPort}/jsonrpc";
                local.DownloadDirectory = settings.DownloadDirectory;
            });
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = store.DataDirectory };
            start.ArgumentList.Add("--conf-path=" + config);
            start.ArgumentList.Add("--rpc-save-upload-metadata=true");
            start.ArgumentList.Add("--stop-with-process=" + Environment.ProcessId);
            foreach (var (key, value) in LocalAriaOptions.Effective(state))
                start.ArgumentList.Add("--" + key + "=" + value);
            process = new Process { StartInfo = start };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogWarning("aria2: {Message}", e.Data); };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogInformation("aria2: {Message}", e.Data); };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await WaitForRpc(ct);
            AppliedStartupOptions = StartupSignature(settings);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (process != null)
            {
                try { Stop(); } catch (InvalidOperationException) { process?.Dispose(); process = null; }
            }
            Error = "本地 aria2 启动失败，请检查执行文件及端口：" + ex.Message;
            logger.LogWarning("{Error}", Error);
            store.Log(Error, "error");
        }
        finally { gate.Release(); }
    }

    private async Task WaitForRpc(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var rpc = new AriaRpc(store, new HttpGateway(store));
        Exception? lastError = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process!.HasExited)
                    throw new InvalidOperationException($"aria2 提前退出，退出码 {process.ExitCode}");
                try
                {
                    var version = await rpc.Call("getVersion", nodeId: "local", ct: timeout.Token);
                    if (string.IsNullOrWhiteSpace(version["version"]?.ToString()))
                        throw new InvalidOperationException("aria2 RPC 未返回版本信息");
                    if (process.HasExited)
                        throw new InvalidOperationException($"aria2 提前退出，退出码 {process.ExitCode}");
                    return;
                }
                catch (HttpRequestException ex) { lastError = ex; }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("等待本地 aria2 RPC 就绪超时（15 秒）", lastError);
        }
    }

    private void Stop()
    {
        if (process is null) return;
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
        process.Dispose();
        process = null;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (Running)
        {
            try
            {
                var rpc = new AriaRpc(store, new HttpGateway(store));
                await rpc.Call("saveSession", nodeId: "local", ct: ct);
                await rpc.Call("shutdown", nodeId: "local", ct: ct);
                if (process != null) await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex) { logger.LogDebug(ex, "aria2 shutdown"); }
        }
        Stop();
    }
    public void Dispose() { Stop(); gate.Dispose(); }
}
