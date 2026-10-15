using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace EndfieldSteamBridge
{
    internal static class Detection
    {
        public static string FindSteam()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string path = key == null ? null : Convert.ToString(key.GetValue("SteamPath"));
                if (!String.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "steam.exe"))) return Path.GetFullPath(path);
            }
            throw new InvalidOperationException("未找到当前 Windows 用户的 Steam，请先安装并登录 Steam。");
        }
        public static List<string> Libraries(string steam)
        {
            var paths = new List<string> { steam };
            string file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
            {
                var folders = KeyValues.Child(KeyValues.Parse(File.ReadAllText(file)), "libraryfolders");
                if (folders != null) foreach (var folder in folders)
                {
                    uint id; if (!UInt32.TryParse(folder.Key, out id)) continue;
                    string path = KeyValues.String(folder.Value as Dictionary<string, object>, "path") ?? folder.Value as string;
                    if (!String.IsNullOrEmpty(path) && Directory.Exists(path) && !paths.Exists(p => Same(p, path))) paths.Add(Path.GetFullPath(path));
                }
            }
            return paths;
        }
        public static bool Same(string a, string b) { return a != null && b != null && String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        public static string FindLauncher()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetSubKeyNames()) using (var item = key.OpenSubKey(name))
                        {
                            if (item == null || Convert.ToString(item.GetValue("DisplayName")) != "鹰角启动器") continue;
                            string location = Convert.ToString(item.GetValue("InstallLocation"));
                            if (!String.IsNullOrEmpty(location) && ValidLauncher(Path.Combine(location, "Launcher.exe"))) return Path.Combine(location, "Launcher.exe");
                        }
                    }
            using (var key = Registry.ClassesRoot.OpenSubKey(@"hypergryphlauncher-abYeZZ16BPluCFyT\shell\open\command"))
            {
                string value = key == null ? "" : Convert.ToString(key.GetValue(""));
                if (value.StartsWith("\"")) { int end = value.IndexOf('"', 1); if (end > 1 && ValidLauncher(value.Substring(1, end - 1))) return value.Substring(1, end - 1); }
            }
            return null;
        }
        public static bool ValidLauncher(string path)
        { return !String.IsNullOrEmpty(path) && File.Exists(path) && Path.GetFileName(path).Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase); }
        public static bool ValidGame(string path)
        {
            try { return ValidGameFiles(path); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        private static bool ValidGameFiles(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path) || !Path.GetFileName(path).Equals("Endfield.exe", StringComparison.OrdinalIgnoreCase)) return false;
            string root = Path.GetDirectoryName(path);
            if (!File.Exists(Path.Combine(root, "UnityPlayer.dll")) || !File.Exists(Path.Combine(root, "Endfield_Data", "app.info")) || !File.Exists(Path.Combine(root, "sdkdata", "sdk_hgsdk_config.gryph"))) return false;
            string info = File.ReadAllText(Path.Combine(root, "Endfield_Data", "app.info"));
            if (!info.Contains("Hypergryph") || !info.Contains("Endfield")) return false;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.BaseStream.Length < 64 || r.ReadUInt16() != 0x5a4d) return false;
                r.BaseStream.Position = 0x3c; int offset = r.ReadInt32();
                if (offset < 64 || offset + 6 > r.BaseStream.Length) return false;
                r.BaseStream.Position = offset; return r.ReadUInt32() == 0x4550 && r.ReadUInt16() == 0x8664;
            }
        }
        public static DetectionResult Scan(string launcher, string game)
        {
            var result = new DetectionResult();
            try
            {
                if (File.Exists(App.StatePath)) result.Installation = App.Load<Installation>(App.StatePath);
                result.SteamRoot = FindSteam(); result.Account = SteamAccount.ProbeIsolated(result.SteamRoot);
                RuntimeConfig config = File.Exists(App.ConfigPath) ? App.Load<RuntimeConfig>(App.ConfigPath) : null;
                result.Launcher = launcher ?? (config != null && ValidLauncher(config.Launcher) ? config.Launcher : FindLauncher());
                result.Game = game ?? (config != null && ValidGame(config.Game) ? config.Game : null);
                if (result.Game == null && result.Launcher != null)
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(result.Launcher), "games", "Arknights Endfield", "Endfield.exe");
                    if (ValidGame(candidate)) result.Game = candidate;
                }
                result.GameStatus = ValidLauncher(result.Launcher) && ValidGame(result.Game) ? "国服文件结构可用" : "请指定鹰角启动器与国服 Endfield.exe";
            }
            catch (Exception e) { result.Error = e.Message; }
            return result;
        }
    }
}
