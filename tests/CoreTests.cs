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
            if (args.Length == 2 && args[0] == "--export-scripts")
            {
                App.Save(args[1], new Dictionary<string, string> { { "official", GameChannel.Official.StartScript(true) }, { "bilibili", GameChannel.Bilibili.StartScript(true) } }); return 0;
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
            foreach (var channel in GameChannel.All)
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
            Detection.ClientStatus(launchers["official"], games["bilibili"], "official", out matched); Check(!matched, "reject official launcher paired with bilibili game");
            Detection.ClientStatus(launchers["bilibili"], games["official"], "bilibili", out matched); Check(!matched, "reject bilibili launcher paired with official game");
        }
    }
}
