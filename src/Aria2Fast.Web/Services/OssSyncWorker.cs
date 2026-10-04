using Aliyun.OSS.Common;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

/// <summary>Merge subscription history before publishing. Local rules win for existing feeds.</summary>
public sealed class OssSyncWorker(StateStore store, BackupService backup, ILogger<OssSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = DateTimeOffset.UtcNow.AddSeconds(15);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var options = store.Read().Settings.Oss;
            if (!options.AutoSync || DateTimeOffset.UtcNow < next) continue;
            next = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(options.IntervalMinutes, 1, 1440));
            try
            {
                try { backup.Download(); }
                catch (OssException ex) when (ex.ErrorCode == "NoSuchKey") { }
                backup.Upload();
                store.Log("OSS 自动同步完成：已合并订阅与历史，再上传当前快照");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OSS 自动同步失败");
                store.Log("OSS 自动同步失败，请检查网络与 OSS 配置", "error");
            }
        }
    }
}
