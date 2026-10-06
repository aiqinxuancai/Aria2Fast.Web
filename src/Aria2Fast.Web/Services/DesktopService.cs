using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;
using Aria2Fast.Web.Infrastructure;

namespace Aria2Fast.Web.Services;

public sealed class DesktopService(IConfiguration config, StateStore store)
{
    private const string Label = "com.aria2fast.web";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public bool Available => IsDesktop(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
        config["DOTNET_RUNNING_IN_CONTAINER"], config["DOTNET_RUNNING_IN_CONTAINERS"], config["ARIA2FAST_DESKTOP"], File.Exists("/.dockerenv"));
    public static bool IsDesktop(bool supported, string? container, string? containers, string? desktop, bool dockerFile) =>
        supported && !dockerFile && !string.Equals(container, "true", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(containers, "true", StringComparison.OrdinalIgnoreCase) && !string.Equals(desktop, "false", StringComparison.OrdinalIgnoreCase);
    public string? BrowserUrl { get; private set; }
    private string Launcher => Path.Combine(store.DataDirectory, "desktop-launch.vbs");
    private string Agent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");
    public object Status() => new { available = Available, autoStart = Available && AutoStart(), url = BrowserUrl };
    public void Started(IEnumerable<string> addresses)
    {
        BrowserUrl = LocalUrl(addresses);
        if (!Available || BrowserUrl is null) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(BrowserUrl) { UseShellExecute = true })?.Dispose();
            else
            {
                var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                start.ArgumentList.Add(BrowserUrl);
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception ex) { store.Log("自动打开浏览器失败：" + ex.Message, "warning"); }
    }
    public static string? LocalUrl(IEnumerable<string> addresses)
    {
        foreach (var address in addresses.OrderBy(x => x.StartsWith("https:") ? 1 : 0))
        {
            var normalized = address.Replace("://*:", "://localhost:").Replace("://+:", "://localhost:");
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var builder = new UriBuilder(uri);
            if (uri.Host is "0.0.0.0" or "[::]" or "::") builder.Host = "localhost";
            return builder.Uri.AbsoluteUri;
        }
        return null;
    }
    private void RequireDesktop()
    {
        if (!Available) throw new ArgumentException("此运行环境不支持桌面集成");
        if (BrowserUrl is null) throw new InvalidOperationException("服务尚未就绪");
    }
    private bool AutoStart()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Aria2Fast.Web") is string;
        }
        return File.Exists(Agent);
    }
    private string[] LaunchArguments()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径");
        var args = new List<string> { process };
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.Add(Path.Combine(AppContext.BaseDirectory, "Aria2Fast.Web.dll"));
        args.AddRange(["--urls", config["urls"] ?? BrowserUrl!, "--ARIA2FAST_DATA_DIR", store.DataDirectory,
            "--ARIA2FAST_DOWNLOAD_DIR", store.Read().Settings.DownloadDirectory]);
        return args.ToArray();
    }
    public static string VbsLauncher(IEnumerable<string> arguments, string directory)
    {
        static string Literal(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var command = string.Join(" ", arguments.Select(x => "\"" + x.TrimEnd('\\') + "\""));
        return "Set shell = CreateObject(\"WScript.Shell\")\r\nshell.CurrentDirectory = " + Literal(directory) +
            "\r\nshell.Run " + Literal(command) + ", 0, False\r\n";
    }
    private void WriteWindowsLauncher() => File.WriteAllText(Launcher, VbsLauncher(LaunchArguments(), AppContext.BaseDirectory), Encoding.Unicode);
    public void SetAutoStart(bool enabled)
    {
        RequireDesktop();
        if (OperatingSystem.IsWindows())
        {
            if (enabled) WriteWindowsLauncher();
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var command = $"\"{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe")}\" \"{Launcher}\"";
                if (command.Length > 260) throw new ArgumentException("数据目录路径过长，无法注册开机自启");
                key.SetValue("Aria2Fast.Web", command);
            }
            else key.DeleteValue("Aria2Fast.Web", false);
        }
        else if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Agent)!);
            File.WriteAllText(Agent, LaunchAgent(LaunchArguments(), AppContext.BaseDirectory, store.DataDirectory));
        }
        else File.Delete(Agent);
    }
    public static string LaunchAgent(IEnumerable<string> arguments, string directory, string data) =>
        new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", Label),
            new XElement("key", "ProgramArguments"), new XElement("array", arguments.Select(x => new XElement("string", x))),
            new XElement("key", "WorkingDirectory"), new XElement("string", directory),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "StandardOutPath"), new XElement("string", Path.Combine(data, "desktop.log")),
            new XElement("key", "StandardErrorPath"), new XElement("string", Path.Combine(data, "desktop-error.log")))).ToString();
    public async Task<string> CreateShortcut(CancellationToken ct)
    {
        RequireDesktop();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) throw new InvalidOperationException("找不到当前用户的桌面目录");
        if (OperatingSystem.IsWindows())
        {
            WriteWindowsLauncher();
            var path = Path.Combine(desktop, "Aria2Fast Web.lnk");
            static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
            var script = "$ErrorActionPreference='Stop'; $s=(New-Object -ComObject WScript.Shell).CreateShortcut(" + Ps(path) + ");" +
                "$s.TargetPath=" + Ps(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe")) + ";" +
                "$s.Arguments=" + Ps("\"" + Launcher + "\"") + ";$s.WorkingDirectory=" + Ps(AppContext.BaseDirectory) + ";" +
                "$s.IconLocation=" + Ps(Environment.ProcessPath! + ",0") + ";$s.Save()";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法创建快捷方式");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidOperationException("创建桌面快捷方式失败");
            return path;
        }
        else
        {
            var path = Path.Combine(desktop, "Aria2Fast Web.app");
            var contents = Path.Combine(path, "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            var plist = new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "CFBundleExecutable"), new XElement("string", "launch"),
                new XElement("key", "CFBundleIdentifier"), new XElement("string", Label + ".launcher"),
                new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL"),
                new XElement("key", "LSUIElement"), new XElement("true")));
            File.WriteAllText(Path.Combine(contents, "Info.plist"), plist.ToString());
            var launcher = Path.Combine(contents, "MacOS", "launch");
            static string Sh(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
            File.WriteAllText(launcher, "#!/bin/sh\ncd " + Sh(AppContext.BaseDirectory) + " || exit 1\nexec " + string.Join(" ", LaunchArguments().Select(Sh)) +
                " >> " + Sh(Path.Combine(store.DataDirectory, "desktop.log")) + " 2>&1\n");
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }
}
