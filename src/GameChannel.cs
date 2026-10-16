using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EndfieldSteamBridge
{
    internal sealed class GameChannel
    {
        public static readonly GameChannel Official = new GameChannel("official", "官服", "abYeZZ16BPluCFyT", "1", "u8sdk_hg_pc");
        public static readonly GameChannel Bilibili = new GameChannel("bilibili", "Bilibili 服", "RhAij5MdwvTWpQ8N", "2", "u8sdk_bilibili_pc");
        public static readonly GameChannel[] All = new[] { Official, Bilibili };
        public readonly string Id, Name, LauncherCode, Number, SdkMarker;
        private GameChannel(string id, string name, string launcherCode, string number, string sdkMarker)
        { Id = id; Name = name; LauncherCode = launcherCode; Number = number; SdkMarker = sdkMarker; }
        public static GameChannel Get(string id)
        {
            // Runtime configs written before server selection always used the official server.
            if (String.IsNullOrEmpty(id) || id == Official.Id) return Official;
            if (id == Bilibili.Id) return Bilibili;
            throw new InvalidDataException("不支持的服务器类型：" + id);
        }
        public override string ToString() { return Name; }
        public static bool HasMarker(string path, string marker)
        {
            if (!File.Exists(path)) return false;
            // Inspect metadata strings only; never load launcher/game DLLs for detection.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > 32 * 1024 * 1024) return false;
                byte[] data = new byte[(int)stream.Length]; int position = 0, read;
                while (position < data.Length && (read = stream.Read(data, position, data.Length - position)) > 0) position += read;
                return Encoding.ASCII.GetString(data, 0, position).Contains(marker);
            }
        }
        public static string IdentifyLauncher(string path)
        {
            if (!Detection.ValidLauncher(path)) return null;
            foreach (var channel in All) if (HasMarker(path, channel.LauncherCode)) return channel.Id;
            return null;
        }
        public static string IdentifyGame(string path)
        {
            if (!Detection.ValidGame(path)) return null;
            string sdk = Path.Combine(Path.GetDirectoryName(path), "u8_channel.dll");
            foreach (var channel in All) if (HasMarker(sdk, channel.SdkMarker)) return channel.Id;
            return null;
        }
        public bool MatchesPage(string url, string launcher)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !uri.IsFile || uri.IsUnc || !String.IsNullOrEmpty(uri.Host)) return false;
            string root = Path.GetDirectoryName(Path.GetFullPath(launcher)) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(uri.LocalPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.IndexOf("\\res\\web\\", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in uri.Fragment.TrimStart('#', '?').Split('&'))
            {
                int equals = pair.IndexOf('='); if (equals < 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, equals));
                if (values.ContainsKey(key)) return false;
                values[key] = Uri.UnescapeDataString(pair.Substring(equals + 1));
            }
            return Is(values, "app_code", "6LL0KJuqHBVz33WK") && Is(values, "region", "cn") && Is(values, "is_game", "true") &&
                Is(values, "channel", Number) && Is(values, "sub_channel", Number) && Is(values, "launcher_channel_name", Id);
        }
        private static bool Is(Dictionary<string, string> values, string key, string expected)
        { string value; return values.TryGetValue(key, out value) && value == expected; }
        public string StartScript(bool click)
        {
            return @"(function() {
                const p = new URLSearchParams(location.hash.replace(/^#\??/, ''));
                if (p.get('app_code') !== '6LL0KJuqHBVz33WK' || p.get('region') !== 'cn' || p.get('is_game') !== 'true' ||
                    p.get('channel') !== " + App.Json.Serialize(Number) + @" || p.get('sub_channel') !== " + App.Json.Serialize(Number) + @" ||
                    p.get('launcher_channel_name') !== " + App.Json.Serialize(Id) + @") return 'wrong-page';
                const visible = e => !!(e.offsetWidth && e.offsetHeight && e.getClientRects().length);
                const nodes = Array.from(document.querySelectorAll('.clamp-text-2,button,[role=button],.cursor-pointer')).filter(visible);
                const ready = [];
                for (const node of nodes) {
                    if (node.textContent.replace(/\s/g, '') !== '开始游戏') continue;
                    const target = node.closest('button,[role=button],.cursor-pointer');
                    if (target && !ready.some(e => e.target === target)) ready.push({ node, target });
                }
                if (ready.length !== 1) return 'not-ready: ' + nodes.map(e => e.textContent.trim()).join(' / ').slice(0, 180);
                const e = ready[0].node, target = ready[0].target;
                if (!target || target.closest('.cursor-not-allowed,[disabled],[aria-disabled=true]')) return 'disabled';
                const r = e.getBoundingClientRect(), top = document.elementFromPoint(r.x + r.width/2, r.y + r.height/2);
                if (!top || !target.contains(top)) return 'covered';
                " + (click ? "e.click(); return 'clicked';" : "return 'ready';") + @"
            })()";
        }
    }
}
