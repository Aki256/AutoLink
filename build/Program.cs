// AutoLink - Wi-Fi 監視・自動再接続・PerSec 再起動ツール
// Windows 標準の csc.exe (C# 5 / .NET Framework 4.x) でそのままビルドできるように書いてあります。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: System.Reflection.AssemblyTitle("AutoLink")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace AutoLink
{
    internal sealed class Config
    {
        public int CheckIntervalSec = 30;       // 正常時の確認間隔
        public int RetryIntervalSec = 10;       // 異常を検知してからの再確認間隔
        public int FailThreshold = 3;           // 連続何回失敗したら復旧処理に入るか
        public int ProbeTimeoutSec = 5;         // 1回の疎通確認のタイムアウト
        public int ReconnectWaitSec = 45;       // 再接続後、疎通回復を待つ最大秒数
        public int AdapterResetAfterFails = 2;  // 復旧に連続失敗したらアダプタ無効化→有効化まで行う回数 (管理者権限が必要)
        public bool RestartPerSec = true;       // 復旧時に PerSec を再起動するか
        public string PerSecProcess = "parsecd"; // プロセス名 (.exe は不要)
        public string PerSecPath = "";          // PerSec.exe のフルパス (空なら自動検出)
        public string WifiProfile = "";         // Wi-Fi プロファイル名 (空なら自動検出)
        public string LogDir = "logs";          // 切断サマリーの出力先 (相対パスはアプリのフォルダ基準)

        public static string DefaultText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# AutoLink 設定ファイル (key=value / # はコメント)。変更後は AutoLink.exe を再起動してください。");
            sb.AppendLine("CheckIntervalSec=30");
            sb.AppendLine("RetryIntervalSec=10");
            sb.AppendLine("FailThreshold=3");
            sb.AppendLine("ProbeTimeoutSec=5");
            sb.AppendLine("ReconnectWaitSec=45");
            sb.AppendLine("AdapterResetAfterFails=2");
            sb.AppendLine("RestartPerSec=true");
            sb.AppendLine("# タスクマネージャーで見える PerSec のプロセス名 (拡張子なし)");
            sb.AppendLine("PerSecProcess=parsecd");
            sb.AppendLine("# 空なら「起動中のPerSecから自動取得」→「Program Files等を検索」の順で探します");
            sb.AppendLine("PerSecPath=");
            sb.AppendLine("# 空なら接続中のプロファイルを自動検出します");
            sb.AppendLine("WifiProfile=");
            sb.AppendLine("# 切断サマリー (yyyyMM_AutoLink.log) の出力先フォルダ。相対パスはAutoLink.exeのあるフォルダ基準。既定は logs");
            sb.AppendLine("LogDir=logs");
            return sb.ToString();
        }

        public static Config Load(string path)
        {
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, DefaultText(), new UTF8Encoding(false)); } catch { }
            }
            Config c = new Config();
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); } catch { return c; }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim().ToLowerInvariant();
                string v = line.Substring(i + 1).Trim();
                int n;
                switch (k)
                {
                    case "checkintervalsec": if (int.TryParse(v, out n)) c.CheckIntervalSec = Math.Max(5, n); break;
                    case "retryintervalsec": if (int.TryParse(v, out n)) c.RetryIntervalSec = Math.Max(3, n); break;
                    case "failthreshold": if (int.TryParse(v, out n)) c.FailThreshold = Math.Max(1, n); break;
                    case "probetimeoutsec": if (int.TryParse(v, out n)) c.ProbeTimeoutSec = Math.Max(1, n); break;
                    case "reconnectwaitsec": if (int.TryParse(v, out n)) c.ReconnectWaitSec = Math.Max(10, n); break;
                    case "adapterresetafterfails": if (int.TryParse(v, out n)) c.AdapterResetAfterFails = Math.Max(1, n); break;
                    case "restartpersec": c.RestartPerSec = !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0"); break;
                    case "persecprocess":
                        if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 4);
                        if (v.Length > 0) c.PerSecProcess = v;
                        break;
                    case "persecpath": c.PerSecPath = v.Trim('"'); break;
                    case "wifiprofile": c.WifiProfile = v; break;
                    case "logdir": if (v.Length > 0) c.LogDir = v; break;
                }
            }
            return c;
        }
    }

    internal static class Program
    {
        internal const string Version = "1.00";

        const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
        const string NcsiBody = "Microsoft Connect Test";
        const string Gen204Url = "http://www.google.com/generate_204";

        static Config C;
        static string Dir;
        static string persecPath;   // 検出済みの PerSec.exe パス
        static string lastProfile;  // 最後に確認できた Wi-Fi プロファイル名
        static bool restartPending; // 復旧失敗で PerSec 起動を保留中 (疎通回復後に起動)

        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
        [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);
        [DllImport("kernel32.dll")] static extern uint GetOEMCP();

        internal static void Log(string level, string msg)
        {
            Console.WriteLine("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}", DateTime.Now, level, msg);
        }

        // コンソールをクリックしただけで出力が止まる「クイック編集モード」を無効化 (常駐用)
        static void DisableQuickEdit()
        {
            try
            {
                IntPtr h = GetStdHandle(-10);
                uint m;
                if (GetConsoleMode(h, out m))
                {
                    m &= ~0x40u;  // ENABLE_QUICK_EDIT_MODE
                    m |= 0x80u;   // ENABLE_EXTENDED_FLAGS
                    SetConsoleMode(h, m);
                }
            }
            catch { }
        }

        static int Main(string[] args)
        {
            bool created;
            Mutex mutex = new Mutex(true, "Local\\AutoLink_SingleInstance", out created);
            if (!created)
            {
                Console.WriteLine("AutoLink は既に起動しています。");
                return 1;
            }

            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            try { Console.Title = "AutoLink Ver" + Version + " - Wi-Fi watchdog"; } catch { }
            DisableQuickEdit();

            Dir = AppDomain.CurrentDomain.BaseDirectory;
            State.Init(Dir);
            C = Config.Load(Path.Combine(Dir, "AutoLink.ini"));
            Outage.Init(Dir, C.LogDir);

            Log("INFO", "AutoLink Ver" + Version + " 起動 (確認間隔 " + C.CheckIntervalSec + "秒 / 異常時 " + C.RetryIntervalSec + "秒 x " + C.FailThreshold + "回で復旧)");
            Log("INFO", "切断サマリーの出力先: " + Outage.LogDir + "\\yyyyMM_AutoLink.log");
            Log("INFO", "管理者権限: " + (IsAdmin() ? "あり" : "なし (アダプタ操作・サービス起動は失敗する可能性があります)"));
            Log("INFO", "監視対象プロセス: " + C.PerSecProcess + ".exe / PerSec再起動: " + (C.RestartPerSec ? "有効" : "無効"));
            RefreshPerSecPath();
            Log("INFO", "PerSec のパス: " + (persecPath ?? "(未検出。PerSecを起動してから次回の確認時に自動取得、または AutoLink.ini の PerSecPath に指定)"));

            lastProfile = State.Get("WifiProfile");
            lastWifiName = State.Get("WifiAdapter");

            int fails = 0, recoverFails = 0, healthy = 0;
            while (true)
            {
                int sleepSec = C.CheckIntervalSec;
                try
                {
                    string detail;
                    bool ok = Check(out detail);
                    Outage.NoteCheck(ok, detail);
                    if (ok)
                    {
                        if (fails > 0) Log("INFO", "疎通が回復しました。");
                        fails = 0;
                        if (restartPending) { restartPending = false; StartPerSec(); }
                        Log("OK", detail);
                        if (healthy % 20 == 0) { UpdateProfileCache(); }
                        if (persecPath == null) RefreshPerSecPath();
                        healthy++;
                    }
                    else
                    {
                        fails++;
                        healthy = 0;
                        Log("WARN", "異常 (" + fails + "/" + C.FailThreshold + "): " + detail);
                        if (fails >= C.FailThreshold)
                        {
                            bool rec = Recover(recoverFails);
                            if (rec) recoverFails = 0; else recoverFails++;
                            fails = 0;
                        }
                        sleepSec = C.RetryIntervalSec;
                    }
                }
                catch (Exception ex)
                {
                    Log("ERROR", "予期しないエラー: " + ex.Message);
                }
                Outage.Sleep(sleepSec);
            }
        }

        // ---------------------------------------------------------------- 状態確認

        static string lastWifiName;   // 最後に接続できていた Wi-Fi アダプタ名 (Wi-Fi Direct 等の別インターフェイスと取り違えないため)

        static void RememberWifi(string name)
        {
            if (string.IsNullOrEmpty(name) || name == lastWifiName) return;
            lastWifiName = name;
            State.Set("WifiAdapter", name);
            Log("INFO", "Wi-Fiアダプタを記録: " + name);
        }

        static NetworkInterface FindWifi()
        {
            List<NetworkInterface> list = new List<NetworkInterface>();
            try
            {
                foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) list.Add(n);
            }
            catch { }
            if (list.Count == 0) return null;

            // 1) 接続中のものを最優先 (そしてその名前を記憶)
            foreach (NetworkInterface n in list)
                if (n.OperationalStatus == OperationalStatus.Up) { RememberWifi(n.Name); return n; }

            // 2) 切断中なら、以前接続できていたアダプタ
            if (!string.IsNullOrEmpty(lastWifiName))
                foreach (NetworkInterface n in list)
                    if (n.Name == lastWifiName) return n;

            // 3) 実績がない場合は netsh wlan から見えているもの
            if (list.Count > 1)
            {
                string o = Run("netsh", "wlan show interfaces", 15000);
                foreach (NetworkInterface n in list)
                    if (o.IndexOf(n.Name, StringComparison.OrdinalIgnoreCase) >= 0) return n;
            }
            return list[0];
        }

        static bool Check(out string detail)
        {
            NetworkInterface wifi = FindWifi();
            string wname = wifi == null ? "Wi-Fiアダプタなし" : wifi.Name;
            if (wifi != null && wifi.OperationalStatus != OperationalStatus.Up)
            {
                detail = "Wi-Fi未接続 (" + wname + ": " + wifi.OperationalStatus + ")";
                return false;
            }

            string i1, i2;
            if (HttpProbe(NcsiUrl, 200, NcsiBody, out i1))
            {
                detail = wname + " 接続中 / インターネットOK (" + i1 + ")";
                return true;
            }
            if (HttpProbe(Gen204Url, 204, null, out i2))
            {
                detail = wname + " 接続中 / インターネットOK (" + i2 + ")";
                return true;
            }
            detail = wname + " 接続中だがインターネットなし [" + i1 + " | " + i2 + "]";
            return false;
        }

        static bool HttpProbe(string url, int expectStatus, string expectBody, out string info)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string host = new Uri(url).Host;
            int timeout = C.ProbeTimeoutSec * 1000;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeout;
                req.ReadWriteTimeout = timeout;
                req.Proxy = null;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.UserAgent = "Microsoft NCSI";
                req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);

                IAsyncResult ar = req.BeginGetResponse(null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout + 2000))
                {
                    req.Abort();
                    info = host + " タイムアウト";
                    return false;
                }
                using (HttpWebResponse res = (HttpWebResponse)req.EndGetResponse(ar))
                {
                    string body = "";
                    if (expectBody != null)
                    {
                        using (StreamReader sr = new StreamReader(res.GetResponseStream()))
                        {
                            body = sr.ReadToEnd().Trim();
                        }
                    }
                    bool ok = (int)res.StatusCode == expectStatus && (expectBody == null || body == expectBody);
                    info = host + (ok ? " " + sw.ElapsedMilliseconds + "ms" : " 応答不一致(" + (int)res.StatusCode + ")");
                    return ok;
                }
            }
            catch (Exception ex)
            {
                info = host + " " + ShortError(ex);
                return false;
            }
        }

        static string ShortError(Exception ex)
        {
            WebException we = ex as WebException;
            if (we != null) return we.Status.ToString();
            return ex.GetType().Name;
        }

        // ---------------------------------------------------------------- 復旧処理

        static bool Recover(int priorFailedRecoveries)
        {
            Log("WARN", "=== 復旧処理を開始します ===");
            Outage.NoteRecoverAttempt();

            NetworkInterface wifi = FindWifi();
            if (wifi == null)
            {
                Log("ERROR", "Wi-Fiアダプタが見つかりません。復旧できません。");
                return false;
            }
            string ifName = wifi.Name;
            string profile = GetProfileName();
            if (string.IsNullOrEmpty(profile))
            {
                Log("ERROR", "Wi-Fiプロファイル名を特定できません。AutoLink.ini の WifiProfile に指定してください。");
                return false;
            }

            if (C.RestartPerSec)
            {
                RefreshPerSecPath();
                KillPerSec();
            }

            // 接続が切れただけなら wlan から見える。見えない場合だけ無線/サービス/アダプタの診断・復旧に進む。
            bool radioProblem = !WlanVisible(ifName);
            if (radioProblem)
            {
                // 接続が切れただけなら wlan サービスにインターフェイスが見えている。
                // 見えていない場合は「無線OFF / WlanSvc停止 / アダプタ無効」などを順に潰す。
                string diag = EnsureWifiReady(ifName, wifi.Description);
                bool vis = WaitWlanVisible(ifName, 10);
                bool notFound = diag.IndexOf("ADAPTER=NOT_FOUND", StringComparison.Ordinal) >= 0;
                if (!vis && notFound)
                {
                    Log("ERROR", "アダプタが Windows から認識されていません。USBアダプタの抜け、またはデバイスマネージャーでの無効化/ドライバ異常の可能性があります。");
                }
                else if (!vis)
                {
                    Log("WARN", "WLANからインターフェイスが見えないため、アダプタを再起動します。");
                    Log("INFO", Oneline(Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ProgressPreference='SilentlyContinue'; Restart-NetAdapter -IncludeHidden -Name '" + ifName.Replace("'", "''") + "' -Confirm:$false\"", 40000)));
                    vis = WaitWlanVisible(ifName, 20);
                }
                if (!vis)
                {
                    Log("ERROR", "Wi-Fiインターフェイスが復活しません。管理者権限での実行、デバイスマネージャー上の無効化、USBアダプタの接続を確認してください。");
                    if (C.RestartPerSec) restartPending = true;
                    Log("WARN", "=== 復旧処理を終了します ===");
                    return false;
                }
            }
            else
            {
                Log("INFO", "Wi-Fi切断: " + ifName);
                Log("INFO", Oneline(Run("netsh", "wlan disconnect interface=\"" + ifName + "\"", 15000)));
                Thread.Sleep(3000);
            }

            if (priorFailedRecoveries >= C.AdapterResetAfterFails)
            {
                Log("WARN", "再接続に連続失敗しているため、アダプタを無効化→有効化します (管理者権限が必要)");
                Log("INFO", Oneline(Run("netsh", "interface set interface name=\"" + ifName + "\" admin=disabled", 20000)));
                Thread.Sleep(3000);
                Log("INFO", Oneline(Run("netsh", "interface set interface name=\"" + ifName + "\" admin=enabled", 20000)));
                Thread.Sleep(5000);
            }

            Log("INFO", "Wi-Fi接続: プロファイル「" + profile + "」");
            Log("INFO", Oneline(Run("netsh", "wlan connect name=\"" + profile + "\" interface=\"" + ifName + "\"", 20000)));

            bool ok = false;
            DateTime until = DateTime.Now.AddSeconds(C.ReconnectWaitSec);
            while (DateTime.Now < until)
            {
                Thread.Sleep(3000);
                string d;
                if (Check(out d)) { ok = true; Log("OK", d); Outage.NoteRecoverSuccess(); Outage.NoteCheck(true, d); break; }
            }
            Log(ok ? "INFO" : "ERROR", ok ? "再接続に成功しました。" : "再接続後も疎通を確認できませんでした。");

            if (C.RestartPerSec)
            {
                if (ok) StartPerSec();
                else
                {
                    restartPending = true;
                    Log("WARN", "ネットワーク未復旧のため PerSec の起動は復帰後まで保留します。");
                }
            }
            Log("WARN", "=== 復旧処理を終了します ===");
            return ok;
        }

        // netsh wlan 側にこのアダプタが見えているか
        static bool WlanVisible(string ifName)
        {
            string o = Run("netsh", "wlan show interfaces", 15000);
            return o.IndexOf(ifName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool WaitWlanVisible(string ifName, int sec)
        {
            DateTime until = DateTime.Now.AddSeconds(sec);
            do
            {
                if (WlanVisible(ifName)) return true;
                Thread.Sleep(2000);
            } while (DateTime.Now < until);
            return WlanVisible(ifName);
        }

        static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsPrincipal pr = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Wi-Fi が使える状態に戻すための確認と復旧を PowerShell 1回で行い、結果をログに出す。
        //  1) Wi-Fi無線(Radio)をオンにする (管理者権限不要)
        //  2) WlanSvc (WLAN AutoConfig) が止まっていたら起動する
        //  3) アダプタが無効なら Enable-NetAdapter で有効化する
        static string EnsureWifiReady(string ifName, string desc)
        {
            string script =
                "$ProgressPreference='SilentlyContinue'\n" +
                "$ErrorActionPreference='Continue'\n" +
                "$ifn = '" + ifName.Replace("'", "''") + "'\n" +
                "$desc = '" + (desc ?? "").Replace("'", "''") + "'\n" +
                "try {\n" +
                "  Add-Type -AssemblyName System.Runtime.WindowsRuntime\n" +
                "  $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]\n" +
                "  function Await($op, $t) { $m = $asTask.MakeGenericMethod($t); $task = $m.Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }\n" +
                "  [Windows.Devices.Radios.Radio,Windows.System.Devices,ContentType=WindowsRuntime] | Out-Null\n" +
                "  [Windows.Devices.Radios.RadioAccessStatus,Windows.System.Devices,ContentType=WindowsRuntime] | Out-Null\n" +
                "  $acc = Await ([Windows.Devices.Radios.Radio]::RequestAccessAsync()) ([Windows.Devices.Radios.RadioAccessStatus])\n" +
                "  if ($acc -ne 'Allowed') { 'RADIO_FAIL:access=' + $acc }\n" +
                "  else {\n" +
                "    $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])\n" +
                "    $w = @($radios | Where-Object { $_.Kind -eq 'WiFi' })\n" +
                "    if ($w.Count -eq 0) { 'RADIO_NONE' }\n" +
                "    foreach ($r in $w) {\n" +
                "      if ($r.State -eq 'On') { 'RADIO_ALREADY_ON' }\n" +
                "      else { $x = Await ($r.SetStateAsync('On')) ([Windows.Devices.Radios.RadioAccessStatus]); if ($x -eq 'Allowed') { 'RADIO_TURNED_ON' } else { 'RADIO_FAIL:set=' + $x } }\n" +
                "    }\n" +
                "  }\n" +
                "} catch { 'RADIO_ERR:' + $_.Exception.Message }\n" +
                "try { $s = Get-Service WlanSvc -ErrorAction Stop; 'WLANSVC=' + $s.Status; if ($s.Status -ne 'Running') { Start-Service WlanSvc -ErrorAction Stop; 'WLANSVC=started' } } catch { 'WLANSVC_ERR:' + $_.Exception.Message }\n" +
                "try { $a = Get-NetAdapter -Name $ifn -IncludeHidden -ErrorAction SilentlyContinue; if ($a) { 'ADAPTER=' + $a.Status + ' (' + $a.InterfaceDescription + ')'; if ($a.Status -eq 'Disabled') { Enable-NetAdapter -Name $ifn -Confirm:$false -ErrorAction Stop; 'ADAPTER_ENABLED' } } else { 'ADAPTER=NOT_FOUND (' + $desc + ')' } } catch { 'ADAPTER_ERR:' + $_.Exception.Message }\n" +
                "try {\n" +
                "  $d = @(Get-PnpDevice -Class Net -ErrorAction Stop | Where-Object { $desc -ne '' -and ($_.FriendlyName -eq $desc -or $_.FriendlyName -like ('*' + $desc + '*')) })\n" +
                "  if ($d.Count -eq 0) { $d = @(Get-PnpDevice -Class Net -ErrorAction Stop | Where-Object { $_.FriendlyName -match 'Wi-?Fi|Wireless|802\\.11|WLAN' }) }\n" +
                "  if ($d.Count -eq 0) { 'PNP=NONE' }\n" +
                "  foreach ($x in $d) {\n" +
                "    'PNP=' + $x.FriendlyName + ' status=' + $x.Status + ' problem=' + $x.Problem + ' present=' + $x.Present\n" +
                "    if ($x.Status -ne 'OK') { if ($x.Present) { Enable-PnpDevice -InstanceId $x.InstanceId -Confirm:$false -ErrorAction Stop; 'PNP_ENABLED' } else { 'PNP_NOT_PRESENT' } }\n" +
                "  }\n" +
                "} catch { 'PNP_ERR:' + $_.Exception.Message }\n";

            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string o = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, 45000);
            int cx = o.IndexOf("#< CLIXML", StringComparison.Ordinal);
            if (cx >= 0) o = o.Substring(0, cx);
            foreach (string line in o.Replace("\r", "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) Log("INFO", "診断: " + t);
            }
            if (o.IndexOf("RADIO_TURNED_ON", StringComparison.Ordinal) >= 0 || o.IndexOf("ADAPTER_ENABLED", StringComparison.Ordinal) >= 0 || o.IndexOf("PNP_ENABLED", StringComparison.Ordinal) >= 0 || o.IndexOf("WLANSVC=started", StringComparison.Ordinal) >= 0)
                Thread.Sleep(5000); // インターフェイスの出現待ち
            return o;
        }

        static string GetProfileName()
        {
            if (!string.IsNullOrEmpty(C.WifiProfile)) return C.WifiProfile;
            UpdateProfileCache();
            if (!string.IsNullOrEmpty(lastProfile)) return lastProfile;

            // 最後の手段: 保存済みプロファイルの先頭
            foreach (string line in Run("netsh", "wlan show profiles", 15000).Split('\n'))
            {
                string k, v;
                if (!SplitKv(line, out k, out v)) continue;
                if ((k.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("プロファイル", StringComparison.Ordinal) >= 0) && v.Length > 0)
                {
                    Log("WARN", "接続中のプロファイルを取得できないため、保存済みの「" + v + "」を使用します。");
                    return v;
                }
            }
            return null;
        }

        // 接続中のプロファイル名を netsh から取得してキャッシュ (日本語/英語どちらの表示にも対応)
        static void UpdateProfileCache()
        {
            if (!string.IsNullOrEmpty(C.WifiProfile)) return;
            string name = null;
            foreach (string line in Run("netsh", "wlan show interfaces", 15000).Split('\n'))
            {
                string k, v;
                if (!SplitKv(line, out k, out v)) continue;
                if ((k == "Profile" || k == "プロファイル") && v.Length > 0) { name = v; break; }
            }
            if (name != null && name != lastProfile)
            {
                lastProfile = name;
                State.Set("WifiProfile", name);
                Log("INFO", "Wi-Fiプロファイルを記録: " + name);
            }
        }

        static bool SplitKv(string line, out string k, out string v)
        {
            k = v = null;
            int i = line.IndexOf(':');
            if (i <= 0) return false;
            k = line.Substring(0, i).Trim();
            v = line.Substring(i + 1).Trim();
            return true;
        }

        // ---------------------------------------------------------------- PerSec 制御

        static void RefreshPerSecPath()
        {
            string p = null;
            if (!string.IsNullOrEmpty(C.PerSecPath) && File.Exists(C.PerSecPath)) p = C.PerSecPath;
            if (p == null) p = RunningPerSecPath();
            if (p == null)
            {
                string t = State.Get("PerSecPath");
                if (t.Length > 0 && File.Exists(t)) p = t;
            }
            if (p == null) p = SearchPerSec();
            if (p != null && p != persecPath)
            {
                persecPath = p;
                State.Set("PerSecPath", p);
                Log("INFO", "PerSec のパスを記録: " + p);
            }
        }

        static string RunningPerSecPath()
        {
            string path = null;
            try
            {
                foreach (Process p in Process.GetProcessesByName(C.PerSecProcess))
                {
                    try { if (path == null) path = p.MainModule.FileName; } catch { }
                    p.Dispose();
                }
            }
            catch { }
            return path;
        }

        static string SearchPerSec()
        {
            string[] roots = new string[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA") == null ? null : Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Programs"),
                Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                Environment.GetEnvironmentVariable("APPDATA")
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string d in Directory.GetDirectories(root, "*Parsec*"))
                    {
                        string exe = Path.Combine(d, C.PerSecProcess + ".exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
                catch { }
            }
            return null;
        }

        static void KillPerSec()
        {
            Process[] ps = new Process[0];
            try { ps = Process.GetProcessesByName(C.PerSecProcess); } catch { }
            if (ps.Length == 0)
            {
                Log("INFO", C.PerSecProcess + ".exe は起動していません。");
                return;
            }
            List<int> old = new List<int>();
            foreach (Process p in ps) { old.Add(p.Id); p.Dispose(); }
            Log("INFO", C.PerSecProcess + ".exe をタスクキルします。");
            Log("INFO", Oneline(Run("taskkill", "/F /T /IM \"" + C.PerSecProcess + ".exe\"", 15000)));

            bool stillAlive = true;
            for (int i = 0; i < 10 && stillAlive; i++)
            {
                stillAlive = false;
                foreach (int id in old)
                {
                    try { Process.GetProcessById(id).Dispose(); stillAlive = true; } catch { }
                }
                if (stillAlive) Thread.Sleep(500);
            }
            if (stillAlive)
            {
                Log("WARN", "PerSec が終了していません。管理者権限で実行されている可能性があります (AutoLink も管理者で実行してください)。");
                return;
            }

            // 終了後に別PIDで現れた場合は、Parsec のサービス等による自動再起動
            Thread.Sleep(2000);
            try
            {
                Process[] again = Process.GetProcessesByName(C.PerSecProcess);
                int n = again.Length;
                foreach (Process p in again) p.Dispose();
                if (n > 0) Log("INFO", C.PerSecProcess + ".exe は終了後に自動で再起動されています (Parsecのサービスによる再起動と思われます)。");
            }
            catch { }
        }

        static void StartPerSec()
        {
            try
            {
                Process[] run = Process.GetProcessesByName(C.PerSecProcess);
                int n = run.Length;
                foreach (Process r in run) r.Dispose();
                if (n > 0) { Log("INFO", C.PerSecProcess + ".exe は既に起動しています。"); return; }
            }
            catch { }
            if (persecPath == null) RefreshPerSecPath();
            if (persecPath == null)
            {
                Log("ERROR", "PerSec.exe のパスが不明なため再起動できません。AutoLink.ini の PerSecPath に指定してください。");
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(persecPath);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(persecPath);
                Process.Start(psi);
                Log("INFO", "PerSec を起動しました: " + persecPath);
            }
            catch (Exception ex)
            {
                Log("ERROR", "PerSec の起動に失敗: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- ユーティリティ

        static string Oneline(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", "").Trim().Replace("\n", " / ");
        }

        static string Decode(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch
            {
                try { return Encoding.GetEncoding((int)GetOEMCP()).GetString(b); }
                catch { return Encoding.Default.GetString(b); }
            }
        }

        static string Run(string file, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    MemoryStream errMs = new MemoryStream();
                    Thread t = new Thread(delegate () { try { p.StandardError.BaseStream.CopyTo(errMs); } catch { } });
                    t.IsBackground = true;
                    t.Start();
                    MemoryStream outMs = new MemoryStream();
                    p.StandardOutput.BaseStream.CopyTo(outMs);
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    t.Join(2000);
                    return Decode(outMs.ToArray()) + Decode(errMs.ToArray());
                }
            }
            catch (Exception ex)
            {
                return "ERR: " + ex.Message;
            }
        }
    }
}
