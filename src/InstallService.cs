using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;

namespace EndfieldSteamBridge
{
    // Filesystem transaction is separate from Steam/Task Scheduler for fixture tests.
    internal sealed class InstallFiles
    {
        private readonly string home;
        public string StatePath { get { return Path.Combine(home, "installation.json"); } }
        public InstallFiles(string dataDirectory) { home = dataDirectory; }
        public Installation Prepare(string steam, string manifest, string exe, Metadata metadata, ulong owner, bool legacy, string channelId = null)
        {
            if (File.Exists(StatePath)) throw new InvalidOperationException("已有载入记录，请先点击“撤回载入国服”。");
            if (!String.Equals(metadata.Executable, "Endfield.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Steam 启动文件已变化，请更新工具后重试。");
            string library = Path.GetDirectoryName(manifest);
            string directory = Path.Combine(library, "common", "Endfield Steam CN Bridge");
            if (Directory.Exists(directory)) throw new IOException("桥接目录已存在，请先处理旧安装：" + directory);
            string backup = Path.Combine(home, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var state = new Installation { ChannelId = GameChannel.Get(channelId).Id, Version = App.Version, SteamRoot = steam, ManifestPath = manifest,
                OriginalManifestBackup = Path.Combine(backup, "original.acf"), PreviousManifestBackup = Path.Combine(backup, "previous.acf"),
                ManagedManifestBackup = Path.Combine(backup, "managed.acf"), BridgeDirectory = directory,
                Token = Guid.NewGuid().ToString("N"), TaskName = App.TaskName, WasInstalled = File.Exists(manifest) && !legacy, Phase = "prepared", Executable = metadata.Executable };
            if (File.Exists(manifest)) File.Copy(manifest, state.PreviousManifestBackup);
            if (state.WasInstalled) File.Copy(manifest, state.OriginalManifestBackup);
            App.Save(StatePath, state);
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".endfield-steam-bridge"), state.Token);
                File.Copy(exe, Path.Combine(directory, state.Executable));
                var depots = new Dictionary<string, object>();
                foreach (var depot in metadata.Depots) depots[depot.Key] = new Dictionary<string, object> { { "manifest", depot.Value }, { "size", "0" } };
                var appState = new Dictionary<string, object> {
                    { "appid", App.AppId.ToString() }, { "Universe", "1" }, { "name", "Arknights: Endfield" }, { "StateFlags", "4" },
                    { "installdir", Path.GetFileName(directory) }, { "LastUpdated", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString() },
                    { "SizeOnDisk", new FileInfo(exe).Length.ToString() }, { "buildid", metadata.Build }, { "LastOwner", owner.ToString() },
                    { "UpdateResult", "0" }, { "BytesToDownload", "0" }, { "BytesDownloaded", "0" }, { "BytesToStage", "0" }, { "BytesStaged", "0" },
                    { "AutoUpdateBehavior", "1" }, { "AllowOtherDownloadsWhileRunning", "0" }, { "ScheduledAutoUpdate", "0" },
                    { "InstalledDepots", depots }, { "UserConfig", new Dictionary<string, object> { { "language", "schinese" } } },
                    { "MountedConfig", new Dictionary<string, object> { { "language", "schinese" } } }
                };
                File.WriteAllText(state.ManagedManifestBackup, KeyValues.Render(new Dictionary<string, object> { { "AppState", appState } }), new UTF8Encoding(false));
                return state;
            }
            catch { Rollback(state); throw; }
        }
        public void Commit(Installation state)
        {
            AtomicCopy(state.ManagedManifestBackup, state.ManifestPath);
            state.Phase = "enabled"; App.Save(StatePath, state);
        }
        public void Rollback(Installation state)
        {
            if (File.Exists(state.PreviousManifestBackup)) AtomicCopy(state.PreviousManifestBackup, state.ManifestPath);
            else if (File.Exists(state.ManifestPath)) File.Delete(state.ManifestPath);
            RemoveBridge(state);
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        public void Restore(Installation state)
        {
            ValidateState(state);
            if (File.Exists(state.ManifestPath) && state.Phase != "restoring")
            {
                var acf = KeyValues.Child(KeyValues.Parse(File.ReadAllText(state.ManifestPath)), "AppState");
                string install = KeyValues.String(acf, "installdir");
                if (state.Phase == "enabled" && !String.Equals(install, Path.GetFileName(state.BridgeDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Steam 安装记录已被其他程序更改，请保留备份并查看使用说明后处理。");
            }
            if (state.WasInstalled && !File.Exists(state.OriginalManifestBackup)) throw new IOException("找不到原始安装记录备份：" + state.OriginalManifestBackup);
            state.Phase = "restoring"; App.Save(StatePath, state);
            if (state.WasInstalled) AtomicCopy(state.OriginalManifestBackup, state.ManifestPath);
            else if (File.Exists(state.ManifestPath)) File.Delete(state.ManifestPath);
            RemoveBridge(state);
            // Keep the state until Task Scheduler cleanup succeeds, so retry is possible.
        }
        public void FinishRestore() { if (File.Exists(StatePath)) File.Delete(StatePath); }
        private static void AtomicCopy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + ".esb-tmp"; File.Copy(source, temp, true);
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        }
        private static void ValidateState(Installation state)
        {
            string expected = Path.Combine(Path.GetDirectoryName(state.ManifestPath), "common", "Endfield Steam CN Bridge");
            if (!Detection.Same(state.BridgeDirectory, expected) || Path.GetFileName(state.ManifestPath) != "appmanifest_4732690.acf" || state.Executable != "Endfield.exe")
                throw new InvalidDataException("本地载入记录路径不正确，已停止修改。");
        }
        private static void RemoveBridge(Installation state)
        {
            ValidateState(state);
            if (!Directory.Exists(state.BridgeDirectory)) return;
            string marker = Path.Combine(state.BridgeDirectory, ".endfield-steam-bridge");
            if (!File.Exists(marker) || File.ReadAllText(marker) != state.Token) throw new IOException("桥接目录标记不匹配，已保留目录：" + state.BridgeDirectory);
            string exe = Path.Combine(state.BridgeDirectory, state.Executable);
            if (File.Exists(exe)) File.Delete(exe);
            File.Delete(marker);
            if (Directory.GetFileSystemEntries(state.BridgeDirectory).Length == 0) Directory.Delete(state.BridgeDirectory);
        }
    }
    internal static class InstallService
    {
        public static void Enable(DetectionResult scan, Action<string> progress)
        {
            // Recheck ownership immediately before changing Steam, including account switches.
            var account = SteamAccount.ProbeIsolated(scan.SteamRoot);
            if (!account.Known || !account.Owned) throw new InvalidOperationException(account.Error ?? "当前 Steam 账号尚未拥有《明日方舟：终末地》，请先在商店免费添加入库。");
            bool ready;
            string clientStatus = Detection.ClientStatus(scan.Launcher, scan.Game, scan.ChannelId, out ready);
            if (!ready) throw new InvalidOperationException(clientStatus);
            var metadata = KeyValues.ReadAppInfo(Path.Combine(scan.SteamRoot, "appcache", "appinfo.vdf"));
            string manifest = FindManifest(scan.SteamRoot);
            bool legacy = IsLegacy(manifest);
            EnsureGameClosed();
            progress("正在正常退出 Steam…"); StopSteam(scan.SteamRoot);
            var files = new InstallFiles(App.Home); Installation state = null; bool taskCreated = false;
            try
            {
                progress("备份原始状态并安装桥接…");
                Directory.CreateDirectory(Path.GetDirectoryName(App.RuntimeExe));
                if (!Detection.Same(System.Reflection.Assembly.GetExecutingAssembly().Location, App.RuntimeExe)) File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, App.RuntimeExe, true);
                state = files.Prepare(scan.SteamRoot, manifest, App.RuntimeExe, metadata, account.SteamId, legacy, scan.ChannelId);
                App.Save(App.ConfigPath, new RuntimeConfig { ChannelId = state.ChannelId, Launcher = Path.GetFullPath(scan.Launcher), Game = Path.GetFullPath(scan.Game), SteamRoot = scan.SteamRoot, TaskName = state.TaskName });
                RegisterTask(state.TaskName); taskCreated = true;
                files.Commit(state);
            }
            catch
            {
                if (state != null) files.Rollback(state);
                if (taskCreated) DeleteTask(state.TaskName);
                throw;
            }
            finally { StartSteam(scan.SteamRoot); }
            progress("已载入国服。请等待 Steam 启动完成，再从游戏库开始游戏。");
        }
        public static void Restore(Action<string> progress)
        {
            var state = App.Load<Installation>(App.StatePath);
            if (state.TaskName != App.TaskName) throw new InvalidOperationException("该载入记录属于其他 Windows 用户，请用原用户卸载。");
            EnsureGameClosed(); progress("正在正常退出 Steam…"); StopSteam(state.SteamRoot);
            try
            {
                var files = new InstallFiles(App.Home);
                progress("正在还原 Steam 安装状态…"); files.Restore(state);
                DeleteTask(state.TaskName); files.FinishRestore();
                if (File.Exists(App.ConfigPath)) File.Delete(App.ConfigPath);
            }
            finally { StartSteam(state.SteamRoot); }
            progress(state.WasInstalled ? "已撤回国服载入，恢复原来的 Steam 客户端。" : "已撤回国服载入，Steam 将恢复为“安装”。");
        }
        public static string FindManifest(string steam)
        {
            var matches = Detection.Libraries(steam).Select(p => Path.Combine(p, "steamapps", "appmanifest_4732690.acf")).Where(File.Exists).ToArray();
            if (matches.Length > 1) throw new IOException("多个 Steam 库中都有终末地安装记录，请先在 Steam 中处理重复安装。");
            return matches.Length == 1 ? matches[0] : Path.Combine(steam, "steamapps", "appmanifest_4732690.acf");
        }
        public static bool IsLegacy(string manifest)
        {
            if (!File.Exists(manifest)) return false;
            var state = KeyValues.Child(KeyValues.Parse(File.ReadAllText(manifest)), "AppState");
            string directory = KeyValues.String(state, "installdir");
            if (directory != "Endfield CN Bridge") return false;
            string exe = Path.Combine(Path.GetDirectoryName(manifest), "common", directory, "Endfield.exe");
            if (!File.Exists(exe)) return false;
            try { string name = System.Reflection.AssemblyName.GetAssemblyName(exe).Name; return name == "EndfieldCNV3" || name == "EndfieldCNV2" || name == "EndfieldCN"; }
            catch (BadImageFormatException) { return false; }
        }
        private static void EnsureGameClosed()
        {
            // Steam's registry Running flags can remain set after a game exits.
            // Check actual Endfield/launcher processes here; StopSteam must still
            // complete normally before any Steam installation files are changed.
            foreach (string name in new[] { "Endfield", "Games", "Launcher" }) foreach (var p in Process.GetProcessesByName(name)) using (p)
                throw new InvalidOperationException("请先完全退出国服游戏与鹰角启动器（包括托盘），再操作。");
        }
        private static bool SteamRunning()
        {
            bool found = false; foreach (var p in Process.GetProcessesByName("steam")) using (p) found = true; return found;
        }
        private static void StopSteam(string steam)
        {
            if (!SteamRunning()) return;
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "-shutdown") { UseShellExecute = false })) { }
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (SteamRunning() && DateTime.UtcNow < deadline) Thread.Sleep(400);
            if (SteamRunning()) throw new InvalidOperationException("Steam 尚未正常退出。请处理 Steam 的提示，完全退出后重试。");
            Thread.Sleep(700);
        }
        private static void StartSteam(string steam)
        { Process.Start(new ProcessStartInfo(Path.Combine(steam, "steam.exe"), "") { UseShellExecute = true }); }
        private static void RegisterTask(string name)
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>Endfield Steam Bridge: official launcher assistant and session cleanup.</Description></RegistrationInfo><Triggers/><Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings><Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(App.RuntimeExe) + "</Command><Arguments>--assist</Arguments><WorkingDirectory>" + SecurityElement.Escape(App.Home) + "</WorkingDirectory></Exec></Actions></Task>";
            string file = Path.Combine(App.Home, "task.xml"); File.WriteAllText(file, xml, Encoding.Unicode);
            try { RunTaskCommand("/create /tn " + Arguments.Quote(name) + " /xml " + Arguments.Quote(file) + " /f", false); }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
        private static void DeleteTask(string name)
        {
            // A missing task is already uninstalled; query before deleting.
            if (RunTaskCommand("/query /tn " + Arguments.Quote(name), true) == 0)
                RunTaskCommand("/delete /tn " + Arguments.Quote(name) + " /f", false);
        }
        private static int RunTaskCommand(string arguments, bool allowFailure)
        {
            using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0 && !allowFailure) throw new InvalidOperationException("计划任务配置失败：" + output + error);
                return p.ExitCode;
            }
        }
    }
}
