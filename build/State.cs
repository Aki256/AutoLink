// AutoLink - 状態ファイル (AutoLink.state) の読み書き
// PerSec のパス / Wi-Fi プロファイル名 / Wi-Fi アダプタ名 / 切断記録を 1 ファイルにまとめて保存する。
// 書き込みは一時ファイル→置き換えで行い、停電等で途中で止まってもファイルが壊れない。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AutoLink
{
    internal static class State
    {
        static readonly object Sync = new object();
        static string path;
        static readonly Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static List<string> events = new List<string>();

        public static void Init(string dir)
        {
            lock (Sync)
            {
                path = Path.Combine(dir, "AutoLink.state");
                if (File.Exists(path))
                {
                    LoadFile(path, false);
                }
                else
                {
                    Migrate(dir);
                }
            }
        }

        public static string Get(string key)
        {
            lock (Sync)
            {
                string v;
                return kv.TryGetValue(key, out v) ? v : "";
            }
        }

        public static void Set(string key, string value)
        {
            lock (Sync)
            {
                if (value == null) value = "";
                string cur;
                if (kv.TryGetValue(key, out cur) && cur == value) return;
                kv[key] = value;
                Save();
            }
        }

        public static List<string> GetEvents()
        {
            lock (Sync) { return new List<string>(events); }
        }

        // 切断記録 (日付・サマリー済み日付・イベント行) をまとめて保存
        public static void SaveOutage(string date, string summarized, List<string> eventLines)
        {
            lock (Sync)
            {
                kv["OutageDate"] = date ?? "";
                kv["OutageSummarized"] = summarized ?? "";
                events = new List<string>(eventLines);
                Save();
            }
        }

        // ------------------------------------------------------------ 内部処理

        static void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# AutoLink 状態ファイル (自動生成・編集不要)。削除すると初期状態に戻ります。");
                foreach (KeyValuePair<string, string> p in kv)
                {
                    sb.AppendLine(p.Key + "=" + p.Value.Replace("\r", " ").Replace("\n", " "));
                }
                foreach (string e in events) sb.AppendLine(e);

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch { }
        }

        static void LoadFile(string file, bool legacyOutage)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("E|", StringComparison.Ordinal)) { events.Add(line); continue; }
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (legacyOutage)
                    {
                        // 旧 outage_state.txt のキー名を新しい名前に読み替える
                        if (k == "DATE") k = "OutageDate";
                        else if (k == "SUMMARIZED") k = "OutageSummarized";
                        else continue;
                    }
                    kv[k] = v;
                }
            }
            catch { }
        }

        static string ReadOld(string file)
        {
            try { return File.ReadAllText(file, Encoding.UTF8).Trim(); }
            catch { return ""; }
        }

        // 旧バージョンの個別ファイルを取り込み、取り込んだものは削除する
        static void Migrate(string dir)
        {
            string[] names = new string[] { "persec_path.txt", "wifi_profile.txt", "wifi_adapter.txt", "outage_state.txt" };
            bool any = false;
            foreach (string n in names) if (File.Exists(Path.Combine(dir, n))) any = true;
            if (!any) return;

            string v;
            v = ReadOld(Path.Combine(dir, "persec_path.txt")); if (v.Length > 0) kv["PerSecPath"] = v;
            v = ReadOld(Path.Combine(dir, "wifi_profile.txt")); if (v.Length > 0) kv["WifiProfile"] = v;
            v = ReadOld(Path.Combine(dir, "wifi_adapter.txt")); if (v.Length > 0) kv["WifiAdapter"] = v;
            string outage = Path.Combine(dir, "outage_state.txt");
            if (File.Exists(outage)) LoadFile(outage, true);

            Save();
            if (!File.Exists(path)) return;  // 保存に失敗したら旧ファイルは消さない
            foreach (string n in names)
            {
                try { File.Delete(Path.Combine(dir, n)); } catch { }
            }
        }
    }
}
