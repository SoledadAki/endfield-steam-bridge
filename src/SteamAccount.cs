using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EndfieldSteamBridge
{
    internal static class SteamAccount
    {
        // ISteamClient021/020 and ISteamApps008 slot order from Valve's public
        // Steamworks headers. The DLL belongs to the user's Steam installation.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Factory([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr code);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int CreatePipe(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool ReleasePipe(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int ConnectUser(IntPtr self, int pipe);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void ReleaseUser(IntPtr self, int pipe, int user);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr GetInterface(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool LoggedOn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Subscribed(IntPtr self, uint app);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] // CSteamID has a C++ return buffer in the Windows x64 ABI.
        private delegate IntPtr GetId(IntPtr self, out ulong value);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate IntPtr GetName(IntPtr self);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static T Method<T>(IntPtr instance, int slot) where T : class
        { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), typeof(T)) as T; }
        private static bool IsSteamRunning()
        { bool running = false; foreach (var p in Process.GetProcessesByName("steam")) using (p) running = true; return running; }
        public static Ownership Probe(string steam)
        {
            var result = new Ownership(); IntPtr client = IntPtr.Zero; int pipe = 0, user = 0;
            try
            {
                if (!IsSteamRunning()) throw new InvalidOperationException("请先打开 Steam 并登录。");
                IntPtr module = LoadLibraryEx(Path.Combine(steam, "steamclient64.dll"), IntPtr.Zero, 8);
                if (module == IntPtr.Zero) throw new InvalidOperationException("无法加载本机 Steam 客户端接口。");
                IntPtr entry = GetProcAddress(module, "CreateInterface");
                if (entry == IntPtr.Zero) throw new InvalidOperationException("Steam 客户端接口不可用。");
                var factory = (Factory)Marshal.GetDelegateForFunctionPointer(entry, typeof(Factory));
                foreach (string version in new[] { "SteamClient021", "SteamClient020" }) { client = factory(version, IntPtr.Zero); if (client != IntPtr.Zero) break; }
                if (client == IntPtr.Zero) throw new InvalidOperationException("当前 Steam 接口版本尚未适配。");
                pipe = Method<CreatePipe>(client, 0)(client); user = Method<ConnectUser>(client, 2)(client, pipe);
                if (pipe == 0 || user == 0) throw new InvalidOperationException("没有已登录的 Steam 用户。");
                var getUser = Method<GetInterface>(client, 5);
                IntPtr steamUser = IntPtr.Zero;
                foreach (string version in new[] { "SteamUser023", "SteamUser022", "SteamUser021" }) { steamUser = getUser(client, user, pipe, version); if (steamUser != IntPtr.Zero) break; }
                if (steamUser == IntPtr.Zero || !Method<LoggedOn>(steamUser, 1)(steamUser)) throw new InvalidOperationException("Steam 当前没有在线登录，请登录后重新检测。");
                ulong steamId; Method<GetId>(steamUser, 2)(steamUser, out steamId); result.SteamId = steamId;
                using (var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    uint account = active == null ? 0 : unchecked((uint)Convert.ToInt32(active.GetValue("ActiveUser", 0)));
                    if (account == 0 || account != (uint)(result.SteamId & 0xffffffffUL)) throw new InvalidOperationException("Steam 正在切换账号，请稍后重新检测。");
                }
                IntPtr apps = Method<GetInterface>(client, 15)(client, user, pipe, "STEAMAPPS_INTERFACE_VERSION008");
                if (apps == IntPtr.Zero) throw new InvalidOperationException("无法读取 Steam 游戏库状态。");
                result.Owned = Method<Subscribed>(apps, 6)(apps, App.AppId); result.Known = true;
                result.Installed = Method<Subscribed>(apps, 19)(apps, App.AppId);
                IntPtr friends = Method<GetInterface>(client, 8)(client, user, pipe, "SteamFriends017");
                if (friends != IntPtr.Zero)
                {
                    IntPtr name = Method<GetName>(friends, 0)(friends); int length = 0;
                    while (length < 4096 && Marshal.ReadByte(name, length) != 0) length++;
                    byte[] bytes = new byte[length]; Marshal.Copy(name, bytes, 0, length); result.Name = System.Text.Encoding.UTF8.GetString(bytes);
                }
            }
            catch (Exception error) { result.Known = false; result.Error = error.Message; }
            finally
            {
                if (client != IntPtr.Zero && user != 0) Method<ReleaseUser>(client, 4)(client, pipe, user);
                if (client != IntPtr.Zero && pipe != 0) Method<ReleasePipe>(client, 1)(client, pipe);
            }
            return result;
        }
        public static Ownership ProbeIsolated(string steam)
        {
            Directory.CreateDirectory(App.Home); string file = Path.Combine(App.Home, "account-probe-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                using (var process = Process.Start(new ProcessStartInfo { FileName = System.Reflection.Assembly.GetExecutingAssembly().Location, Arguments = "--probe " + Arguments.Quote(file), UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!process.WaitForExit(15000)) { process.Kill(); return new Ownership { Error = "Steam 账号检测超时，请确认客户端已登录。" }; }
                    if (process.ExitCode != 0 || !File.Exists(file)) return new Ownership { Error = "当前 Steam 接口检测失败，请更新工具后重试。" };
                }
                return App.Load<Ownership>(file);
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
    }
}

