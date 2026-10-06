using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class LocalDiagnostics(StateStore store, LocalAriaService local, AriaRpc rpc)
{
    public async Task<object> Inspect(CancellationToken ct)
    {
        var state = store.Read();
        var checks = new List<object>();
        void Add(string name, string status, string detail) => checks.Add(new { name, status, detail });
        Add("本地进程", local.Running ? "ok" : "error", local.Running ? "运行中" : local.Error ?? "本地服务未启动");
        var options = new Dictionary<string, string>();
        JsonArray active = [];
        if (local.Running)
        {
            try
            {
                var version = await rpc.Call("getVersion", nodeId: "local", ct: ct);
                Add("RPC 连接", "ok", "aria2 " + version["version"]);
                var result = await rpc.Call("getGlobalOption", nodeId: "local", ct: ct);
                foreach (var key in LocalAriaOptions.Effective(state).Keys)
                    if (result[key] != null) options[key] = result[key]!.ToString();
                active = (await rpc.Call("tellActive", nodeId: "local", ct: ct)).AsArray();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Add("RPC 连接", "error", ex.Message); }
        }
        try
        {
            var network = IPGlobalProperties.GetIPGlobalProperties();
            var btPort = int.TryParse(options.GetValueOrDefault("listen-port"), out var bt) ? bt : state.Settings.LocalBtPort;
            var dhtPort = int.TryParse(options.GetValueOrDefault("dht-listen-port"), out var dht) ? dht : state.Settings.LocalDhtPort;
            var tcp = network.GetActiveTcpListeners().Any(x => x.Port == btPort);
            var udp = network.GetActiveUdpListeners().Any(x => x.Port == dhtPort);
            Add("BT TCP 监听", tcp ? "ok" : "warning", $"本机端口 {btPort}：" + (tcp ? "已监听（不代表公网可达）" : "未检测到监听"));
            Add("DHT UDP 监听", udp ? "ok" : "warning", $"本机端口 {dhtPort}：" + (udp ? "已绑定（不代表公网可达）" : "未检测到绑定"));
        }
        catch (Exception ex) { Add("端口检查", "warning", "系统不支持读取监听状态：" + ex.Message); }
        Add("公网连通性", "unknown", "未进行外部探测。请确认路由器、防火墙及 Docker 的 BT TCP / DHT UDP 端口映射；共享公网地址可能无法接受入站连接。");
        Add("Tracker 列表", state.Trackers.Error != null ? "warning" : "info", $"缓存 {state.Trackers.Urls.Count} 个；" + (state.Settings.TrackerAutoUpdate ? "自动更新已开启" : "自动更新未开启") + (state.Trackers.Error == null ? "" : "；" + state.Trackers.Error));
        var torrents = active.Where(x => x?["bittorrent"] != null).Select(x => new
        {
            gid = x!["gid"]?.ToString(), name = x["bittorrent"]?["info"]?["name"]?.ToString() ?? x["gid"]?.ToString(),
            connections = x["connections"]?.ToString() ?? "0", seeders = x["numSeeders"]?.ToString() ?? "未知",
            downloadSpeed = x["downloadSpeed"]?.ToString() ?? "0"
        }).ToArray();
        return new { checks, options, torrents, tracker = state.Trackers, restartRequired = local.Running && local.AppliedStartupOptions != LocalAriaService.StartupSignature(state.Settings) };
    }
}
