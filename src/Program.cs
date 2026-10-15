using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Threading;

namespace EndfieldSteamBridge
{
    internal static class Arguments
    {
        public static string Quote(string s)
        {
            var b = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && (args[0] == "--enable" || args[0] == "--restore"))
            {
                try
                {
                    Action<string> progress = message => App.Save(args[1], new { success = false, message = message });
                    if (args[0] == "--enable") InstallService.Enable(Detection.Scan(null, null), progress); else InstallService.Restore(progress);
                    App.Save(args[1], new { success = true, message = "completed" }); return 0;
                }
                catch (Exception e) { App.Save(args[1], new { success = false, message = e.ToString() }); return 1; }
            }
            try
            {
                if (args.Length == 2 && args[0] == "--probe") { App.Save(args[1], SteamAccount.Probe(Detection.FindSteam())); return 0; }
                if (args.Length == 2 && args[0] == "--diagnose") { App.Save(args[1], Detection.Scan(null, null)); return 0; }
                if (args.Length == 2 && args[0] == "--metadata") { App.Save(args[1], KeyValues.ReadAppInfo(Path.Combine(Detection.FindSteam(), "appcache", "appinfo.vdf"))); return 0; }
                if (args.Length == 1 && args[0] == "--assist") return BridgeRuntime.RunAssist();
                if (Path.GetFileNameWithoutExtension(Application.ExecutablePath).Equals("Endfield", StringComparison.OrdinalIgnoreCase) || (args.Length == 1 && args[0] == "--bridge"))
                    return BridgeRuntime.Run();
                bool created;
                using (var mutex = new Mutex(true, @"Local\EndfieldSteamBridgeManager", out created))
                {
                    if (!created) return 0;
                    using (var form = new MainForm())
                    {
                        if (args.Length == 2 && args[0] == "--preview") { form.SavePreview(args[1]); return 0; }
                        Application.Run(form);
                    }
                }
                return 0;
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Endfield Steam Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}
