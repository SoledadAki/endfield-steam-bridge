using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace EndfieldSteamBridge
{
    public sealed class SteamCompatibilityChange
    {
        public bool AllUsers { get; set; }
        public string Executable { get; set; }
        public string Original { get; set; }
        public int OriginalKind { get; set; }
        public string Applied { get; set; }
    }
    public sealed class SteamStartupBackup
    {
        public string Original { get; set; }
        public int OriginalKind { get; set; }
        public string Arguments { get; set; }
        public string TaskName { get; set; }
    }
    // Read and restore legacy settings only. New installations never change Steam settings.
    internal static class SteamWindowsSettings
    {
        private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static void RestoreCompatibility(List<SteamCompatibilityChange> changes, Action<string> report)
        {
            if (changes == null) return;
            foreach (var change in changes)
            using (var root = RegistryKey.OpenBaseKey(change.AllUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
            using (var key = root.CreateSubKey(Layers))
            {
                string current = key.GetValue(change.Executable, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!MayRestore(current, change.Applied, change.Original))
                { report("检测到手动更改，已保留兼容性设置：" + change.Executable); continue; }
                Write(key, change.Executable, change.Original, (RegistryValueKind)change.OriginalKind);
            }
        }
        internal static bool MayRestore(string current, string applied, string original)
        { return String.Equals(current, applied, StringComparison.Ordinal) || String.Equals(current, original, StringComparison.Ordinal); }
        private static void Write(RegistryKey key, string name, string value, RegistryValueKind kind)
        { if (value == null) key.DeleteValue(name, false); else key.SetValue(name, value, kind); }

        internal static void RestoreStartup(SteamStartupBackup backup, Action<string> report)
        {
            if (backup == null) return;
            using (var key = Registry.CurrentUser.CreateSubKey(Run))
            {
                object current = key.GetValue("Steam", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (current != null && !String.Equals(current as string, backup.Original, StringComparison.Ordinal))
                { report("Steam 开机启动项已有手动修改，已保留现有设置。"); return; }
                key.SetValue("Steam", backup.Original, (RegistryValueKind)backup.OriginalKind);
            }
        }
    }
}
