using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EndfieldSteamBridge
{
    // Steam text KeyValues and binary appinfo caches. No account tokens are read.
    internal static class KeyValues
    {
        public static Dictionary<string, object> Parse(string text)
        {
            int position = 0;
            return ReadObject(text, ref position, false);
        }
        private static Dictionary<string, object> ReadObject(string text, ref int pos, bool nested)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                string key = Token(text, ref pos);
                if (key == null) { if (nested) throw new InvalidDataException("KeyValues 对象不完整。"); return result; }
                if (key == "}") { if (!nested) throw new InvalidDataException("KeyValues 括号不匹配。"); return result; }
                string value = Token(text, ref pos);
                if (value == null) throw new InvalidDataException("KeyValues 缺少值。");
                result[key] = value == "{" ? (object)ReadObject(text, ref pos, true) : value;
            }
        }
        private static string Token(string text, ref int pos)
        {
            while (pos < text.Length)
            {
                if (Char.IsWhiteSpace(text[pos]) || text[pos] == '\ufeff') { pos++; continue; }
                if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
                { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
                break;
            }
            if (pos >= text.Length) return null;
            char c = text[pos++];
            if (c == '{' || c == '}') return c.ToString();
            var s = new StringBuilder();
            if (c != '"')
            { s.Append(c); while (pos < text.Length && !Char.IsWhiteSpace(text[pos]) && text[pos] != '{' && text[pos] != '}') s.Append(text[pos++]); return s.ToString(); }
            while (pos < text.Length)
            {
                c = text[pos++];
                if (c == '"') return s.ToString();
                if (c == '\\' && pos < text.Length)
                {
                    char next = text[pos++];
                    if (next == '\\' || next == '"') s.Append(next);
                    else if (next == 'n') s.Append('\n');
                    else if (next == 't') s.Append('\t');
                    else { s.Append('\\'); s.Append(next); }
                }
                else s.Append(c);
            }
            throw new InvalidDataException("KeyValues 字符串不完整。");
        }
        public static Dictionary<string, object> Child(Dictionary<string, object> o, string key)
        { object value; return o != null && o.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        public static string String(Dictionary<string, object> o, string key)
        { object value; return o != null && o.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        public static string Quote(string value) { return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
        public static string Render(Dictionary<string, object> obj, int level = 0)
        {
            var b = new StringBuilder(); string indent = new string('\t', level);
            foreach (var pair in obj)
            {
                b.Append(indent).Append(Quote(pair.Key));
                var child = pair.Value as Dictionary<string, object>;
                if (child != null) b.AppendLine().Append(indent).AppendLine("{").Append(Render(child, level + 1)).Append(indent).AppendLine("}");
                else b.Append('\t').AppendLine(Quote(Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
            }
            return b.ToString();
        }
        public static Metadata ReadAppInfo(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new BinaryReader(stream, Encoding.UTF8))
            {
                uint magic = r.ReadUInt32(); r.ReadUInt32();
                bool tableKeys = magic == 0x07564429;
                if (!tableKeys && magic != 0x07564428 && magic != 0x07564427) throw new InvalidDataException("暂不支持此 Steam 元数据版本，请更新工具。");
                string[] keys = null;
                long limit = stream.Length;
                if (tableKeys)
                {
                    long table = r.ReadInt64();
                    if (table < 16 || table >= stream.Length) throw new InvalidDataException("Steam 元数据字符串表无效。");
                    limit = table; stream.Position = table;
                    int count = r.ReadInt32();
                    if (count < 0 || count > 2000000) throw new InvalidDataException("Steam 字符串表过大。");
                    keys = new string[count]; for (int i = 0; i < count; i++) keys[i] = CString(r);
                    stream.Position = 16;
                }
                while (stream.Position + 8 <= limit)
                {
                    uint id = r.ReadUInt32(); if (id == 0) break;
                    uint size = r.ReadUInt32(); long next = stream.Position + size;
                    if (size < 40 || next > limit) throw new InvalidDataException("Steam 元数据记录无效。");
                    if (id == App.AppId)
                    {
                        stream.Position += magic == 0x07564427 ? 40 : 60;
                        var root = BinaryObject(r, keys, next, 0);
                        var info = Child(root, "appinfo");
                        var config = Child(info, "config"); var depots = Child(info, "depots");
                        var m = new Metadata { Build = String(Child(Child(depots, "branches"), "public"), "buildid"), Executable = String(Child(Child(config, "launch"), "0"), "executable") };
                        uint build;
                        if (!UInt32.TryParse(m.Build, out build) || build == 0 || System.String.IsNullOrEmpty(m.Executable)) throw new InvalidDataException("终末地元数据缺少启动或公共版本信息。请在 Steam 打开游戏库页面后重试。");
                        foreach (var depot in depots)
                        {
                            uint depotId; if (!UInt32.TryParse(depot.Key, out depotId)) continue;
                            string gid = String(Child(Child(depot.Value as Dictionary<string, object>, "manifests"), "public"), "gid");
                            ulong parsed; if (UInt64.TryParse(gid, out parsed) && parsed > 0) m.Depots[depot.Key] = gid;
                        }
                        if (m.Depots.Count == 0) throw new InvalidDataException("没有可用的公共 depot 元数据。");
                        return m;
                    }
                    stream.Position = next;
                }
            }
            throw new InvalidDataException("Steam 缓存中没有终末地。请先将游戏加入库，并打开它的库页面，再重试检测。");
        }
        private static string CString(BinaryReader r)
        {
            var bytes = new List<byte>(); byte b;
            while ((b = r.ReadByte()) != 0) { if (bytes.Count > 1048576) throw new InvalidDataException("Steam 字符串过长。"); bytes.Add(b); }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        private static Dictionary<string, object> BinaryObject(BinaryReader r, string[] keys, long end, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Steam 元数据嵌套过深。");
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (r.BaseStream.Position < end)
            {
                byte type = r.ReadByte(); if (type == 8) return result;
                string key;
                if (keys != null) { uint index = r.ReadUInt32(); if (index >= keys.Length) throw new InvalidDataException("Steam 字符串索引无效。"); key = keys[index]; }
                else key = CString(r);
                object value;
                switch (type)
                {
                    case 0: value = BinaryObject(r, keys, end, depth + 1); break;
                    case 1: value = CString(r); break;
                    case 2: case 4: case 6: value = r.ReadUInt32(); break;
                    case 3: value = r.ReadSingle(); break;
                    case 7: case 10: value = r.ReadUInt64(); break;
                    default: throw new InvalidDataException("不支持的 Steam 元数据类型：" + type);
                }
                result[key] = value;
            }
            throw new InvalidDataException("Steam 元数据对象不完整。");
        }
    }
}
