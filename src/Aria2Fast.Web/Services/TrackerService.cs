using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class TrackerService(StateStore store, HttpGateway http, AriaRpc rpc, LocalAriaService local, ILogger<TrackerService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1);

    public static List<string> Parse(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length > 512 || line.Any(char.IsWhiteSpace) || line.Contains(',') ||
                !Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "udp") ||
                (uri.Scheme == "udp" && uri.Port <= 0) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) continue;
            if (!result.Contains(line)) result.Add(line);
            if (result.Count == 100) break;
        }
        return result;
    }

    public async Task<object> Update(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var settings = store.Read().Settings;
            if (!settings.TrackerAutoUpdate) throw new ArgumentException("请先启用公共 Tracker 自动更新并保存设置");
            store.Update(s => s.Trackers.LastAttempt = DateTimeOffset.UtcNow);
            var urls = new List<string>();
            var errors = new List<string>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            foreach (var source in LocalAriaOptions.Sources(settings))
            {
                try
                {
                    var parsed = Parse(await http.GetTextAsync(source, deadline.Token));
                    if (parsed.Count == 0) throw new InvalidDataException("列表中没有有效的 Tracker");
                    urls.AddRange(parsed);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add(new Uri(source).Host + ": " + ex.Message); }
            }
            if (urls.Count == 0) throw new InvalidOperationException("Tracker 更新失败，保留原列表。" + string.Join("；", errors));
            var changed = false;
            store.Update(s =>
            {
                if (!s.Settings.TrackerAutoUpdate || s.Settings.TrackerSources != settings.TrackerSources) { changed = true; return; }
                s.Trackers.Urls = urls.Distinct().Take(100).ToList();
                s.Trackers.LastSuccess = DateTimeOffset.UtcNow;
                s.Trackers.Error = errors.Count > 0 ? string.Join("；", errors) : null;
            });
            if (changed) throw new InvalidOperationException("Tracker 设置已改变，请按新设置重新更新");
            // Global changes affect future tasks. Existing tasks keep their own tracker configuration.
            var applied = false;
            if (local.Running)
            {
                await rpc.Call("changeGlobalOption", [new Dictionary<string, string> { ["bt-tracker"] = LocalAriaOptions.Trackers(store.Read()) }], "local", ct);
                applied = true;
            }
            return new { count = store.Read().Trackers.Urls.Count, applied, warning = store.Read().Trackers.Error };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            store.Update(s => s.Trackers.Error = ex.Message);
            throw;
        }
        finally { gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var state = store.Read();
            if (state.Settings.LocalEnabled && state.Settings.TrackerAutoUpdate &&
                (state.Trackers.LastAttempt == null || DateTimeOffset.UtcNow - state.Trackers.LastAttempt >= TimeSpan.FromHours(state.Trackers.Error == null ? state.Settings.TrackerUpdateHours : 1)))
            {
                try { await Update(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Tracker 自动更新失败"); }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
