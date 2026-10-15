using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Steam launches the official launcher in its own child process tree so Valve's
// renderer can propagate normally. Only the button assistant uses Task Scheduler.
namespace EndfieldSteamBridge
{
internal static class BridgeRuntime
{
    private static readonly string Root = App.Home;
    private static RuntimeConfig Config;
    private static string Launcher { get { return Config.Launcher; } }
    private static string Game { get { return Config.Game; } }
    private static string TaskName { get { return Config.TaskName; } }
    private static int Port;
    private static readonly string RequestPath = Path.Combine(Root, "request.json");
    private static readonly string StatusPath = Path.Combine(Root, "status.json");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    internal sealed class Request
    {
        public int Pid { get; set; }
        public int DebugPort { get; set; }
        public long StartTicks { get; set; }
        public string Id { get; set; }
        public int LauncherPid { get; set; }
        public long LauncherStartTicks { get; set; }
    }

    public static int Run()
    {
        Config = App.Load<RuntimeConfig>(App.ConfigPath);
        if (Environment.GetEnvironmentVariable("SteamAppId") != App.AppId.ToString())
            throw new InvalidOperationException("请从 Steam 库中的《明日方舟：终末地》开始游戏。");
        bool created;
        using (var mutex = new Mutex(true, @"Local\SteamEndfieldCNBridge", out created))
        {
            if (!created) return 0;
            return Bridge();
        }
    }
    public static int RunAssist()
    {
        Config = App.Load<RuntimeConfig>(App.ConfigPath);
        return Assist();
    }
    private static bool SessionPath(string path)
    {
        return path != null && (path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.GetDirectoryName(Game) + "\\", StringComparison.OrdinalIgnoreCase));
    }
    private static int Bridge()
    {
        Request request = null;
        var ownedLaunchers = new Dictionary<int, long>();
        var originalLaunchers = LauncherProcesses();
        bool launchedHere = false;
        bool gameObserved = false;
        try
        {
            if (!File.Exists(Game)) throw new FileNotFoundException("找不到国服客户端。", Game);
            Log("bridge", "启动；SteamAppId=" + Environment.GetEnvironmentVariable("SteamAppId"));
            if (GameRunning()) throw new InvalidOperationException("国服已在运行。请正常退出后再从 Steam 启动。");
            if (!GameRunning())
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                if (LauncherRunning()) throw new InvalidOperationException("请先完全退出鹰角启动器（包括托盘），再从 Steam 启动。本工具需要由 Steam 重新打开启动器，才能把叠加层传递给国服。");
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new InvalidOperationException("国服启动器需要管理员权限。请先正常退出 Steam，再以管理员身份打开 Steam 后重试。原生叠加层需要 Steam 与国服保持相同权限。");
                using (var self = Process.GetCurrentProcess())
                    request = new Request { Pid = self.Id, StartTicks = self.StartTime.ToUniversalTime().Ticks, Id = Guid.NewGuid().ToString("N"), DebugPort = Port };
                WriteJson(RequestPath, request);
                var info = new ProcessStartInfo
                {
                    FileName = Launcher, Arguments = "--game=endfield",
                    WorkingDirectory = Path.GetDirectoryName(Launcher), UseShellExecute = false
                };
                info.EnvironmentVariables["QTWEBENGINE_REMOTE_DEBUGGING"] = "127.0.0.1:" + Port;
                // Keep the environment Steam assigned to this official app entry.
                using (var launcher = Process.Start(info))
                {
                    launchedHere = true;
                    ownedLaunchers[launcher.Id] = launcher.StartTime.ToUniversalTime().Ticks;
                    request.LauncherPid = launcher.Id;
                    request.LauncherStartTicks = ownedLaunchers[launcher.Id];
                    WriteJson(RequestPath, request);
                    Log("bridge", "由 Steam 进程链打开官方启动器 PID=" + launcher.Id);
                }
                using (var task = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    Arguments = "/run /tn \"" + TaskName + "\"",
                    UseShellExecute = false, CreateNoWindow = true
                }))
                {
                    task.WaitForExit();
                    if (task.ExitCode != 0) throw new InvalidOperationException("无法运行自动启动任务。请重新载入国服。");
                }
                Log("bridge", "等待自动启动国服。");
                var deadline = DateTime.UtcNow.AddMinutes(15);
                while (!GameRunning())
                {
                    TrackLaunchers(originalLaunchers, ownedLaunchers);
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("15 分钟内未检测到国服游戏。请查看鹰角启动器与 bridge.log。");
                    Thread.Sleep(500);
                }
            }
            Log("bridge", "检测到国服游戏，跟踪游玩时长。");
            gameObserved = true;
            var nextOverlayCheck = DateTime.UtcNow;
            while (GameRunning())
            {
                if (launchedHere)
                {
                    TrackLaunchers(originalLaunchers, ownedLaunchers);
                    if (DateTime.UtcNow >= nextOverlayCheck)
                    {
                        RemoveLauncherOverlay(ownedLaunchers);
                        nextOverlayCheck = DateTime.UtcNow.AddSeconds(5);
                    }
                }
                Thread.Sleep(1000);
            }
            Log("bridge", "国服已退出，结束 Steam 本次运行。");
            return 0;
        }
        catch (Exception error)
        {
            Log("bridge", "错误：" + error.Message);
            MessageBox.Show(error.Message, "Endfield Steam Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (launchedHere && (!gameObserved || !GameRunning()))
            {
                TrackLaunchers(originalLaunchers, ownedLaunchers);
                CloseOwnedLaunchers(ownedLaunchers);
            }

        }
    }

    private static int Assist()
    {
        Request request;
        try { request = Json.Deserialize<Request>(File.ReadAllText(RequestPath)); Port = request.DebugPort; }
        catch (Exception error) { Log("watch", "无法读取本轮启动记录：" + error.Message); return 1; }
        if (!BridgeAlive(request))
        {
            // Steam may have stopped the bridge while Task Scheduler was still
            // starting this helper. The recorded root identifies that session.
            CleanupStoppedSession(request);
            return 0;
        }
        var watcher = new Thread(delegate() { WatchBridge(request); });
        watcher.IsBackground = true;
        watcher.Start();
        int result = AssistStart(request);
        // Stay outside Steam's process tree for the entire session. Steam Stop
        // forcibly terminates the bridge and cannot execute its finally block.
        watcher.Join();
        return result;
    }

    private static int AssistStart(Request request)
    {
        try
        {
            if (!BridgeAlive(request)) return 0;
            Status(request, "preparing", "准备官方启动器。");
            if (GameRunning()) { Status(request, "started", "国服已经运行。"); return 0; }

            // These variables apply only to this launcher and its child processes.
            // No user/system environment settings or launcher files are changed.
            string endpoint;
            Status(request, "waiting", "等待国服开始游戏按钮。");
            var deadline = DateTime.UtcNow.AddMinutes(2);
            string lastState = "等待游戏页面";
            while (DateTime.UtcNow < deadline && BridgeAlive(request))
            {
                if (GameRunning()) { Status(request, "started", "已检测到国服。"); return 0; }
                endpoint = FindGamePage();
                if (endpoint == null) { Thread.Sleep(500); continue; }
                using (var cdp = new Cdp(endpoint))
                {
                    while (DateTime.UtcNow < deadline && BridgeAlive(request))
                    {
                        if (GameRunning()) { Status(request, "started", "已检测到国服。"); return 0; }
                        // DOM click follows the launcher's own React/native event path.
                        // Exact CN app identity, ready label and overlay hit-test must all match.
                        string state = cdp.Evaluate(ClickStartScript);
                        if (state != lastState) { Log("assist", "按钮状态：" + state); lastState = state; }
                        if (state == "clicked")
                        {
                            Status(request, "clicked", "已自动触发官方开始游戏按钮。");
                            var gameDeadline = DateTime.UtcNow.AddMinutes(2);
                            while (DateTime.UtcNow < gameDeadline && BridgeAlive(request))
                            {
                                if (GameRunning()) { Status(request, "started", "自动启动成功，检测到国服。"); return 0; }
                                Thread.Sleep(500);
                            }
                            if (!BridgeAlive(request)) return 0;
                            throw new TimeoutException("已点击官方开始游戏，但尚未检测到国服。请查看启动器中的更新、提示或登录要求。");
                        }
                        Thread.Sleep(700);
                    }
                }
            }
            if (!BridgeAlive(request)) return 0;
            throw new TimeoutException("自动开始等待超时，最后状态：" + lastState + "。\n请处理启动器中的更新或弹窗，再手动点击开始游戏；Steam 仍会正常计时。");
        }
        catch (Exception error)
        {
            Log("assist", "自动开始未完成：" + error.Message);
            if (request != null && BridgeAlive(request))
            {
                Status(request, "manual", error.Message);
                MessageBox.Show(error.Message, "Endfield Steam Bridge：需要手动处理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 1;
        }
    }

    private static void WatchBridge(Request request)
    {
        var owned = SessionRoot(request);
        Log("watch", "停止监护已就绪；桥接 PID=" + request.Pid + "，启动器根 PID=" + request.LauncherPid);
        while (BridgeAlive(request))
        {
            TrackSession(owned);
            Thread.Sleep(500);
        }
        Log("watch", "桥接已结束，清理本轮残留进程。");
        TrackSession(owned);
        CloseOwnedLaunchers(owned);
        Log("watch", "本轮停止清理完成。");
        // Also dismiss any helper-owned error dialog left open during a stop.
        Environment.Exit(0);
    }

    private static Dictionary<int, long> SessionRoot(Request request)
    {
        var owned = new Dictionary<int, long>();
        if (request.LauncherPid > 0 && request.LauncherStartTicks > 0)
            owned[request.LauncherPid] = request.LauncherStartTicks;
        return owned;
    }

    private static void CleanupStoppedSession(Request request)
    {
        var owned = SessionRoot(request);
        TrackSession(owned);
        CloseOwnedLaunchers(owned);
        Log("watch", "启动期间已取消，清理完成。");
    }

    private static void TrackSession(Dictionary<int, long> owned)
    {
        // Match descendants of this launch's recorded root, not every launcher
        // or game on the machine. Retain exited parents to discover their children.
        try
        {
            var candidates = new List<int[]>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process WHERE Name='Launcher.exe' OR Name='Games.exe' OR Name='QtWebEngineProcess.exe' OR Name='Endfield.exe' OR Name='UnityCrashHandler64.exe'"))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row) candidates.Add(new[] { Convert.ToInt32(row["ProcessId"]), Convert.ToInt32(row["ParentProcessId"]) });
            bool added;
            do
            {
                added = false;
                foreach (var candidate in candidates)
                {
                    int pid = candidate[0], parent = candidate[1];
                    if (owned.ContainsKey(pid) || !owned.ContainsKey(parent)) continue;
                    long started = CreationTicks(pid);
                    string path = ImagePath(pid);
                    if (started < owned[parent] || !SessionPath(path)) continue;
                    owned[pid] = started;
                    added = true;
                }
            } while (added);
        }
        catch (ManagementException error) { Log("watch", "读取本轮进程树失败：" + error.Message); }
    }

    private const string ClickStartScript = @"(function() {
        const p = new URLSearchParams(location.hash.replace(/^#\??/, ''));
        if (p.get('app_code') !== '6LL0KJuqHBVz33WK' || p.get('region') !== 'cn' || p.get('is_game') !== 'true') return 'wrong-page';
        const visible = e => !!(e.offsetWidth && e.offsetHeight && e.getClientRects().length);
        const nodes = Array.from(document.querySelectorAll('.clamp-text-2')).filter(visible);
        const ready = nodes.filter(e => e.textContent.trim() === '开始游戏');
        if (ready.length !== 1) return 'not-ready: ' + nodes.map(e => e.textContent.trim()).join(' / ').slice(0, 180);
        const e = ready[0], target = e.closest('.cursor-pointer');
        if (!target || target.closest('.cursor-not-allowed,[disabled],[aria-disabled=true]')) return 'disabled';
        const r = e.getBoundingClientRect(), top = document.elementFromPoint(r.x + r.width/2, r.y + r.height/2);
        if (!top || !target.contains(top)) return 'covered';
        e.click();
        return 'clicked';
    })()";

    private static string FindGamePage()
    {
        try
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + "/json/list");
            request.Proxy = null; request.Timeout = 1000; request.ReadWriteTimeout = 1000;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var pages = Json.DeserializeObject(reader.ReadToEnd()) as object[];
                if (pages == null) return null;
                foreach (var item in pages)
                {
                    var page = item as Dictionary<string, object>;
                    if (page == null || !page.ContainsKey("url") || !page.ContainsKey("webSocketDebuggerUrl")) continue;
                    string url = Convert.ToString(page["url"]);
                    if (!url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) || !url.Contains("/res/web/") ||
                        !url.Contains("app_code=6LL0KJuqHBVz33WK") || !url.Contains("region=cn") || !url.Contains("is_game=true")) continue;
                    var endpoint = new Uri(Convert.ToString(page["webSocketDebuggerUrl"]));
                    if (endpoint.Scheme == "ws" && endpoint.Host == "127.0.0.1" && endpoint.Port == Port) return endpoint.AbsoluteUri;
                }
            }
        }
        catch (WebException) { }
        return null;
    }

    private sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private int nextId;
        public Cdp(string endpoint)
        {
            socket.Options.Proxy = null;
            using (var timeout = new CancellationTokenSource(5000)) socket.ConnectAsync(new Uri(endpoint), timeout.Token).GetAwaiter().GetResult();
        }
        public string Evaluate(string expression)
        {
            int id = ++nextId;
            var command = new { id = id, method = "Runtime.evaluate", @params = new { expression = expression, returnByValue = true } };
            using (var timeout = new CancellationTokenSource(5000))
            {
                byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(command));
                socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                var buffer = new byte[16384];
                while (true)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult received;
                        do
                        {
                            received = socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                            if (received.MessageType == WebSocketMessageType.Close) throw new IOException("启动器页面连接已关闭。");
                            message.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        var reply = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                        if (!reply.ContainsKey("id") || Convert.ToInt32(reply["id"]) != id) continue;
                        if (reply.ContainsKey("error")) throw new IOException("启动器页面拒绝自动操作。");
                        var result = (Dictionary<string, object>)reply["result"];
                        if (result.ContainsKey("exceptionDetails")) throw new IOException("启动器按钮识别脚本执行失败。");
                        var value = (Dictionary<string, object>)result["result"];
                        return value.ContainsKey("value") ? Convert.ToString(value["value"]) : "no-value";
                    }
                }
            }
        }
        public void Dispose() { socket.Dispose(); }
    }

    private static bool BridgeAlive(Request request)
    {
        if (request == null) return false;
        try
        {
            using (var bridge = Process.GetProcessById(request.Pid))
                return !bridge.HasExited && bridge.StartTime.ToUniversalTime().Ticks == request.StartTicks &&
                    (bridge.ProcessName == "Endfield" || bridge.ProcessName == "EndfieldSteamBridge");
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool LauncherRunning()
    {
        return LauncherProcesses().Count != 0;
    }
    private static Dictionary<int, long> LauncherProcesses()
    {
        var result = new Dictionary<int, long>();
        foreach (string name in new[] { "Launcher", "Games", "QtWebEngineProcess" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        string path = ImagePath(process.Id);
                        if (path != null && path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase))
                            result[process.Id] = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
        return result;
    }
    private static void TrackLaunchers(Dictionary<int, long> before, Dictionary<int, long> owned)
    {
        foreach (var pair in LauncherProcesses())
            if (!before.ContainsKey(pair.Key) || before[pair.Key] != pair.Value) owned[pair.Key] = pair.Value;
    }
    private static void CloseOwnedLaunchers(Dictionary<int, long> owned)
    {
        // This bridge's newly opened launcher must finish as well, otherwise
        // Steam keeps the app running after the actual game has exited.
        foreach (var pair in owned)
        {
            try
            {
                using (var process = Process.GetProcessById(pair.Key))
                {
                    if (process.HasExited || CreationTicks(process.Id) != pair.Value) continue;
                    string path = ImagePath(process.Id);
                    if (!SessionPath(path)) continue;
                    string name = process.ProcessName;
                    process.Kill();
                    process.WaitForExit(3000);
                    Log("cleanup", "关闭本轮进程 " + name + " PID=" + process.Id);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception error) { Log("bridge", "启动器收尾失败：" + error.Message); }
        }
    }
    private static void RemoveLauncherOverlay(Dictionary<int, long> owned)
    {
        // Steam creates a UI context in both the Qt launcher and the game. Its
        // notification queue selected the launcher, hiding screenshot toasts.
        // Retire only our launcher's UI once the real game's UI exists. Valve's
        // renderer stays loaded, preserving its normal child-process hooks.
        try
        {
            var launcherUis = new List<int>();
            bool gameUiReady = false;
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='gameoverlayui64.exe'"))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        string command = Convert.ToString(row["CommandLine"]);
                        if (!Regex.IsMatch(command, @"(?:^|\s)-gameid\s+4732690(?:\s|$)")) continue;
                        var match = Regex.Match(command, @"(?:^|\s)-pid\s+(\d+)(?:\s|$)");
                        if (!match.Success) continue;
                        int target = Int32.Parse(match.Groups[1].Value);
                        int ui = Convert.ToInt32(row["ProcessId"]);
                        string path = ImagePath(target);
                        if (String.Equals(path, Game, StringComparison.OrdinalIgnoreCase)) gameUiReady = true;
                        else if (owned.ContainsKey(target))
                        {
                            using (var launcher = Process.GetProcessById(target))
                                if (launcher.StartTime.ToUniversalTime().Ticks == owned[target] && path != null &&
                                    path.StartsWith(Path.GetDirectoryName(Launcher) + "\\", StringComparison.OrdinalIgnoreCase)) launcherUis.Add(ui);
                        }
                    }
            if (!gameUiReady) return;
            foreach (int ui in launcherUis)
                using (var process = Process.GetProcessById(ui))
                {
                    if (process.ProcessName != "gameoverlayui64") continue;
                    process.Kill();
                    Log("bridge", "关闭启动器重复叠加层 PID=" + ui + "；保留国服叠加层和截图通知。");
                }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (ManagementException error) { Log("bridge", "叠加层状态检查失败：" + error.Message); }
        catch (System.ComponentModel.Win32Exception error) { Log("bridge", "叠加层收尾失败：" + error.Message); }
    }
    private static bool GameRunning()
    {
        int self = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName("Endfield"))
            using (process)
            {
                if (process.Id == self) continue;
                if (String.Equals(ImagePath(process.Id), Game, StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }
    private static string ImagePath(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(2048);
            int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    private static long CreationTicks(int processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            long created, exited, kernel, user;
            return GetProcessTimes(handle, out created, out exited, out kernel, out user) ? DateTime.FromFileTimeUtc(created).Ticks : 0;
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    private static void Status(Request request, string state, string message)
    {
        WriteJson(StatusPath, new { id = request.Id, state = state, message = message, time = DateTime.Now.ToString("s") });
        Log("assist", message);
    }
    private static void WriteJson(string path, object value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
    private static void Log(string role, string message)
    {
        using (var mutex = new Mutex(false, @"Local\SteamEndfieldBridgeLog"))
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            try { File.AppendAllText(Path.Combine(Root, "bridge.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + role + "] " + message + Environment.NewLine); }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

}
