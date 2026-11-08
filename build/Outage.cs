// AutoLink - 切断時間の記録と、日次(0:00)/アプリ終了時のサマリー出力
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AutoLink
{
    internal sealed class OutageEvent
    {
        public DateTime Start;
        public DateTime End;
        public string Cause = "";
        public int Attempts;
        public bool Fixed;
        public string Note = "";
    }

    internal static class Outage
    {
        static readonly object Sync = new object();
        static string logDir;
        public static string LogDir { get { return logDir; } }
        static readonly Dictionary<DateTime, List<OutageEvent>> days = new Dictionary<DateTime, List<OutageEvent>>();
        static DateTime curDay;
        static string lastSummarized = "";   // 終了時にサマリー済みの日付 (起動時の補完で二重出力しないため)
        static bool exitDone;

        // 進行中の切断
        static bool active;
        static DateTime oStart;
        static string oCause = "";
        static string oLastCls = "";
        static int oAttempts;
        static bool oFixed;
        static string oNote = "";

        delegate bool CtrlHandler(uint ctrlType);
        static CtrlHandler ctrlHandler;
        [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);

        // ------------------------------------------------------------ 公開API

        public static void Init(string dir, string logDirSetting)
        {
            lock (Sync)
            {
                string ld = string.IsNullOrWhiteSpace(logDirSetting) ? "logs" : logDirSetting.Trim().Trim('"');
                if (!Path.IsPathRooted(ld)) ld = Path.Combine(dir, ld);
                logDir = ld;
                DateTime today = DateTime.Today;
                curDay = today;

                DateTime stateDate = LoadState();
                if (stateDate == today)
                {
                    lastSummarized = "";  // 同じ日に再起動: これ以降の記録を改めてサマリー対象にする
                }
                else if (stateDate != DateTime.MinValue && !days.ContainsKey(stateDate) && Key(stateDate) != lastSummarized)
                {
                    days[stateDate] = new List<OutageEvent>();
                }
                FlushOldDays("起動時に補完 (前回は異常終了、または日付をまたいで停止していたため)", true);
                SaveState();
            }
            ctrlHandler = OnCtrl;
            try { SetConsoleCtrlHandler(ctrlHandler, true); } catch { }
            AppDomain.CurrentDomain.ProcessExit += delegate (object s, EventArgs e) { ExitSummary(); };
        }

        // 各チェック結果を渡す。異常の開始/終了をここで検出する。
        public static void NoteCheck(bool ok, string detail)
        {
            lock (Sync)
            {
                RolloverIfNeeded();
                DateTime now = DateTime.Now;
                if (!ok)
                {
                    string cls = Classify(detail);
                    if (!active)
                    {
                        active = true;
                        oStart = now;
                        oAttempts = 0;
                        oFixed = false;
                        oNote = "";
                        oLastCls = cls;
                        oCause = cls + " [" + detail + "]";
                    }
                    else if (cls != oLastCls)
                    {
                        oLastCls = cls;
                        oCause += " → " + now.ToString("HH:mm:ss") + " " + cls + " [" + detail + "]";
                    }
                }
                else if (active)
                {
                    OutageEvent ev = new OutageEvent();
                    ev.Start = oStart; ev.End = now; ev.Cause = oCause;
                    ev.Attempts = oAttempts; ev.Fixed = oFixed; ev.Note = oNote;
                    AddEvent(ev);
                    active = false;
                    SaveState();
                }
            }
        }

        public static void NoteRecoverAttempt()
        {
            lock (Sync) { if (active) oAttempts++; }
        }

        public static void NoteRecoverSuccess()
        {
            lock (Sync) { if (active) oFixed = true; }
        }

        // 待機。0:00 をまたぐときはちょうど 0:00 で目を覚まして日次サマリーを出す。
        public static void Sleep(int sec)
        {
            int ms = sec * 1000;
            double toMid = (DateTime.Today.AddDays(1) - DateTime.Now).TotalMilliseconds + 300;
            if (toMid > 0 && toMid < ms)
            {
                Thread.Sleep((int)toMid);
                Tick();
                Thread.Sleep(ms - (int)toMid);
            }
            else
            {
                Thread.Sleep(ms);
            }
            Tick();
        }

        public static void Tick()
        {
            lock (Sync) { RolloverIfNeeded(); }
        }

        // ------------------------------------------------------------ 日付またぎ・終了

        static void RolloverIfNeeded()
        {
            DateTime today = DateTime.Today;
            if (today <= curDay) return;

            if (active && oStart < today)
            {
                // 進行中の切断を 0:00 で区切って前日分に計上し、今日分として継続させる
                OutageEvent ev = new OutageEvent();
                ev.Start = oStart; ev.End = today; ev.Cause = oCause;
                ev.Attempts = oAttempts; ev.Fixed = oFixed;
                ev.Note = (oNote + " 0:00で区切り(継続中)").Trim();
                AddEvent(ev);
                oStart = today;
                oNote = "前日から継続";
                oAttempts = 0;
                oFixed = false;
            }
            if (!days.ContainsKey(curDay)) days[curDay] = new List<OutageEvent>();
            FlushOldDays("日次 (0:00)", false);
            curDay = today;
            SaveState();
        }

        static bool OnCtrl(uint ctrlType)
        {
            // 0=Ctrl+C 1=Ctrl+Break 2=コンソールを閉じる 5=ログオフ 6=シャットダウン
            if (ctrlType <= 6) ExitSummary();
            return false;
        }

        static void ExitSummary()
        {
            lock (Sync)
            {
                if (exitDone || logDir == null) return;
                exitDone = true;
                try
                {
                    RolloverIfNeeded();
                    DateTime now = DateTime.Now;
                    if (active)
                    {
                        OutageEvent ev = new OutageEvent();
                        ev.Start = oStart; ev.End = now; ev.Cause = oCause;
                        ev.Attempts = oAttempts; ev.Fixed = oFixed;
                        ev.Note = (oNote + " 終了時点で継続中").Trim();
                        AddEvent(ev);
                        active = false;
                    }
                    if (!days.ContainsKey(curDay)) days[curDay] = new List<OutageEvent>();
                    WriteSummary(curDay, days[curDay], "アプリ終了時");
                    lastSummarized = Key(curDay);
                    SaveState();
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ 内部処理

        static string Classify(string detail)
        {
            if (detail == null) return "疎通失敗";
            if (detail.IndexOf("アダプタなし", StringComparison.Ordinal) >= 0) return "Wi-Fiアダプタ消失";
            if (detail.IndexOf("未接続", StringComparison.Ordinal) >= 0) return "Wi-Fi切断";
            if (detail.IndexOf("インターネットなし", StringComparison.Ordinal) >= 0) return "インターネット到達不可";
            return "疎通失敗";
        }

        static string Key(DateTime d)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 日付をまたぐ区間は 0:00 で分割して、それぞれの日に計上する
        static void AddEvent(OutageEvent ev)
        {
            DateTime s = ev.Start;
            while (s.Date < ev.End.Date)
            {
                DateTime mid = s.Date.AddDays(1);
                Put(Clone(ev, s, mid, (ev.Note + " 0:00で区切り").Trim()));
                s = mid;
            }
            Put(Clone(ev, s, ev.End, ev.Note));
        }

        static OutageEvent Clone(OutageEvent e, DateTime s, DateTime en, string note)
        {
            OutageEvent c = new OutageEvent();
            c.Start = s; c.End = en; c.Cause = e.Cause; c.Attempts = e.Attempts; c.Fixed = e.Fixed; c.Note = note;
            return c;
        }

        static void Put(OutageEvent ev)
        {
            DateTime k = ev.Start.Date;
            List<OutageEvent> list;
            if (!days.TryGetValue(k, out list)) { list = new List<OutageEvent>(); days[k] = list; }
            list.Add(ev);
        }

        static void FlushOldDays(string trigger, bool startup)
        {
            DateTime today = DateTime.Today;
            List<DateTime> keys = new List<DateTime>(days.Keys);
            keys.Sort();
            foreach (DateTime k in keys)
            {
                if (k >= today) continue;
                if (startup && Key(k) == lastSummarized) { days.Remove(k); continue; }
                WriteSummary(k, days[k], trigger);
                lastSummarized = Key(k);
                days.Remove(k);
            }
        }

        static string Fmt(TimeSpan t)
        {
            long total = (long)Math.Round(t.TotalSeconds);
            long h = total / 3600, m = (total % 3600) / 60, s = total % 60;
            if (h > 0) return h + "時間" + m.ToString("00") + "分" + s.ToString("00") + "秒";
            if (m > 0) return m + "分" + s.ToString("00") + "秒";
            return s + "秒";
        }

        static void WriteSummary(DateTime day, List<OutageEvent> list, string trigger)
        {
            try
            {
                list.Sort(delegate (OutageEvent a, OutageEvent b) { return a.Start.CompareTo(b.Start); });
                TimeSpan total = TimeSpan.Zero;
                foreach (OutageEvent e in list) total += (e.End - e.Start);

                CultureInfo ja;
                try { ja = new CultureInfo("ja-JP"); } catch { ja = CultureInfo.InvariantCulture; }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("==== " + day.ToString("yyyy-MM-dd (ddd)", ja) + " ネットワーク切断サマリー ====");
                sb.AppendLine("記録日時: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "   契機: " + trigger);
                sb.AppendLine("合計切断時間(概算): " + Fmt(total) + "   切断回数: " + list.Count);
                int i = 1;
                foreach (OutageEvent e in list)
                {
                    string rec;
                    if (e.Fixed) rec = "AutoLinkの復旧処理(" + e.Attempts + "回目)で回復";
                    else if (e.Attempts > 0) rec = "AutoLinkの復旧処理を" + e.Attempts + "回実施後に回復";
                    else rec = "復旧処理なし(自然回復)";
                    sb.AppendLine("  " + i + ". " + e.Start.ToString("HH:mm:ss") + " - " + e.End.ToString("HH:mm:ss")
                        + " (" + Fmt(e.End - e.Start) + ")  原因: " + e.Cause + "  / " + rec
                        + (string.IsNullOrEmpty(e.Note) ? "" : "  ※" + e.Note));
                    i++;
                }
                if (list.Count == 0) sb.AppendLine("  切断なし");
                sb.AppendLine("  (注) 開始=最初に異常を検知した時刻、終了=疎通の回復を確認した時刻。確認間隔の分だけ実際とずれる概算値です。");
                sb.AppendLine();

                string path = Path.Combine(logDir, day.ToString("yyyyMM", CultureInfo.InvariantCulture) + "_AutoLink.log");
                try { Directory.CreateDirectory(logDir); }
                catch (Exception dex)
                {
                    Program.Log("ERROR", "ログフォルダを作成できません (" + logDir + "): " + dex.Message + " → アプリのフォルダに出力します");
                    path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, day.ToString("yyyyMM", CultureInfo.InvariantCulture) + "_AutoLink.log");
                }
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(true));
                Program.Log("INFO", "切断サマリーを書き込みました: " + Key(day) + " 合計 " + Fmt(total) + " / " + list.Count + "回 (" + trigger + ") → " + path);
            }
            catch (Exception ex)
            {
                Program.Log("ERROR", "切断サマリーの書き込みに失敗: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ 状態の保存 (再起動をまたいで日次合計を保つ)

        static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        static void SaveState()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<DateTime, List<OutageEvent>> kv in days)
                {
                    foreach (OutageEvent e in kv.Value)
                    {
                        lines.Add("E|" + e.Start.Ticks + "|" + e.End.Ticks + "|" + e.Attempts + "|" + (e.Fixed ? "1" : "0") + "|" + Clean(e.Note) + "|" + Clean(e.Cause));
                    }
                }
                State.SaveOutage(Key(curDay), lastSummarized, lines);
            }
            catch { }
        }

        // 戻り値: 保存されていた日付 (なければ DateTime.MinValue)
        static DateTime LoadState()
        {
            DateTime stateDate = DateTime.MinValue;
            try
            {
                DateTime d;
                if (DateTime.TryParseExact(State.Get("OutageDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) stateDate = d;
                lastSummarized = State.Get("OutageSummarized");
                foreach (string line in State.GetEvents())
                {
                    string[] p = line.Split(new char[] { '|' }, 7);
                    if (p.Length < 7) continue;
                    OutageEvent e = new OutageEvent();
                    long st, en; int at;
                    if (!long.TryParse(p[1], out st) || !long.TryParse(p[2], out en) || !int.TryParse(p[3], out at)) continue;
                    e.Start = new DateTime(st); e.End = new DateTime(en); e.Attempts = at;
                    e.Fixed = p[4] == "1"; e.Note = p[5]; e.Cause = p[6];
                    Put(e);
                }
            }
            catch { }
            return stateDate;
        }
    }
}
