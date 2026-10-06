using System.Globalization;
using System.Text.RegularExpressions;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public static class LocalAriaOptions
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["max-concurrent-downloads"] = "3", ["max-connection-per-server"] = "8",
        ["split"] = "8", ["min-split-size"] = "10M", ["bt-max-peers"] = "128",
        ["max-overall-download-limit"] = "0", ["max-overall-upload-limit"] = "0",
        ["enable-dht"] = "true", ["enable-peer-exchange"] = "true"
    };

    public static void Validate(WebSettings settings)
    {
        if (settings.LocalBtPort is < 1024 or > 65535 || settings.LocalDhtPort is < 1024 or > 65535 || settings.LocalBtPort == settings.LocalRpcPort)
            throw new ArgumentException("BT / DHT 端口应在 1024 至 65535 之间，BT TCP 端口不可与 RPC 相同");
        if (settings.TrackerUpdateHours is < 1 or > 168) throw new ArgumentException("Tracker 更新间隔应在 1 至 168 小时之间");
        var sources = Sources(settings);
        if (sources.Length > 5 || settings.TrackerSources.Length > 10000 || (settings.TrackerAutoUpdate && sources.Length == 0))
            throw new ArgumentException("请填写 1 至 5 个 Tracker 列表地址");
        foreach (var source in sources) Validation.HttpUrl(source);
        foreach (var key in Defaults.Keys)
        {
            if (!settings.LocalOptions.TryGetValue(key, out var value)) continue;
            var valid = key switch
            {
                "enable-dht" or "enable-peer-exchange" => value is "true" or "false",
                "max-concurrent-downloads" => InRange(value, 1, 20),
                "max-connection-per-server" => InRange(value, 1, 16),
                "split" => InRange(value, 1, 16),
                "bt-max-peers" => InRange(value, 1, 512),
                "min-split-size" => Regex.IsMatch(value, "^(?:[1-9]|[1-9][0-9]|[1-9][0-9]{2}|10[01][0-9]|102[0-4])M$"),
                _ => Regex.IsMatch(value, "^[0-9]{1,9}[KMG]?$", RegexOptions.IgnoreCase)
            };
            if (!valid) throw new ArgumentException("本地下载参数无效：" + key);
        }
    }

    private static bool InRange(string value, int min, int max) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max;
    public static string[] Sources(WebSettings settings) => settings.TrackerSources.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();

    public static Dictionary<string, string> Effective(AppState state)
    {
        var options = new Dictionary<string, string>(Defaults);
        foreach (var pair in state.Settings.LocalOptions) options[pair.Key] = pair.Value;
        options["listen-port"] = state.Settings.LocalBtPort.ToString(CultureInfo.InvariantCulture);
        options["dht-listen-port"] = state.Settings.LocalDhtPort.ToString(CultureInfo.InvariantCulture);
        options["bt-tracker"] = Trackers(state);
        return options;
    }

    public static string Trackers(AppState state) => string.Join(',',
        (state.Settings.LocalOptions.GetValueOrDefault("bt-tracker", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        .Concat(state.Settings.TrackerAutoUpdate ? state.Trackers.Urls : []).Distinct());
}
