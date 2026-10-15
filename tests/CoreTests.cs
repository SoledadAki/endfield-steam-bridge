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
        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "EndfieldBridge-tests-空 格-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
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
    }
}
