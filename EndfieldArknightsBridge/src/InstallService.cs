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
            GameChannel.RequireSupported(channelId);
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
        // Steam steady-state flags that mean an update is queued, running or paused.
        private const uint UpdateFlags = 2 | 256 | 512 | 1024;
        // A new Steam build makes the managed manifest stale, so Steam would download
        // the international depots into the bridge directory. Point the manifest at the
        // current public build instead, so Steam treats the bridge as up to date.
        public string Sync(Installation state, Metadata metadata, string frontend)
        {
            ValidateState(state);
            if (state.Phase != "enabled") throw new InvalidOperationException("上次操作尚未完成，请先点击“撤回载入国服”。");
            if (!String.Equals(metadata.Executable, state.Executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Steam 启动文件已变化，请更新工具后重试。");
            if (!File.Exists(state.ManifestPath)) throw new IOException("找不到 Steam 安装记录，请先点击“撤回载入国服”后重新载入。");
            var root = KeyValues.Parse(File.ReadAllText(state.ManifestPath));
            var acf = KeyValues.Child(root, "AppState");
            if (acf == null || !String.Equals(KeyValues.String(acf, "installdir"), Path.GetFileName(state.BridgeDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Steam 安装记录已被其他程序更改，请保留备份并查看使用说明后处理。");
            string marker = Path.Combine(state.BridgeDirectory, ".endfield-steam-bridge");
            if (!File.Exists(marker) || File.ReadAllText(marker) != state.Token) throw new IOException("桥接目录标记不匹配，已停止修改：" + state.BridgeDirectory);

            string previous = KeyValues.String(acf, "buildid");
            var depots = new Dictionary<string, object>();
            foreach (var depot in metadata.Depots) depots[depot.Key] = new Dictionary<string, object> { { "manifest", depot.Value }, { "size", "0" } };
            acf["buildid"] = metadata.Build; acf["InstalledDepots"] = depots; acf["StateFlags"] = "4"; acf["UpdateResult"] = "0";
            foreach (string key in new[] { "BytesToDownload", "BytesDownloaded", "BytesToStage", "BytesStaged", "ScheduledAutoUpdate" }) acf[key] = "0";
            acf.Remove("TargetBuildID"); acf.Remove("FullValidateAfterNextUpdate");
            acf["LastUpdated"] = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
            string pending = state.ManifestPath + ".esb-new";
            File.WriteAllText(pending, KeyValues.Render(root), new UTF8Encoding(false));
            try { AtomicCopy(pending, state.ManifestPath); } finally { File.Delete(pending); }

            // Drop Steam's partial international download for this app only.
            string apps = Path.GetDirectoryName(state.ManifestPath), id = App.AppId.ToString();
            string downloading = Path.Combine(apps, "downloading");
            if (Directory.Exists(Path.Combine(downloading, id))) Directory.Delete(Path.Combine(downloading, id), true);
            if (Directory.Exists(downloading)) foreach (string patch in Directory.GetFiles(downloading, "state_" + id + "_*.patch")) File.Delete(patch);
            if (Directory.Exists(Path.Combine(apps, "temp", id))) Directory.Delete(Path.Combine(apps, "temp", id), true);

            // A finished Steam update may already have replaced the bridge entry point.
            string exe = Path.Combine(state.BridgeDirectory, state.Executable);
            bool repaired = false, haveFrontend = frontend != null && File.Exists(frontend);
            if (!File.Exists(exe) || (haveFrontend && !SameBytes(exe, frontend)))
            {
                if (!haveFrontend) throw new IOException("桥接程序已被 Steam 更新覆盖，且找不到备用副本。请点击“撤回载入国服”后重新载入。");
                AtomicCopy(frontend, exe); repaired = true;
            }
            int extra = Directory.GetFileSystemEntries(state.BridgeDirectory).Count(p => !Detection.Same(p, marker) && !Detection.Same(p, exe));

            var message = new StringBuilder(previous == metadata.Build
                ? "Steam 缓存中的版本未变化（" + metadata.Build + "），已清除更新标记。若 Steam 仍提示更新，请先打开终末地库页面等待片刻，再同步一次。"
                : "已同步到 Steam 版本 " + metadata.Build + "，无需下载国际服。");
            if (repaired) message.Append(" 已修复被覆盖的桥接程序。");
            if (extra > 0) message.Append(" 桥接目录中有 " + extra + " 项 Steam 下载的国际服文件，可手动删除：" + state.BridgeDirectory);
            return message.ToString();
        }
        // Steam writes a newly published build into the manifest as soon as it learns of it.
        public static bool UpdateQueued(string manifest, out string target)
        {
            target = null;
            if (!File.Exists(manifest)) return false;
            var acf = KeyValues.Child(KeyValues.Parse(File.ReadAllText(manifest)), "AppState");
            if (acf == null) return false;
            target = KeyValues.String(acf, "TargetBuildID") ?? "";
            uint flags;
            if (UInt32.TryParse(KeyValues.String(acf, "StateFlags"), out flags) && (flags & UpdateFlags) != 0) return true;
            string bytes = KeyValues.String(acf, "BytesToDownload");
            return !String.IsNullOrEmpty(bytes) && bytes != "0";
        }
        // True when Steam wants to download the international client over the bridge.
        public static bool UpdatePending(Installation state, string frontend, Metadata latest)
        {
            if (state == null || state.Phase != "enabled" || !File.Exists(state.ManifestPath)) return false;
            string target;
            if (UpdateQueued(state.ManifestPath, out target)) return true;
            var acf = KeyValues.Child(KeyValues.Parse(File.ReadAllText(state.ManifestPath)), "AppState");
            if (latest != null && !String.IsNullOrEmpty(latest.Build) && KeyValues.String(acf, "buildid") != latest.Build) return true;
            string exe = Path.Combine(state.BridgeDirectory, state.Executable ?? "Endfield.exe");
            return frontend != null && File.Exists(frontend) && (!File.Exists(exe) || !SameBytes(exe, frontend));
        }
        private static bool SameBytes(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
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
            GameChannel.RequireSupported(scan.ChannelId);
            // Recheck ownership immediately before changing Steam, including account switches.
            var account = SteamAccount.ProbeIsolated(scan.SteamRoot);
            if (!account.Known || !account.Owned) throw new InvalidOperationException(account.Error ?? "当前 Steam 账号尚未拥有《明日方舟：终末地》，请先在商店免费添加入库。");
            bool ready;
            string clientStatus = Detection.ClientStatus(scan.Launcher, scan.Game, scan.ChannelId, out ready);
            if (!ready) throw new InvalidOperationException(clientStatus);
            bool includeArknights = true;
            if (!Detection.ArknightsReady(scan.ArknightsLauncher, scan.ArknightsGame))
                throw new InvalidOperationException("双游戏版需要安装明日方舟官服客户端。若只玩终末地，请使用终末地版程序。");
            string manifest = FindManifest(scan.SteamRoot);
            bool legacy = IsLegacy(manifest);
            EnsureGameClosed();
            using (var restart = SteamSession.Capture(scan.SteamRoot))
            {
            progress("正在正常退出 Steam…"); StopSteam(scan.SteamRoot);
            // Steam writes appinfo.vdf on a normal exit; a fresh or cleaned cache only exists afterwards.
            Metadata metadata;
            try { metadata = KeyValues.ReadAppInfo(Path.Combine(scan.SteamRoot, "appcache", "appinfo.vdf")); }
            catch { restart.Start(); throw; }
            var files = new InstallFiles(App.Home); Installation state = null; bool taskCreated = false;
            try
            {
                progress("备份原始状态并安装桥接…");
                Directory.CreateDirectory(Path.GetDirectoryName(App.RuntimeExe));
                if (!Detection.Same(System.Reflection.Assembly.GetExecutingAssembly().Location, App.RuntimeExe)) File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, App.RuntimeExe, true);
                App.ExtractFrontend();
                state = files.Prepare(scan.SteamRoot, manifest, App.FrontendExe, metadata, account.SteamId, legacy, scan.ChannelId);
                App.Save(App.ConfigPath, new RuntimeConfig { ChannelId = state.ChannelId, Launcher = Path.GetFullPath(scan.Launcher), Game = Path.GetFullPath(scan.Game), SteamRoot = scan.SteamRoot, TaskName = state.TaskName,
                    ArknightsLauncher = includeArknights ? Path.GetFullPath(scan.ArknightsLauncher) : null, ArknightsGame = includeArknights ? Path.GetFullPath(scan.ArknightsGame) : null });
                RegisterTask(state.TaskName); taskCreated = true;
                ScheduledTasks.Register(WatchTaskName, WatchTaskXml(App.RuntimeExe, App.Home));
                files.Commit(state);
            }
            catch
            {
                // Roll back only this installation; Steam Windows settings are untouched.
                DeleteTask(WatchTaskName);
                if (taskCreated) DeleteTask(state.TaskName);
                if (state != null) files.Rollback(state);
                throw;
            }
            finally { restart.Start(); }
            try { ScheduledTasks.Run(WatchTaskName); } catch (InvalidOperationException) { } // Starts at the next logon otherwise.
            progress("已载入国服。请等待 Steam 启动完成，再从游戏库开始游戏。");
            }
        }
        public static void Restore(Action<string> progress)
        {
            var state = App.Load<Installation>(App.StatePath);
            if (state.TaskName != App.TaskName) throw new InvalidOperationException("该载入记录属于其他 Windows 用户，请用原用户卸载。");
            using (var restart = SteamSession.Capture(state.SteamRoot))
            {
            EnsureGameClosed(); progress("正在正常退出 Steam…"); StopSteam(state.SteamRoot);
            try
            {
                var files = new InstallFiles(App.Home);
                progress("正在还原 Steam 安装状态…"); files.Restore(state);
                DeleteTask(state.TaskName); DeleteTask(WatchTaskName);
                if (state.SteamStartup != null && state.SteamStartup.TaskName != state.SteamLaunchTaskName) DeleteTask(state.SteamStartup.TaskName);
                if (!String.IsNullOrEmpty(state.SteamLaunchTaskName)) DeleteTask(state.SteamLaunchTaskName);
                SteamWindowsSettings.RestoreStartup(state.SteamStartup, progress);
                SteamWindowsSettings.RestoreCompatibility(state.SteamCompatibility, progress);
                files.FinishRestore();
                if (File.Exists(App.ConfigPath)) File.Delete(App.ConfigPath);
            }
            finally { restart.Start(); }
            progress(state.WasInstalled ? "已撤回国服载入，恢复原来的 Steam 客户端。" : "已撤回国服载入，Steam 将恢复为“安装”。");
            }
        }
        internal static string WatchTaskName { get { return App.TaskName + "-update-watch"; } }
        // Steam flags the manifest when a new international build is published and keeps
        // showing "Update" until it restarts. Sync then, while no game or manager is open.
        public static int Watch()
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\EndfieldSteamBridgeUpdateWatch", out created))
            {
                if (!created) return 0;
                string attempted = null; var retry = DateTime.MinValue;
                while (File.Exists(App.StatePath))
                {
                    Thread.Sleep(3000);
                    try
                    {
                        var state = App.Load<Installation>(App.StatePath);
                        if (state.TaskName != App.TaskName) return 0;
                        string target;
                        if (state.Phase != "enabled" || !InstallFiles.UpdateQueued(state.ManifestPath, out target)) continue;
                        // A build that is still flagged after one sync needs the manual button.
                        if (target == attempted || DateTime.UtcNow < retry || Busy()) continue;
                        attempted = target;
                        Sync(delegate(string message) { });
                    }
                    catch (Exception) { attempted = null; retry = DateTime.UtcNow.AddMinutes(10); }
                }
            }
            return 0;
        }
        private static bool Busy()
        {
            Mutex manager;
            if (Mutex.TryOpenExisting(@"Local\EndfieldSteamBridgeManager", out manager)) { manager.Dispose(); return true; }
            foreach (string name in new[] { "Endfield", "Arknights", "Games", "Launcher" }) foreach (var p in Process.GetProcessesByName(name)) using (p) return true;
            // Never restart Steam under another running Steam game.
            if (!SteamRunning()) return false;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                return key != null && Convert.ToInt64(key.GetValue("RunningAppID") ?? 0) != 0;
        }
        internal static string WatchTaskXml(string executable, string workingDirectory)
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>Endfield Steam Bridge: keep Steam from downloading the international client after game updates.</Description></RegistrationInfo><Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + sid + "</UserId></LogonTrigger></Triggers><Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings><Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(executable) + "</Command><Arguments>--watch</Arguments><WorkingDirectory>" + SecurityElement.Escape(workingDirectory) + "</WorkingDirectory></Exec></Actions></Task>";
        }
        public static void Sync(Action<string> progress)
        {
            var state = App.Load<Installation>(App.StatePath);
            if (state.TaskName != App.TaskName) throw new InvalidOperationException("该载入记录属于其他 Windows 用户，请用原用户操作。");
            EnsureGameClosed();
            using (var restart = SteamSession.Capture(state.SteamRoot))
            {
            // Steam flushes the latest build metadata to appinfo.vdf on a normal exit.
            progress("正在正常退出 Steam…"); StopSteam(state.SteamRoot);
            string summary;
            try
            {
                progress("正在读取 Steam 最新版本并更新安装记录…");
                var metadata = KeyValues.ReadAppInfo(Path.Combine(state.SteamRoot, "appcache", "appinfo.vdf"));
                summary = new InstallFiles(App.Home).Sync(state, metadata, App.FrontendExe);
            }
            finally { restart.Start(); }
            progress(summary);
            }
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
            foreach (string name in new[] { "Endfield", "Arknights", "Games", "Launcher" }) foreach (var p in Process.GetProcessesByName(name)) using (p)
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
        private static void RegisterTask(string name)
        { ScheduledTasks.Register(name, AssistantTaskXml(App.RuntimeExe, App.Home)); }
        internal static string AssistantTaskXml(string executable, string workingDirectory)
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>Endfield Steam Bridge: official launcher assistant and session cleanup.</Description></RegistrationInfo><Triggers/><Principals><Principal id=\"Author\"><UserId>" + sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings><Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(executable) + "</Command><Arguments>--assist</Arguments><WorkingDirectory>" + SecurityElement.Escape(workingDirectory) + "</WorkingDirectory></Exec></Actions></Task>";
            return xml;
        }
        private static void DeleteTask(string name) { ScheduledTasks.Delete(name); }
    }
}
