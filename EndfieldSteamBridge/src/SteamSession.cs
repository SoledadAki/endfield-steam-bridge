using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EndfieldSteamBridge
{
    // Retain the existing client's token before shutdown. Restarting from our
    // elevated manager must not silently change a normal Steam into admin Steam.
    internal sealed class SteamSession : IDisposable
    {
        private IntPtr token;
        private readonly string root;
        internal bool WasRunning { get { return token != IntPtr.Zero; } }
        private SteamSession(string steamRoot) { root = steamRoot; }
        internal static SteamSession Capture(string steamRoot)
        {
            var session = new SteamSession(steamRoot);
            foreach (var process in Process.GetProcessesByName("steam"))
                using (process)
                {
                    if (!Detection.Same(process.MainModule.FileName, Path.Combine(steamRoot, "steam.exe"))) continue;
                    IntPtr original;
                    Check(OpenProcessToken(process.Handle, 0x000A, out original));
                    try { Check(DuplicateTokenEx(original, 0xF01FF, IntPtr.Zero, 2, 1, out session.token)); }
                    finally { CloseHandle(original); }
                    return session;
                }
            return session;
        }
        internal void Start()
        {
            if (!WasRunning) return;
            IntPtr environment;
            Check(CreateEnvironmentBlock(out environment, token, false));
            try
            {
                var startup = new Startup { cb = Marshal.SizeOf(typeof(Startup)), desktop = @"winsta0\default" };
                ProcessInfo process;
                Check(CreateProcessWithTokenW(token, 0, Path.Combine(root, "steam.exe"), new StringBuilder(Arguments.Quote(Path.Combine(root, "steam.exe"))),
                    0x400, environment, root, ref startup, out process));
                CloseHandle(process.thread); CloseHandle(process.process);
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
        private static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保留 Steam 原有的运行权限。"); }
        public void Dispose() { if (token != IntPtr.Zero) { CloseHandle(token); token = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Startup
        {
            public int cb; public string reserved, desktop, title;
            public int x, y, cx, cy, xchars, ychars, fill, flags; public short show, reserved2;
            public IntPtr reservedPtr, stdin, stdout, stderr;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr original, uint access, IntPtr attributes, int impersonation, int type, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(IntPtr token, uint logon, string application, StringBuilder command, uint flags, IntPtr environment, string cwd, ref Startup startup, out ProcessInfo process);
        [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
