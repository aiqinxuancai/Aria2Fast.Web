using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class LocalAriaStartupTests
{
    // Reuse the test apphost as a controllable child process with aria2's arguments.
    public static async Task RunEngine(string[] args)
    {
        var config = File.ReadAllLines(args.Single(a => a.StartsWith("--conf-path="))[12..])
            .Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        var mode = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "startup-test-mode"));
        if (mode == "exit") { Environment.ExitCode = 23; return; }
        await Task.Delay(mode is "cancel" or "timeout" ? 30000 : 1200);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{config["rpc-listen-port"]}/");
        listener.Start();
        while (true)
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var request = JsonNode.Parse(await reader.ReadToEndAsync())!;
            if (request["params"]?[0]?.ToString() != "token:" + config["rpc-secret"])
                throw new InvalidOperationException("Readiness probe must authenticate");
            var method = request["method"]!.ToString();
            var result = method == "aria2.getVersion" ? "{\"version\":\"startup-fixture\"}" : "\"OK\"";
            var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":" + result + "}");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
            if (method == "aria2.shutdown") return;
        }
    }

    public static async Task<int> Run()
    {
        foreach (var mode in new[] { "delay", "exit", "cancel", "timeout" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "aria2fast-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "startup-test-mode"), mode);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ARIA2FAST_DATA_DIR"] = directory,
                    ["ARIA2FAST_LOCAL_ENABLED"] = "true"
                }).Build();
                var store = new StateStore(config);
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                store.Update(s =>
                {
                    s.Settings.LocalRpcPort = port;
                    s.Settings.Aria2Executable = Path.Combine(AppContext.BaseDirectory,
                        "Aria2Fast.Web.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                });
                using var service = new LocalAriaService(store, NullLogger<LocalAriaService>.Instance);
                using var cancellation = new CancellationTokenSource();
                if (mode == "cancel") cancellation.CancelAfter(500);
                var timer = Stopwatch.StartNew();
                try
                {
                    await service.StartAsync(cancellation.Token);
                    if (mode == "cancel") throw new Exception("Startup must propagate cancellation");
                }
                catch (OperationCanceledException) when (mode == "cancel") { }
                if (mode == "delay")
                {
                    if (!service.Running || service.Error != null || service.AppliedStartupOptions == null)
                        throw new Exception("Delayed engine failed: " + service.Error);
                    var rpc = new AriaRpc(store, new HttpGateway(store));
                    var version = await rpc.Call("getVersion", nodeId: "local");
                    if (version["version"]?.ToString() != "startup-fixture" || timer.ElapsedMilliseconds < 1200)
                        throw new Exception("Startup returned before RPC was ready");
                    await service.StopAsync(CancellationToken.None);
                }
                else
                {
                    if (service.Running || service.AppliedStartupOptions != null)
                        throw new Exception("Failed startup left an active process or applied settings");
                    if (mode == "exit" && service.Error?.Contains("23") != true)
                        throw new Exception("Early exit must report its exit code: " + service.Error);
                    if (mode == "timeout" && (service.Error?.Contains("15 秒") != true || timer.Elapsed.TotalSeconds > 25))
                        throw new Exception("Unresponsive engine must time out: " + service.Error);
                }
                Console.WriteLine("PASS local aria2 startup: " + mode);
            }
            finally { Directory.Delete(directory, true); }
        }
        return 4;
    }
}
