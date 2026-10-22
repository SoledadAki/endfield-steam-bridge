using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EndfieldSteamBridge
{
    internal static class CoreTests
    {
        private static int count;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        private static void Throws(Action action, string message)
        { bool threw = false; try { action(); } catch (IOException) { threw = true; } catch (InvalidOperationException) { threw = true; } Check(threw, message); }
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--run-task") { ScheduledTasks.Run(args[1]); return 0; }
            if (args.Length >= 1 && args[0] == "--scheduler-smoke") return SchedulerSmoke(args.Length == 2 ? args[1] : null);
            if (args.Length == 2 && args[0] == "--export-scripts")
            {
                App.Save(args[1], new Dictionary<string, string> {
                    { "official", GameChannel.Official.StartScript(true) },
                    { "officialReady", GameChannel.Official.StartScript(false) },
                    { "arknights", GameChannel.Official.StartScript(true, "arknights") }
                }); return 0;
            }
            if (args.Length == 4 && args[0] == "--launcher-check")
            {
                var channel = GameChannel.Get(args[1]); string launcher = Detection.FindLauncher(channel.Id);
                string endpoint = BridgeRuntime.FindGamePage(Int32.Parse(args[2]), launcher, channel);
                if (endpoint == null) { App.Save(args[3], new { channel = channel.Id, state = "no-matching-page" }); return 1; }
                using (var cdp = new BridgeRuntime.Cdp(endpoint)) App.Save(args[3], new { channel = channel.Id, state = cdp.Evaluate(channel.StartScript(false)) });
                return 0;
            }
            string root = Path.Combine(Path.GetTempPath(), "EndfieldBridge-tests-空 格-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                ChannelTests(root);
                DetectionAutoFillTests(root);
                Check(GameChannel.All.Length == 1 && GameChannel.All[0] == GameChannel.Official, "dual edition exposes official server only");
                GameChannel.RequireSupported(null); GameChannel.RequireSupported("official");
                Throws(() => GameChannel.RequireSupported("bilibili"), "dual edition rejects Bilibili channel before launch or install");
                Check(Detection.Scan(null, null, "bilibili").Error.Contains("仅支持"), "command-line detection rejects Bilibili without touching Steam");
                var unsupported = new InstallFiles(Path.Combine(root, "unsupported-channel"));
                Throws(() => unsupported.Prepare("unused", "unused", "unused", null, 1, false, "bilibili"), "Bilibili install rejected before filesystem changes");
                Check(!File.Exists(unsupported.StatePath), "rejected Bilibili install leaves no installation state");
                WindowsSettingsTests(root);
                Check(ScheduledTasks.Describe("创建", new System.Runtime.InteropServices.COMException("service unavailable", unchecked((int)0x80041315))).Contains("Task Scheduler"), "scheduler service failure gives actionable advice");
                Check(ScheduledTasks.Describe("创建", new System.Runtime.InteropServices.COMException("unspecified", unchecked((int)0x80004005))).Contains("0x80004005"), "scheduler preserves original HRESULT");
                Check(App.Json.Deserialize<RuntimeConfig>("{\"Game\":\"legacy\"}").ArknightsGame == null, "legacy installations retain single-game launch");
                Check(BridgeRuntime.AcceptSteamLaunch("4732690", null, false), "accept normal Steam app identity");
                Check(BridgeRuntime.AcceptSteamLaunch(null, "4732690", false), "accept SteamGameId when SteamAppId is missing");
                Check(BridgeRuntime.AcceptSteamLaunch("0", "4732690", false), "accept SteamGameId when SteamAppId is zero");
                Check(BridgeRuntime.AcceptSteamLaunch(null, null, true), "accept managed installation without environment or Steam process access");
                Check(BridgeRuntime.AcceptSteamLaunch("0", "0", true), "accept managed installation with zero identities");
                Check(BridgeRuntime.AcceptSteamLaunch("480", null, true), "accept managed installation with inherited app identity");
                Check(BridgeRuntime.AcceptSteamLaunch("480", "123", true), "accept managed installation with both identities stale");
                Check(BridgeRuntime.AcceptSteamLaunch("4732690", "480", false), "accept expected app identity despite conflicting game identity");
                Check(BridgeRuntime.AcceptSteamLaunch("480", "4732690", false), "accept expected game identity despite conflicting app identity");
                Check(BridgeRuntime.AcceptSteamLaunch(" 4732690 ", null, false), "trim Steam identity whitespace");
                Check(!BridgeRuntime.AcceptSteamLaunch(null, null, false), "reject unmanaged launch without any Steam identity");
                Check(!BridgeRuntime.AcceptSteamLaunch("480", "123", false), "reject unmanaged launch with unrelated identities");
                var vdf = KeyValues.Parse("//comment\n\"root\" { \"path\" \"D:\\\\游戏\\\\Endfield\" \"a\" { \"b\" \"quoted\\\"text\" } }");
                var roundtrip = KeyValues.Parse(KeyValues.Render(vdf));
                Check(KeyValues.String(KeyValues.Child(roundtrip, "root"), "path") == @"D:\游戏\Endfield", "VDF Unicode path roundtrip");
                Check(KeyValues.String(KeyValues.Child(KeyValues.Child(roundtrip, "root"), "a"), "b") == "quoted\"text", "VDF escaped quotes");
                Check(Arguments.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"", "Windows trailing slash quoting");
                string exe = Path.Combine(root, "source.exe"); File.WriteAllBytes(exe, new byte[] { 77, 90, 1, 2 });
                var metadata = new Metadata { Executable = "Endfield.exe", Build = "123", Depots = new Dictionary<string, string> { { "4732691", "987" } } };
                for (int mode = 0; mode < 5; mode++)
                {
                    string steam = Path.Combine(root, "Steam " + mode), home = Path.Combine(root, "data " + mode);
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf"); Directory.CreateDirectory(Path.GetDirectoryName(manifest));
                    byte[] original = Encoding.UTF8.GetBytes("\ufeff\"AppState\"\r\n{\r\n\t\"installdir\" \"Original international\"\r\n}\r\n");
                    string official = Path.Combine(steam, "steamapps", "common", "Original international", "Endfield.exe");
                    if (mode == 1 || mode == 2 || mode == 4) { File.WriteAllBytes(manifest, original); Directory.CreateDirectory(Path.GetDirectoryName(official)); File.WriteAllText(official, "original game"); }
                    var service = new InstallFiles(home); var state = service.Prepare(steam, manifest, exe, metadata, 76561197960265728UL, mode == 4);
                    Check(File.Exists(service.StatePath), "transaction state saved before mutation mode=" + mode);
                    if (mode == 2)
                    {
                        service.Commit(state); service.Rollback(state);
                        Check(File.ReadAllBytes(manifest).SequenceEqual(original), "rollback restores exact original bytes"); continue;
                    }
                    if (mode != 3) service.Commit(state);
                    if (mode == 1)
                    {
                        Throws(() => service.Prepare(steam, manifest, exe, metadata, 1, false), "repeat enable cannot overwrite original backup");
                        File.WriteAllText(Path.Combine(state.BridgeDirectory, "user-note.txt"), "keep");
                    }
                    service.Restore(state); service.FinishRestore();
                    Check(!File.Exists(service.StatePath), "restore clears state mode=" + mode);
                    Check(state.WasInstalled ? File.ReadAllBytes(manifest).SequenceEqual(original) : !File.Exists(manifest), "restore previous Steam install state mode=" + mode);
                    if (mode == 1 || mode == 4) Check(File.ReadAllText(official) == "original game", "international game files preserved mode=" + mode);
                    if (mode == 1) Check(File.ReadAllText(Path.Combine(state.BridgeDirectory, "user-note.txt")) == "keep", "uninstall preserves unrelated files");
                }
                {
                    string steam = Path.Combine(root, "copy-failure"), home = Path.Combine(root, "copy-failure-data");
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf");
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)); File.WriteAllText(manifest, "original manifest");
                    var service = new InstallFiles(home);
                    Throws(() => service.Prepare(steam, manifest, exe + ".missing", metadata, 1, false), "failed bridge copy rolls back");
                    Check(File.ReadAllText(manifest) == "original manifest" && !File.Exists(service.StatePath), "failed install leaves original manifest and no active state");
                }
                {
                    string steam = Path.Combine(root, "validation"), home = Path.Combine(root, "validation-data");
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_4732690.acf");
                    var service = new InstallFiles(home); var state = service.Prepare(steam, manifest, exe, metadata, 1, false); service.Commit(state);
                    File.WriteAllText(manifest, "\"AppState\" { \"installdir\" \"External game\" }");
                    Throws(() => service.Restore(state), "external manifest changes are preserved for review");
                    File.Copy(state.ManagedManifestBackup, manifest, true); service.Restore(state); service.FinishRestore();
                }
                Console.WriteLine("ALL " + count + " CHECKS PASSED"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally
            {
                string full = Path.GetFullPath(root);
                if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("EndfieldBridge-tests-")) Directory.Delete(full, true);
            }
        }
        private static void ChannelTests(string root)
        {
            Check(GameChannel.Get(App.Json.Deserialize<RuntimeConfig>("{\"Launcher\":\"legacy\"}").ChannelId) == GameChannel.Official, "legacy config defaults to official server");
            var games = new Dictionary<string, string>(); var launchers = new Dictionary<string, string>();
            foreach (var channel in new[] { GameChannel.Official, GameChannel.Bilibili })
            {
                string folder = Path.Combine(root, channel.Id), launcher = Path.Combine(folder, "Launcher.exe"), gameRoot = Path.Combine(folder, "games", "Arknights Endfield");
                Directory.CreateDirectory(gameRoot); File.WriteAllText(launcher, "MZ public-build-marker/" + channel.LauncherCode);
                byte[] pe = new byte[256]; pe[0] = 0x4d; pe[1] = 0x5a; pe[0x3c] = 0x80; pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x84] = 0x64; pe[0x85] = 0x86;
                string game = Path.Combine(gameRoot, "Endfield.exe"); File.WriteAllBytes(game, pe);
                File.WriteAllText(Path.Combine(gameRoot, "UnityPlayer.dll"), "fixture");
                Directory.CreateDirectory(Path.Combine(gameRoot, "Endfield_Data")); File.WriteAllText(Path.Combine(gameRoot, "Endfield_Data", "app.info"), "Hypergryph\nEndfield\n");
                Directory.CreateDirectory(Path.Combine(gameRoot, "U8Data", "config")); File.WriteAllText(Path.Combine(gameRoot, "U8Data", "config", "config.gryph"), "fixture");
                File.WriteAllText(Path.Combine(gameRoot, "u8_channel.dll"), "MZ/" + channel.SdkMarker);
                games[channel.Id] = game; launchers[channel.Id] = launcher;
                bool ready; string status = Detection.ClientStatus(launcher, game, channel.Id, out ready);
                Check(ready, channel.Id + " launcher and game match without official-only SDK files");
                string wrong = channel == GameChannel.Official ? "bilibili" : "official";
                Detection.ClientStatus(launcher, game, wrong, out ready); Check(!ready, "reject wrong server selection for " + channel.Id);
                Detection.ClientStatus(launcher, null, channel.Id, out ready); Check(!ready, "launcher-only install cannot enable " + channel.Id);
                string prefix = new Uri(Path.Combine(folder, "1.6.0", "res", "web", "index.html")).AbsoluteUri;
                string fragment = "#?app_code=6LL0KJuqHBVz33WK&region=cn&is_game=true&channel=" + channel.Number + "&sub_channel=" + channel.Number + "&launcher_channel_name=" + channel.Id;
                Check(channel.MatchesPage(prefix + fragment, launcher), "accept exact local launcher page " + channel.Id);
                Check(!GameChannel.Get(wrong).MatchesPage(prefix + fragment, launcher), "reject other channel page " + channel.Id);
                Check(!channel.MatchesPage(prefix + fragment.Replace("app_code=6LL0KJuqHBVz33WK", "app_code=6LL0KJuqHBVz33WKsuffix"), launcher), "reject partial game identity " + channel.Id);
                Check(!channel.MatchesPage("https://example.invalid/res/web/index.html" + fragment, launcher), "reject remote launcher page " + channel.Id);
                Check(!channel.MatchesPage(prefix + fragment + "&channel=" + channel.Number, launcher), "reject duplicate channel parameters " + channel.Id);
                string foreign = new Uri(Path.Combine(root, "other-launcher", "res", "web", "index.html")).AbsoluteUri;
                Check(!channel.MatchesPage(foreign + fragment, launcher), "reject another launcher directory " + channel.Id);
            }
            bool matched;
            string arkRoot = Path.Combine(root, "arknights"), arkInfo = Path.Combine(arkRoot, "Arknights_Data", "app.info");
            Directory.CreateDirectory(Path.GetDirectoryName(arkInfo)); File.WriteAllText(arkInfo, "HyperGryph\nArknights\n");
            string arkGame = Path.Combine(arkRoot, "Arknights.exe"); File.WriteAllText(arkGame, "fixture"); File.WriteAllText(Path.Combine(arkRoot, "UnityPlayer.dll"), "fixture");
            Check(Detection.ValidArknights(arkGame), "detect Arknights native client");
            Check(!Detection.ValidArknights(games["official"]), "Endfield is not an Arknights client");
            Check(Detection.ArknightsReady(launchers["official"], arkGame), "Arknights uses official launcher");
            Check(!Detection.ArknightsReady(launchers["bilibili"], arkGame), "reject Bilibili launcher for official Arknights");
            string arkPage = new Uri(Path.Combine(Path.GetDirectoryName(launchers["official"]), "1.6.0", "res", "web", "index.html")).AbsoluteUri +
                "#?app_code=ark-fixture&theme=arknights&region=cn&is_game=true&channel=1&sub_channel=1&launcher_channel_name=official";
            Check(GameChannel.Official.MatchesPage(arkPage, launchers["official"], "arknights"), "match local Arknights page");
            Check(!GameChannel.Official.MatchesPage(arkPage, launchers["official"]), "Arknights page cannot start Endfield");
            Check(!GameChannel.Official.MatchesPage(arkPage.Replace("theme=arknights", "theme=popucom"), launchers["official"], "arknights"), "reject other launcher game themes");
            Check(!GameChannel.Official.MatchesPage(arkPage.Replace("ark-fixture", "6LL0KJuqHBVz33WK"), launchers["official"], "arknights"), "reject Endfield identity on Arknights theme");
            Detection.ClientStatus(launchers["official"], games["bilibili"], "official", out matched); Check(!matched, "reject official launcher paired with bilibili game");
            Detection.ClientStatus(launchers["bilibili"], games["official"], "bilibili", out matched); Check(!matched, "reject bilibili launcher paired with official game");
        }
        private static void DetectionAutoFillTests(string root)
        {
            foreach (var channel in GameChannel.All)
            {
                string folder = Path.Combine(root, "auto-fill 中文 空格 " + channel.Id);
                Directory.CreateDirectory(folder);
                string launcher = Path.Combine(folder, "Launcher.exe");
                File.WriteAllText(launcher, "MZ public-build-marker/" + channel.LauncherCode);
                Check(Detection.DetectGameFromLauncher(launcher) == null, "launcher without game falls back to manual selection " + channel.Id);
                string newer = BuildAutoFillGame(Path.Combine(folder, "games", "Endfield Game"), channel);
                Check(Detection.DetectGameFromLauncher(launcher) == newer, "detect alternate install layout with Unicode and spaces " + channel.Id);
                string legacy = BuildAutoFillGame(Path.Combine(folder, "games", "Arknights Endfield"), channel);
                Check(Detection.DetectGameFromLauncher(launcher) == legacy, "prefer existing legacy layout when both valid " + channel.Id);
                File.Delete(Path.Combine(Path.GetDirectoryName(legacy), "UnityPlayer.dll"));
                Check(Detection.DetectGameFromLauncher(launcher) == newer, "skip incomplete client and try alternate layout " + channel.Id);
                var other = channel.Id == "official" ? GameChannel.Bilibili : GameChannel.Official;
                BuildAutoFillGame(Path.GetDirectoryName(legacy), other);
                Check(Detection.DetectGameFromLauncher(launcher) == newer, "skip wrong server before matching alternate layout " + channel.Id);
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(newer), "u8_channel.dll"), "MZ/" + other.SdkMarker);
                Check(Detection.DetectGameFromLauncher(launcher) == null, "never auto-fill a different server client " + channel.Id);
                BuildAutoFillGame(Path.GetDirectoryName(newer), channel);
                string manual = BuildAutoFillGame(Path.Combine(folder, "manual location"), channel);
                Check(Detection.ResolveGame(manual, newer, true, launcher) == manual, "preserve manual game path " + channel.Id);
                Check(Detection.ResolveGame(null, manual, true, launcher) == manual, "preserve valid saved game path " + channel.Id);
                Check(Detection.ResolveGame(null, manual, false, launcher) == newer, "ignore saved path when switching server " + channel.Id);
                Check(Detection.ResolveGame(" ", null, true, launcher) == newer, "blank input allows automatic detection " + channel.Id);
                Check(Detection.ResolveGame(null, "missing.exe", true, launcher) == newer, "stale saved file falls back to detection " + channel.Id);
                Check(Detection.ResolveGame("selected-but-invalid.exe", null, true, launcher) == "selected-but-invalid.exe", "retain explicit invalid selection for UI validation " + channel.Id);
            }
            Check(Detection.DetectGameFromLauncher(null) == null, "missing launcher remains a manual selection");
        }
        private static string BuildAutoFillGame(string folder, GameChannel channel)
        {
            Directory.CreateDirectory(folder);
            byte[] pe = new byte[256]; pe[0] = 0x4d; pe[1] = 0x5a; pe[0x3c] = 0x80; pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x84] = 0x64; pe[0x85] = 0x86;
            string game = Path.Combine(folder, "Endfield.exe"); File.WriteAllBytes(game, pe);
            File.WriteAllText(Path.Combine(folder, "UnityPlayer.dll"), "fixture");
            Directory.CreateDirectory(Path.Combine(folder, "Endfield_Data")); File.WriteAllText(Path.Combine(folder, "Endfield_Data", "app.info"), "Hypergryph\nEndfield\n");
            Directory.CreateDirectory(Path.Combine(folder, "U8Data", "config")); File.WriteAllText(Path.Combine(folder, "U8Data", "config", "config.gryph"), "fixture");
            File.WriteAllText(Path.Combine(folder, "u8_channel.dll"), "MZ/" + channel.SdkMarker);
            return game;
        }
        private static int SchedulerSmoke(string mediumLauncher)
        {
            string root = Path.Combine(Path.GetTempPath(), "Bridge任务 空格 & " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string name = App.TaskName + "-Test-" + Guid.NewGuid().ToString("N");
            string marker = Path.Combine(root, "ran.txt"), script = Path.Combine(root, "run.vbs");
            File.WriteAllText(script, "Set f = CreateObject(\"Scripting.FileSystemObject\").CreateTextFile(\"" + marker.Replace("\"", "\"\"") + "\", True)\r\nf.Write \"ok\"\r\nf.Close", Encoding.Unicode);
            string xml = InstallService.AssistantTaskXml(Path.Combine(Environment.SystemDirectory, "wscript.exe"), root)
                .Replace("<Arguments>--assist</Arguments>", "<Arguments>" + System.Security.SecurityElement.Escape("//B //NoLogo " + Arguments.Quote(script)) + "</Arguments>");
            try
            {
                ScheduledTasks.Register(name, xml);
                ScheduledTasks.Register(name, xml);
                Check(true, "native task create/update accepts Unicode, spaces and XML-sensitive paths");
                if (mediumLauncher == null) ScheduledTasks.Run(name);
                else {
                    using(var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(mediumLauncher,
                        "--start-medium " + Arguments.Quote(System.Reflection.Assembly.GetExecutingAssembly().Location) + " --run-task " + Arguments.Quote(name)) { UseShellExecute = false, CreateNoWindow = true })) process.WaitForExit();
                }
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while(!File.Exists(marker) && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(100);
                Check(File.Exists(marker), "native task Run executes the registered helper" + (mediumLauncher == null ? "" : " from a Medium process"));
                ScheduledTasks.Delete(name); ScheduledTasks.Delete(name);
                Check(true, "native task delete treats only missing task as already deleted");
                bool specific = false;
                try { ScheduledTasks.Register(name, "<invalid/>"); }
                catch(InvalidOperationException error) { specific = error.InnerException is System.Runtime.InteropServices.COMException && error.Message.Contains("0x800"); Console.WriteLine(error.Message); }
                Check(specific, "task API failure keeps exact system HRESULT in displayed message");
                return 0;
            }
            finally { ScheduledTasks.Delete(name); Directory.Delete(root, true); }
        }
        private static void WindowsSettingsTests(string root)
        {
            Check(App.Json.Deserialize<Installation>("{\"Version\":\"0.2.3\"}").SteamCompatibility == null, "old installation record remains readable");
            Check(!App.Json.Deserialize<BridgeRuntime.Request>("{\"Pid\":42}").Delegated, "old requests retain direct elevated launch");
            var request = new BridgeRuntime.Request { Delegated = true, GameId = "endfield", Pid = 42, StartTicks = 1234, Id = "session", SteamEnvironment = new Dictionary<string,string> { {"SteamAppId", "4732690"} } };
            var restored = App.Json.Deserialize<BridgeRuntime.Request>(App.Json.Serialize(request));
            Check(restored.Delegated && restored.Pid == 42 && restored.StartTicks == 1234 && restored.SteamEnvironment["SteamAppId"] == "4732690", "delegated handoff retains session owner and Steam environment");
            Check(SteamWindowsSettings.MayRestore("RUNASADMIN", "RUNASADMIN", null), "restore owned legacy administrator setting");
            Check(!SteamWindowsSettings.MayRestore("RUNASADMIN HIGHDPIAWARE", "RUNASADMIN", null), "preserve later manual compatibility edits");
            string fixtureExe = Path.Combine(root, "legacy Steam", "steam.exe");
            const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
            var changes = new List<SteamCompatibilityChange> { new SteamCompatibilityChange { Executable = fixtureExe, Original = "~ WIN7RTM HIGHDPIAWARE", Applied = "RUNASADMIN", OriginalKind = 1 } };
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(layers))
            {
                try
                {
                    key.SetValue(fixtureExe, "RUNASADMIN");
                    SteamWindowsSettings.RestoreCompatibility(changes, delegate { });
                    Check((string)key.GetValue(fixtureExe) == "~ WIN7RTM HIGHDPIAWARE", "restore exact original legacy compatibility");
                    changes[0].Original = null;
                    key.SetValue(fixtureExe, "RUNASADMIN");
                    SteamWindowsSettings.RestoreCompatibility(changes, delegate { });
                    Check(key.GetValue(fixtureExe) == null, "remove legacy value when originally absent");
                    key.SetValue(fixtureExe, "RUNASADMIN HIGHDPIAWARE");
                    SteamWindowsSettings.RestoreCompatibility(changes, delegate { });
                    Check((string)key.GetValue(fixtureExe) == "RUNASADMIN HIGHDPIAWARE", "keep user's later registry edits");
                }
                finally { key.DeleteValue(fixtureExe, false); }
            }
        }
    }
}