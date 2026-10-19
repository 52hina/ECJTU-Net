// ECJTU 校园网自动登录 (Dr.COM ePortal) —— 便携单文件版
// 用法:
//   双击           -> 打开设置界面(填账号/密码/运营商), 可立即测试连接
//   /silent /login -> 无界面静默连接(供开机自启/任务计划使用), 连上后自动退出
//   开机自启: 把快捷方式放进「启动」文件夹, 或建任务计划, 目标参数加 /silent
// 配置与日志保存在 exe 同目录: config.ini / login.log (密码用 Windows DPAPI 加密)
// 编译: csc /codepage:65001 /target:winexe /out:ECJTU-Net.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll Program.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// 程序版本/产品信息（减少杀毒软件对"无版本信息程序"的启发式误报）
[assembly: AssemblyTitle("校园网自动登录")]
[assembly: AssemblyDescription("校园网自动连接工具")]
[assembly: AssemblyProduct("ECJTU-Net")]
[assembly: AssemblyCompany("ECJTU-Net")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

namespace EcjtuNet
{
    // ------------------------------------------------------------------ 路径
    static class Paths
    {
        static string resolved;
        public static string DataDir
        {
            get
            {
                if (!string.IsNullOrEmpty(resolved)) return resolved;

                // exe 所在目录(默认数据目录)
                string exeDir = null;
                try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); } catch { }
                if (string.IsNullOrEmpty(exeDir))
                {
                    try { exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } catch { }
                }

                // 仅当环境变量指定的目录确实存在时才用(测试用), 否则一律用 exe 目录,
                // 避免配置/日志写到不存在的路径导致"保存失败: 找不到路径"
                string ov = Environment.GetEnvironmentVariable("ECJTU_DATA_DIR");
                if (!string.IsNullOrEmpty(ov) && Directory.Exists(ov))
                {
                    resolved = ov;
                    return resolved;
                }
                resolved = exeDir;
                return resolved;
            }
        }
    }

    // ------------------------------------------------------------------ 图标
    static class AppIcon
    {
        static Icon cached;
        public static Icon Get()
        {
            if (cached != null) return cached;
            // 1) 优先读取 exe 同目录的高清图标文件（不把大资源内嵌进 exe，避免被 Smart App Control 拦截）
            try
            {
                string p = Path.Combine(Paths.DataDir, "ECJTU-Net.ico");
                if (File.Exists(p)) { cached = new Icon(p); return cached; }
            }
            catch { }
            // 2) 退回 exe 内嵌的小图标
            try { cached = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            if (cached == null) cached = SystemIcons.Application;
            return cached;
        }
    }

    // ------------------------------------------------------------------ 日志
    static class Log
    {
        static readonly object Gate = new object();
        static string LastTrimDate = "";
        public static string FilePath { get { return Path.Combine(Paths.DataDir, "ECJTU-Net.log"); } }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (Gate)
            {
                try
                {
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    if (LastTrimDate != today)
                    {
                        LastTrimDate = today;
                        Trim(30);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        // 只保留最近 keepDays 天的日志, 更早的自动删掉(每天清理一次)
        static void Trim(int keepDays)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                DateTime cutoff = DateTime.Now.Date.AddDays(-keepDays);
                string[] all = File.ReadAllLines(FilePath, Encoding.UTF8);
                List<string> keep = new List<string>();
                foreach (string ln in all)
                {
                    DateTime d;
                    if (ln.Length >= 10 && DateTime.TryParseExact(ln.Substring(0, 10), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    {
                        if (d < cutoff) continue;
                    }
                    keep.Add(ln);
                }
                if (keep.Count != all.Length)
                {
                    File.WriteAllLines(FilePath, keep.ToArray(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ 配置
    class Config
    {
        public string User = "";
        public string Password = "";
        public string Isp = "@cmcc";          // 运营商后缀, 直接拼在账号后面
        public string Ssid = "ECJTU-Stu";
        public bool AutoWifi = true;
        public bool AutoStart = false;
        public bool Watchdog = true;
        public bool Enabled = true;      // 总开关: 不在学校时可关闭, 程序完全不运行

        public static string FilePath { get { return Path.Combine(Paths.DataDir, "ECJTU-Net.ini"); } }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "user": c.User = v; break;
                        case "pass": c.Password = Crypto.Unprotect(v); break;
                        case "isp": c.Isp = v; break;
                        case "ssid": c.Ssid = v; break;
                        case "autowifi": c.AutoWifi = v == "1"; break;
                        case "autostart": c.AutoStart = v == "1"; break;
                        case "watchdog": c.Watchdog = v == "1"; break;
                        case "enabled": c.Enabled = v != "0"; break;
                    }
                }
            }
            catch (Exception ex) { Log.Write("读取配置失败: " + ex.Message); }
            return c;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 校园网自动登录配置(密码已用 Windows DPAPI 加密, 仅本机本用户可解)");
            sb.AppendLine("user=" + User);
            sb.AppendLine("pass=" + Crypto.Protect(Password));
            sb.AppendLine("isp=" + Isp);
            sb.AppendLine("ssid=" + Ssid);
            sb.AppendLine("autowifi=" + (AutoWifi ? "1" : "0"));
            sb.AppendLine("autostart=" + (AutoStart ? "1" : "0"));
            sb.AppendLine("watchdog=" + (Watchdog ? "1" : "0"));
            sb.AppendLine("enabled=" + (Enabled ? "1" : "0"));
            // 原子写入: 先写临时文件再替换, 防止守护进程读到半截文件
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
            Log.Write("配置已保存: 账号 " + MaskUser(User) + Isp);
        }

        public string FullUser
        {
            get
            {
                string u = (User ?? "").Trim();
                string s = (Isp ?? "").Trim();
                if (s == "" || s == "-1") return u;
                if (u.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return u;
                return u + s;
            }
        }

        public static string MaskUser(string u)
        {
            if (string.IsNullOrEmpty(u)) return "";
            if (u.Length <= 4) return "****";
            return u.Substring(0, 4) + new string('*', Math.Min(6, u.Length - 4));
        }
    }

    // ------------------------------------------------------------ 密码加密
    static class Crypto
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return "dpapi:" + Convert.ToBase64String(enc);
            }
            catch (Exception ex) { Log.Write("密码加密失败: " + ex.Message); return "plain:" + plain; }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                if (stored.StartsWith("dpapi:"))
                {
                    byte[] enc = Convert.FromBase64String(stored.Substring(6));
                    byte[] data = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(data);
                }
                if (stored.StartsWith("plain:")) return stored.Substring(6);
                return stored;
            }
            catch (Exception ex) { Log.Write("密码解密失败(可能换了用户或机器): " + ex.Message); return ""; }
        }
    }

    // -------------------------------------------------------------- 开机自启(启动文件夹快捷方式)
    static class AutoStart
    {
        public static string StartupFolder
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }
        public static string LnkPath
        {
            get { return Path.Combine(StartupFolder, "ECJTU-Net.lnk"); }
        }

        public static bool IsEnabled()
        {
            try { return File.Exists(LnkPath); }
            catch { return false; }
        }

        public static bool Enable()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string dir = Path.GetDirectoryName(exe);
                Directory.CreateDirectory(StartupFolder);
                try { if (File.Exists(LnkPath)) File.Delete(LnkPath); } catch { }

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) { Log.Write("开机自启失败: 系统缺少 WScript.Shell"); return false; }
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LnkPath });
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                scType.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { "/silent" });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Log.Write("已启用开机自启: " + LnkPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("启用开机自启失败: " + ex.Message);
                return false;
            }
        }

        public static void Disable()
        {
            try
            {
                if (File.Exists(LnkPath)) { File.Delete(LnkPath); Log.Write("已取消开机自启"); }
            }
            catch (Exception ex) { Log.Write("取消开机自启失败: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------ 守护进程控制
    static class WatchdogControl
    {
        const string StopEventName = "Local\\ECJTU-Net-WatchdogStop";
        const string MutexName = "Local\\ECJTU-Net-Watchdog";

        // 唤醒已在运行的守护, 让它立即重读配置
        public static void WakeExisting()
        {
            try
            {
                bool created;
                using (EventWaitHandle e = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName, out created))
                {
                    e.Set();
                }
            }
            catch { }
        }

        public static bool IsRunning()
        {
            try
            {
                bool createdNew;
                using (Mutex m = new Mutex(false, MutexName, out createdNew))
                {
                    bool owned;
                    try { owned = m.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) return true;
                    m.ReleaseMutex();
                    return false;
                }
            }
            catch { return false; }
        }

        public static void Start()
        {
            try
            {
                Process.Start(Application.ExecutablePath, "/silent");
                Log.Write("已启动掉线守护进程");
            }
            catch (Exception ex) { Log.Write("启动守护失败: " + ex.Message); }
        }

        // 保存设置后调用: 立即让守护应用新设置(账号/密码/勾选项全部生效)
        public static void ApplyAfterSave(bool enabled, bool watchdogOn)
        {
            WakeExisting();   // 唤醒已有守护: 重读配置; 若关闭了启用/守护, 它会自行退出
            if (enabled && watchdogOn && !IsRunning())
            {
                Start();
            }
        }
    }

    // ------------------------------------------------------------ 网络/门户
    class ProbeResult
    {
        public bool Online;
        public string PortalUrl = "";
        public string Detail = "";
    }

    class LoginResult
    {
        public bool Ok;
        public bool AlreadyOnline;
        public string Message = "";
        public string Response = "";
    }

    static class Net
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        static readonly Encoding Gbk = Encoding.GetEncoding(936);

        // ---------- 当前使用的 IPv4（取默认路由中 metric 最小的那条）
        public static string ActiveIPv4()
        {
            string ip = RouteTableIPv4();
            if (!string.IsNullOrEmpty(ip)) return ip;
            return FallbackIPv4();
        }

        static string RouteTableIPv4()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("route", "print -4");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string line;
                    int bestMetric = int.MaxValue;
                    string bestIp = null;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        string[] t = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (t.Length != 5) continue;
                        if (t[0] != "0.0.0.0" || t[1] != "0.0.0.0") continue;
                        int metric;
                        if (!int.TryParse(t[4], out metric)) continue;
                        if (!IsUsableIPv4(t[3])) continue;
                        if (IsVirtualIp(t[3])) continue;   // 虚拟网卡(VPN等)的路由不算
                        if (metric < bestMetric) { bestMetric = metric; bestIp = t[3]; }
                    }
                    try { p.WaitForExit(3000); } catch { }
                    if (bestIp != null && IsUsableIPv4(bestIp)) return bestIp;
                }
            }
            catch (Exception ex) { Log.Write("读取路由表失败: " + ex.Message); }
            return null;
        }

        static string FallbackIPv4()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsVirtualAdapter(ni)) continue;   // 虚拟网卡不算
                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (IsUsableIPv4(s)) return s;
                    }
                }
            }
            catch { }
            return null;
        }

        static bool IsUsableIPv4(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            if (!Regex.IsMatch(ip, @"^\d{1,3}(\.\d{1,3}){3}$")) return false;
            if (ip.StartsWith("127.") || ip.StartsWith("169.254.") || ip == "0.0.0.0") return false;
            return true;
        }

        // 虚拟网卡(Radmin VPN / TAP / TUN / Wi-Fi Direct 等)不算真实网络
        static bool IsVirtualAdapter(NetworkInterface ni)
        {
            try
            {
                string s = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
                if (s.Contains("virtual") || s.Contains("vpn") || s.Contains("tap") || s.Contains("tun")
                    || s.Contains("radmin") || s.Contains("loopback") || s.Contains("teredo")
                    || s.Contains("wi-fi direct") || s.Contains("pseudo")) return true;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
            }
            catch { }
            return false;
        }

        static bool IsVirtualIp(string ip)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.Address.ToString() == ip)
                            return IsVirtualAdapter(ni);
                    }
                }
            }
            catch { }
            return false;
        }

        public static string MacForIPv4(string ip)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.Address.ToString() != ip) continue;
                        byte[] b = ni.GetPhysicalAddress().GetAddressBytes();
                        if (b == null || b.Length != 6) break;
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < b.Length; i++)
                        {
                            if (i > 0) sb.Append('-');
                            sb.Append(b[i].ToString("x2"));
                        }
                        return sb.ToString();
                    }
                }
            }
            catch { }
            return "00-00-00-00-00-00";
        }

        public static bool HasAnyNetwork()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (IsVirtualAdapter(ni)) continue;   // 虚拟网卡(VPN等)不算真实网络
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsUsableIPv4(ua.Address.ToString()))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // 等网络就绪: 在总时长内(开机约 45 秒)持续尝试帮连, 连上就立刻继续, 到点还不行就放弃
        //   · 已经有网 → 直接用
        //   · 没网 → 主动帮连校园 WiFi(无线慢时会补试几次, 不做任何 Windows 状态判断)
        //   · 到时间还连不上 → 返回 false, 由调用方退出(不再隔一段时间反复试)
        // 网线是否插着(以太网链路是否通, 排除 Radmin VPN 等虚拟网卡)
        public static bool HasEthernetLink()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    if (IsVirtualAdapter(ni)) continue;
                    if (ni.OperationalStatus == OperationalStatus.Up) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool EnsureNetwork(Config cfg, int totalWaitSec)
        {
            if (HasEthernetLink()) Log.Write("检测到网线已插入, 优先等有线网络…");
            bool radioWarned = false;

            for (int i = 0; i < totalWaitSec; i++)
            {
                if (HasAnyNetwork()) return true;

                // 有网线 → 只等有线, 不去动 WiFi(避免有线+无线同时连被算两台设备);
                // 没网线 → 不用管 Windows 开没开自动连接, 程序立即直接连校园 WiFi
                //          (前 30 秒 4 次); 到点还连不上就退出, 交回给 Windows
                if (!HasEthernetLink() && cfg.AutoWifi && !string.IsNullOrEmpty(cfg.Ssid) && (i == 0 || i == 7 || i == 14 || i == 21))
                {
                    if (!radioWarned)
                    {
                        radioWarned = true;
                        if (IsWifiRadioOff())
                            Log.Write("⚠ WiFi 开关处于关闭状态（软件关闭），程序无法自动连接，请手动打开 WiFi 开关");
                    }
                    Log.Write((i == 0 ? "没有网线, 不等 Windows, 直接连接校园 WiFi: " : "仍未连上, 再试一次连接校园 WiFi: ") + cfg.Ssid);
                    try { ConnectWifi(cfg.Ssid); } catch { }
                }

                Thread.Sleep(1000);
            }
            return HasAnyNetwork();
        }

        // WiFi 开关是否处于软件关闭状态(这种情况任何程序都连不上)
        public static bool IsWifiRadioOff()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show interfaces");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    try { p.WaitForExit(3000); } catch { }
                    string low = (output ?? "").ToLowerInvariant();
                    if (low.Contains("software off") || low.Contains("软件关闭") || low.Contains("软件 关")) return true;
                }
            }
            catch { }
            return false;
        }

        // 扫描附近 WiFi, 返回所有可见 SSID
        public static List<string> ScanWifiSsid()
        {
            List<string> list = new List<string>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show networks mode=bssid");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    try { p.WaitForExit(8000); } catch { }
                    string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        Match m = Regex.Match(line, @"^\s*SSID\s*\d*\s*:\s*(.+?)\s*$");
                        if (m.Success)
                        {
                            string v = m.Groups[1].Value.Trim();
                            if (v.Length > 0 && !list.Contains(v)) list.Add(v);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // 附近是否能看到目标 WiFi
        public static bool SsidInRange(string ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return false;
            foreach (string s in ScanWifiSsid())
                if (string.Equals(s, ssid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 是否已有该 WiFi 的配置(profile)
        static bool HasWifiProfile(string ssid)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "wlan show profiles");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    try { p.WaitForExit(5000); } catch { }
                    return output.IndexOf(": " + ssid, StringComparison.OrdinalIgnoreCase) >= 0
                        || output.IndexOf(":" + ssid, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        // 没有 profile 时自动创建一个「开放网络」配置, 这样 netsh wlan connect 才能连
        // profile 检查结果缓存: 一次进程只查一次 netsh(每次连接前都查会拖慢搜索连接)
        static bool profileChecked = false;
        static bool profileExists = false;
        static void EnsureWifiProfile(string ssid)
        {
            if (!profileChecked)
            {
                profileChecked = true;
                profileExists = HasWifiProfile(ssid);
            }
            if (profileExists) return;
            try
            {
                string xml =
                    "<?xml version=\"1.0\"?>\r\n" +
                    "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\r\n" +
                    "  <name>" + ssid + "</name>\r\n" +
                    "  <SSIDConfig>\r\n" +
                    "    <SSID>\r\n" +
                    "      <name>" + ssid + "</name>\r\n" +
                    "    </SSID>\r\n" +
                    "  </SSIDConfig>\r\n" +
                    "  <connectionType>ESS</connectionType>\r\n" +
                    "  <connectionMode>auto</connectionMode>\r\n" +
                    "  <MSM>\r\n" +
                    "    <security>\r\n" +
                    "      <authEncryption>\r\n" +
                    "        <authentication>open</authentication>\r\n" +
                    "        <encryption>none</encryption>\r\n" +
                    "        <useOneX>false</useOneX>\r\n" +
                    "      </authEncryption>\r\n" +
                    "    </security>\r\n" +
                    "  </MSM>\r\n" +
                    "</WLANProfile>";
                string tmp = Path.Combine(Path.GetTempPath(), "ecjtu-wifi-" + ssid + ".xml");
                File.WriteAllText(tmp, xml, new UTF8Encoding(false));
                try
                {
                    RunNetsh("wlan add profile filename=\"" + tmp + "\"");
                    Log.Write("已自动创建 WiFi 配置: " + ssid);
                }
                finally { try { File.Delete(tmp); } catch { } }
                profileExists = true;
            }
            catch (Exception ex) { Log.Write("创建 WiFi 配置失败: " + ex.Message); }
        }

        static void RunNetsh(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.Default;
                psi.StandardErrorEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    try { p.WaitForExit(8000); } catch { }
                }
            }
            catch { }
        }

        // ---------- 连接 WiFi: 自动搜索 + 确保有配置 + 连接
        public static bool ConnectWifi(string ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return false;
            try
            {
                EnsureWifiProfile(ssid);
                string args = "wlan connect name=\"" + ssid + "\"";
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.Default;
                psi.StandardErrorEncoding = Encoding.Default;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    try { p.WaitForExit(8000); } catch { }
                    Log.Write("连接 WiFi \"" + ssid + "\" -> " + Shorten(output, 160));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Write("连接 WiFi 失败(退回交给 Windows): " + ex.Message);
                return false;
            }
        }

        public static bool CanReachHost(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        // 判断当前是不是校园网: 能连到校园认证服务器(172.16.2.100)就是
        public static bool IsCampusNetwork()
        {
            return CanReachHost("172.16.2.100", 80, 2000) || CanReachHost("172.16.2.100", 801, 2000);
        }

        // ---------- 在线检测
        // 权威判据: 微软 NCSI 地址, 未认证时会被 AC 劫持跳转到认证页(172.16.2.100/a79.htm)
        // 辅助判据: 小米 204。不使用 captive.apple.com(离线时它也可能返回 Success, 会误判)
        public static ProbeResult Probe(int timeoutMs)
        {
            ProbeResult r = new ProbeResult();
            TryProbe("http://www.msftconnecttest.com/connecttest.txt", "msftconnecttest.com", "Microsoft Connect Test", timeoutMs, r);
            if (r.Online) return r;
            TryProbe204("http://connect.rom.miui.com/generate_204", timeoutMs, r);
            if (r.Online) return r;
            return r;
        }

        static void TryProbe(string url, string expectHost, string expectBody, int timeoutMs, ProbeResult r)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = 6;
                req.UserAgent = UA;
                req.Proxy = null;
                req.KeepAlive = false;
                req.ServicePoint.Expect100Continue = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string finalUrl = resp.ResponseUri != null ? resp.ResponseUri.ToString() : url;
                    string body = "";
                    try { using (Stream s = resp.GetResponseStream()) using (StreamReader sr = new StreamReader(s, Gbk)) body = sr.ReadToEnd(); }
                    catch { }
                    bool sameHost = finalUrl.IndexOf(expectHost, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (sameHost && body.IndexOf(expectBody, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        r.Online = true;
                        r.Detail = "在线 (" + expectHost + ")";
                        return;
                    }
                    if (!sameHost)
                    {
                        r.PortalUrl = finalUrl;
                        r.Detail = "被劫持到认证页: " + finalUrl;
                    }
                    else
                    {
                        r.Detail = "返回内容异常: " + Shorten(body, 120);
                    }
                }
            }
            catch (WebException we)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null && hr.ResponseUri != null && !string.IsNullOrEmpty(hr.ResponseUri.ToString()))
                {
                    r.PortalUrl = hr.ResponseUri.ToString();
                    r.Detail = "认证页: " + r.PortalUrl;
                }
                else r.Detail = "网络错误: " + we.Message;
            }
            catch (Exception ex) { r.Detail = "网络错误: " + ex.Message; }
        }

        static void TryProbe204(string url, int timeoutMs, ProbeResult r)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = true; req.UserAgent = UA; req.Proxy = null;
                req.KeepAlive = false; req.ServicePoint.Expect100Continue = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string finalUrl = resp.ResponseUri != null ? resp.ResponseUri.ToString() : url;
                    if ((int)resp.StatusCode == 204 && finalUrl.IndexOf("generate_204", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        r.Online = true; r.Detail = "在线 (204)"; return;
                    }
                    if (finalUrl.IndexOf("generate_204", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        r.PortalUrl = finalUrl; r.Detail = "认证页: " + finalUrl;
                    }
                }
            }
            catch { }
        }

        static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            if (query.StartsWith("?")) query = query.Substring(1);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                try { k = Uri.UnescapeDataString(k); v = Uri.UnescapeDataString(v); } catch { }
                d[k] = v;
            }
            return d;
        }

        // ---------- 门户登录
        public static LoginResult PortalLogin(Config cfg)
        {
            LoginResult result = new LoginResult();
            if (string.IsNullOrEmpty(cfg.User) || string.IsNullOrEmpty(cfg.Password))
            {
                result.Message = "未配置账号或密码";
                return result;
            }

            string ip = ActiveIPv4();
            if (string.IsNullOrEmpty(ip))
            {
                // 刚连上 WiFi/网线时 DHCP 要几秒才拿到 IP, 先等最多 8 秒再判断
                Log.Write("网卡还没拿到 IP, 等待 DHCP…");
                for (int i = 0; i < 16 && string.IsNullOrEmpty(ip); i++)
                {
                    Thread.Sleep(500);
                    ip = ActiveIPv4();
                }
                if (string.IsNullOrEmpty(ip))
                {
                    result.Message = "网卡尚未获取到 IP（请稍等几秒，或检查 WiFi/网线）";
                    return result;
                }
            }

            ProbeResult probe = Probe(3000);
            if (probe.Online)
            {
                result.Ok = true;
                result.AlreadyOnline = true;
                result.Message = "已经在线，无需登录";
                return result;
            }

            string portalHost = "172.16.2.100";
            string acName = "ecjtu_nic_ME60";
            string acIp = "172.16.1.2";
            string userIp = ip;

            if (!string.IsNullOrEmpty(probe.PortalUrl))
            {
                try
                {
                    Uri u = new Uri(probe.PortalUrl);
                    if (!string.IsNullOrEmpty(u.Host)) portalHost = u.Host;
                    Dictionary<string, string> q = ParseQuery(u.Query);
                    string v;
                    if (q.TryGetValue("wlanuserip", out v) && !string.IsNullOrEmpty(v)) userIp = v;
                    if (q.TryGetValue("wlanacname", out v) && !string.IsNullOrEmpty(v)) acName = v;
                    if (!q.TryGetValue("wlanacip", out v) || string.IsNullOrEmpty(v)) q.TryGetValue("wlancip", out v);
                    if (!string.IsNullOrEmpty(v)) acIp = v;
                }
                catch (Exception ex) { Log.Write("解析认证页参数失败: " + ex.Message); }
            }

            string mac = MacForIPv4(ip);
            string url = "http://" + portalHost + ":801/eportal/?" + BuildLoginQuery(portalHost, userIp, acName, acIp, mac);
            string body = BuildLoginBody(cfg.FullUser, cfg.Password);

            Log.Write("发起认证: 账号=" + Config.MaskUser(cfg.User) + cfg.Isp + " IP=" + userIp + " AC=" + acName + "/" + acIp);

            string respText = "";
            string finalUrl = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.UserAgent = UA;
                req.Proxy = null;
                req.Timeout = 12000;
                req.ReadWriteTimeout = 12000;
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = 5;
                req.KeepAlive = false;
                req.ServicePoint.Expect100Continue = false;
                byte[] data = Encoding.ASCII.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream rs = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(rs, Gbk))
                {
                    try { finalUrl = resp.ResponseUri != null ? resp.ResponseUri.ToString() : ""; } catch { }
                    respText = sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                HttpWebResponse hr = we.Response as HttpWebResponse;
                if (hr != null)
                {
                    try { using (StreamReader sr = new StreamReader(hr.GetResponseStream(), Gbk)) respText = sr.ReadToEnd(); }
                    catch { }
                }
                if (string.IsNullOrEmpty(respText))
                {
                    result.Message = "无法连接认证服务器: " + we.Message;
                    Log.Write(result.Message + " URL=" + url);
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Message = "认证请求异常: " + ex.Message;
                Log.Write(result.Message + " URL=" + url);
                return result;
            }

            result.Response = respText;
            string plain = Regex.Replace(respText ?? "", @"\s+", " ").Trim();

            bool successPage = plain.IndexOf("Dr.COMWebLoginID_3", StringComparison.OrdinalIgnoreCase) >= 0;
            bool failPage = plain.IndexOf("Dr.COMWebLoginID_2", StringComparison.OrdinalIgnoreCase) >= 0;

            // 探测确认: 成功页多给几次机会; 失败页只快速探 2 次(避免白等)
            int probeCount = successPage ? 4 : 2;
            for (int i = 0; i < probeCount; i++)
            {
                Thread.Sleep(i == 0 ? 400 : 500);
                ProbeResult after = Probe(3000);
                if (after.Online)
                {
                    result.Ok = true;
                    result.Message = "登录成功，网络已连通";
                    return result;
                }
            }

            if (successPage)
            {
                result.Ok = true;
                result.Message = "认证页提示成功，但网络探测未通过（请稍后重试）";
                Log.Write("警告: 认证成功页但网络探测失败");
                return result;
            }

            string hint = ExtractError(plain);
            string codeInfo = ParseRetCode(finalUrl);
            result.Ok = false;
            result.Message = "认证失败"
                + (codeInfo.Length > 0 ? " [" + codeInfo + "]" : "")
                + (hint.Length > 0 ? "：" + hint : (failPage && codeInfo.Length == 0 ? "（请检查账号/密码/运营商）" : ""));
            Log.Write("认证失败" + (codeInfo.Length > 0 ? " [" + codeInfo + "]" : "") + " 响应片段: " + Shorten(plain, 300));
            return result;
        }

        static string BuildLoginQuery(string host, string userIp, string acName, string acIp, string mac)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("c=ACSetting&a=Login");
            sb.Append("&protocol=" + Uri.EscapeDataString("http:"));
            sb.Append("&hostname=" + Uri.EscapeDataString(host));
            sb.Append("&port=");
            sb.Append("&iTermType=1");
            sb.Append("&wlanuserip=" + Uri.EscapeDataString(userIp));
            sb.Append("&wlanacip=" + Uri.EscapeDataString(acIp));
            sb.Append("&wlanacname=" + Uri.EscapeDataString(acName));
            sb.Append("&mac=" + Uri.EscapeDataString(mac));
            sb.Append("&ip=" + Uri.EscapeDataString(userIp));
            sb.Append("&redirect=&session=&enAdvert=0&queryACIP=0&loginMethod=1");
            return sb.ToString();
        }

        static string BuildLoginBody(string fullUser, string password)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("DDDDD=" + Uri.EscapeDataString(fullUser));
            sb.Append("&upass=" + Uri.EscapeDataString(password));
            sb.Append("&R1=0&R2=0&R3=0&R6=0&para=00&0MKKey=123456");
            sb.Append("&buttonClicked=&redirect_url=&err_flag=&username=&password=&user=&cmd=&Login=");
            return sb.ToString();
        }

        // 从失败跳转 URL 里取 Dr.COM 的返回码, 例如 ...&RetCode=512&ErrorMsg=NTEy
        static string ParseRetCode(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "";
                Match m = Regex.Match(url, @"[?&]RetCode=([^&]*)", RegexOptions.IgnoreCase);
                if (!m.Success) return "";
                string rc = Uri.UnescapeDataString(m.Groups[1].Value).Trim();
                string desc = RetCodeText(rc);
                return "RetCode=" + rc + (desc.Length > 0 ? " " + desc : "");
            }
            catch { return ""; }
        }

        // Dr.COM 返回码说明(取自认证页脚本 a41.js 的注释)
        static string RetCodeText(string code)
        {
            switch ((code ?? "").Trim())
            {
                case "0": return "成功";
                case "1": return "账号或密码不对";
                case "2": return "终端IP已经在线";
                case "3": return "系统忙";
                case "4": return "未知错误";
                case "5": return "REQ_CHALLENGE失败";
                case "6": return "REQ_CHALLENGE超时";
                case "7": return "认证失败";
                case "8": return "认证超时";
                case "9": return "计费失败";
                case "10": return "下线超时";
                case "11": return "操作失败";
                case "512": return "账号已在线（可能已在别的设备登录，或旧会话未超时）";
                default: return "";
            }
        }

        static string ExtractError(string plain)
        {
            try
            {
                Match m = Regex.Match(plain, @"alert\s*\(\s*[""']([^""']{2,120})[""']\s*\)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
                m = Regex.Match(plain, @"dr1003\s*\(\s*[^,]*,\s*[""']([^""']{2,120})[""']", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
                m = Regex.Match(plain, @"(密码错误|账号或密码|用户名或密码|用户不存在|欠费|停机|已在线|余额不足|认证失败|账号已|用户已|不允许|无效)[^<""',;]{0,40}");
                if (m.Success) return m.Value.Trim();
            }
            catch { }
            return "";
        }
    }

    // ------------------------------------------------------------------ 静默连接
    static class Silent
    {
        public static int Run(Config cfg)
        {
            Log.Write("==================== 静默连接开始 ====================");
            if (string.IsNullOrEmpty(cfg.User) || string.IsNullOrEmpty(cfg.Password))
            {
                Log.Write("尚未配置账号密码, 请先双击本程序完成设置");
                return 1;
            }

            // 1. 等网络: 45 秒内持续帮连(无线慢会自动补试); 到点还不行就退出(不反复试)
            Log.Write("等待网络(45 秒内尝试帮连校园 WiFi)…");
            if (!Net.EnsureNetwork(cfg, 45))
            {
                Log.Write("45 秒内没连上，程序退出，这次不再开");
                return 1;
            }

            // 2. 如果当前不是校园网(家里 WiFi / 其它网络) → 不做校园网认证, 直接退出
            if (!Net.IsCampusNetwork())
            {
                Log.Write("当前网络不是校园网，无需认证，程序退出");
                return 0;
            }

            // 3. 登录: 最多 3 次
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                LoginResult r = Net.PortalLogin(cfg);
                Log.Write((r.Ok ? "连接成功" : "连接失败") + "(" + attempt + "/3): " + r.Message);
                if (r.Ok) return 0;
                if (attempt < 3) Thread.Sleep(2000);
            }
            Log.Write("认证未成功，程序退出，这次不再开");
            return 1;
        }
    }

    // ---------------------------------------------------------------- 使用说明
    class HelpForm : Form
    {
        public HelpForm()
        {
            Text = "使用说明";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(660, 580);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            Icon = AppIcon.Get();

            RichTextBox box = new RichTextBox();
            box.ReadOnly = true;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.ScrollBars = RichTextBoxScrollBars.Vertical;
            box.Bounds = new Rectangle(18, 14, ClientSize.Width - 36, ClientSize.Height - 82);
            box.Font = new Font("Microsoft YaHei UI", 9.5F);
            box.ForeColor = Color.FromArgb(45, 45, 45);
            box.Text = HelpText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            Controls.Add(box);

            Button btn = new Button();
            btn.Text = "知道了";
            btn.Bounds = new Rectangle(ClientSize.Width - 140, ClientSize.Height - 52, 120, 34);
            btn.Click += delegate { Close(); };
            Controls.Add(btn);
        }

        public const string HelpText = @"
校园网自动登录 —— 使用说明
==================================================

【一、这个工具是干什么的】
开机或掉线时自动登录校园网，不用每次打开网页手动输账号密码。
工作流程：
  开机 → 检测网络
       · 有网：是校园网就认证；已经在线就不动；是家里等其它网络就直接退出
       · 没网：主动帮连一次校园 WiFi（默认 ECJTU-Stu）→ 连上后认证
       · 连不上：最多尝试约 45 秒就退出，这次不再开（不会隔一段时间反复试）
所有文件都在 D 盘本文件夹里；不写注册表；密码用 Windows 加密保存。
程序很轻：后台常驻约 6~7MB 内存、最低优先级、CPU 几乎为 0。

【二、界面各设置的作用 与 可能情况】
1. 启用自动连接（总开关）
   勾选：开机 / 掉线时自动连校园网。
   取消并保存：立即停止后台守护，开机也不运行（不在学校时用）。
   程序只在识别到校园网时才认证；连的是家里等其它网络时不会去认证。
2. 登录账号
   一般就是学号。填错会认证失败；后缀不用自己加（按「运营商」自动加）。
3. 登录密码
   改校园网密码后要在这里同步改；只在本机、本 Windows 用户下有效，
   换电脑或换 Windows 用户需要重新输入。
4. 运营商
   移动 @cmcc / 联通 @unicom / 电信 @telecom。选错会认证失败。
5. 开机未连网时自动连接 WiFi
   优先级：有网线 → 只等有线（不会去连 WiFi，避免两台设备）；
   没网线 → 不管 Windows 有没有开自动连接，程序都直接帮连校园 WiFi
   （默认 ECJTU-Stu，前 30 秒试 4 次）；没保存过这个网络也会自动创建配置。
   连上后如果是校园网就自动认证；连不上就退出、交回给 Windows 自己处理。
   注意：如果 WiFi 开关本身是关的（Windows 里关了 WiFi / 开了飞行模式），
   任何程序都连不上，日志会提示你手动打开 WiFi 开关。
   不勾选则不会主动连。
6. 开机自启动
   登录 Windows 后自动运行。会在 C 盘「启动文件夹」放一个快捷方式
   （全机唯一不在 D 盘的东西）；取消勾选立即删除；挪动过程序文件夹要重新保存一次。
7. 掉线自动重连（后台守护）
   右下角托盘常驻；插拔网线/有线无线切换会立即检查（每 5 秒兜底一次）。
   运行中掉线 → 一直自动重连，最多试 5 分钟（十几次）才放弃；
   开机一开始就连不上 → 只试 2~3 次就退出，不赖在后台。
   约 6~7MB 内存、最低优先级；插拔网线 / WiFi 重连 / 休眠唤醒后自动恢复。
   不勾选则是连上一次就退出，不常驻。

【三、常见使用场景】
· 在学校：勾选「启用自动连接」+「掉线自动重连」，开机自动联网，掉线自动恢复。
· 不在学校 / 在家里：建议把「启用自动连接」取消勾选并保存，把这个功能关掉。
  Windows 会自己连家里的 WiFi，程序认出不是校园网 → 不做认证，直接退出。
  （就算忘了关也不要紧：连不上校园网最多试约 45 秒就自己退出，不会一直在后台重试。）
· 有网线就自动优先用有线（认证有线）；没网线就自动转 WiFi 连接（认证无线）。
· 插网线后 WiFi 自动断开：这是 Windows / 网卡驱动的正常行为
  （有些电脑设了「有线连接时自动关闭无线」），程序会用有线顶上认证，不影响上网。
· 装了 VPN（如 Radmin VPN）：程序会自动排除虚拟网卡，不会拿 VPN 的地址去认证。
· 想彻底删除：双击 卸载.bat，它会先结束后台程序，再删干净整个文件夹。

【四、为什么会被算成「两台设备」】
电脑的有线网卡和无线网卡是两块不同硬件，各有一个 MAC 地址；校园网按 MAC 认设备。
所以插着网线、同时又连着 WiFi 时，账号下就会显示「两台设备 / 占用两个名额」。
程序一次只登录当前主用的那张网卡，旧会话不会自己消失，
要等它超时，或者你去校园网自助页手动注销那台旧设备。
避免办法：固定只用一种连接；或切换前先去自助页把旧设备注销。

【五、常见问题速查】
· 「已经在线，无需登录」→ 正常，网络本来就通，不用再认证。
· 「网卡尚未获取到 IP」→ 刚连上 WiFi 时 DHCP 还没分到地址，属正常；
  程序会自动等最多 8 秒，拿到 IP 后马上认证。
· 认证失败 → 看日志里的 [RetCode=xx] 就知道原因：
    1 账号或密码不对      2 终端IP已经在线      7 认证失败
    512 已在线 / 账号状态页（可能已在别的设备登录）
  对照检查账号 / 密码 / 运营商。
· 「账号已在其它设备在线」→ 去校园网自助页注销旧设备，或稍等超时。
· WiFi 连不上 → 先确认 WiFi 开关是开的（没开飞行模式），再看日志。
· 自助页显示「两台设备在线」→ 有线+无线两个 MAC，属正常，见第四条。
· 杀毒软件 / 系统弹拦截 → 加白名单（程序没有数字签名，属误报）。
· 开机连不到网（不在学校）→ 试 2~3 次（约 45 秒）就自动退出；
  运行中掉线 → 一直自动重连，最多 5 分钟（十几次）才放弃。
· 程序会不会拖慢电脑 → 不会：约 6~7MB、最低优先级、CPU 几乎为 0。
· 日志 → 只保留最近 30 天，更早的自动清理。
· 卸载 → 双击 卸载.bat，先结束后台程序，再删程序 + 配置 + 日志 + 图标 + 开机自启 + 整个文件夹。
";
    }

    // ---------------------------------------------------------------- 设置界面
    class SettingsForm : Form
    {
        public Config Cfg;
        TextBox txtUser, txtPass, txtSsid;
        ComboBox cbIsp;
        CheckBox chkWifi, chkAutoStart, chkWatchdog, chkEnabled, chkShowPass;
        Label lblStatus;
        readonly ToolTip toolTip = new ToolTip();

        readonly string[] IspTexts = { "中国移动 (@cmcc)", "中国联通 (@unicom)", "中国电信 (@telecom)", "不选择运营商" };
        readonly string[] IspValues = { "@cmcc", "@unicom", "@telecom", "" };

        public SettingsForm(Config cfg)
        {
            Cfg = cfg;
            BuildUi();
            LoadToUi();
            RefreshStatusAsync();
        }

        void BuildUi()
        {
            Text = "校园网自动登录";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(470, 452);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = AppIcon.Get();

            toolTip.AutoPopDelay = 20000;
            toolTip.InitialDelay = 150;
            toolTip.ReshowDelay = 80;
            toolTip.ShowAlways = true;

            int x = 20, y = 18, wLabel = 78, wInput = 340, qx = 445;

            chkEnabled = new CheckBox();
            chkEnabled.Text = "启用自动连接（不在学校时可取消勾选，程序就不运行了）";
            chkEnabled.Bounds = new Rectangle(x, y, 428, 24);
            Controls.Add(chkEnabled);
            AddQuestion(qx, y + 2, "勾选：开机 / 掉线时自动连接校园网。\r\n取消勾选并保存：立即停止后台守护，开机也不会运行（适合不在学校时使用）。");
            y += 36;

            AddLabel("登录账号", x, y + 3);
            txtUser = AddText(x + wLabel, y, wInput);
            AddQuestion(qx, y + 3, "登录账号，一般就是你的学号。\r\n运营商后缀不用自己加，程序会按下面选的运营商自动拼上。");
            y += 38;

            AddLabel("登录密码", x, y + 3);
            txtPass = AddText(x + wLabel, y, 236);
            txtPass.UseSystemPasswordChar = true;
            chkShowPass = new CheckBox();
            chkShowPass.Text = "显示密码";
            chkShowPass.Bounds = new Rectangle(x + wLabel + 242, y + 2, 96, 22);
            chkShowPass.CheckedChanged += delegate { txtPass.UseSystemPasswordChar = !chkShowPass.Checked; };
            Controls.Add(chkShowPass);
            AddQuestion(qx, y + 3, "校园网账号的密码。\r\n勾选右侧「显示密码」可以明文查看输入内容。\r\n保存时用 Windows DPAPI 加密，只有本机、你这个 Windows 用户能解密，\r\n文件就在本文件夹的 ECJTU-Net.ini 里。");
            y += 38;

            AddLabel("运 营 商", x, y + 3);
            cbIsp = new ComboBox();
            cbIsp.DropDownStyle = ComboBoxStyle.DropDownList;
            cbIsp.Bounds = new Rectangle(x + wLabel, y, wInput, 26);
            cbIsp.Items.AddRange(IspTexts);
            Controls.Add(cbIsp);
            AddQuestion(qx, y + 3, "登录时账号要带的后缀。学校要求选对应运营商（移动/联通/电信）才能认证，\r\n程序会自动把后缀拼到账号后面，例如 2024000000000000@cmcc。");
            y += 44;

            chkWifi = new CheckBox();
            chkWifi.Text = "开机未连网时自动连接 WiFi：";
            chkWifi.Bounds = new Rectangle(x, y, 200, 24);
            Controls.Add(chkWifi);
            txtSsid = AddText(x + 202, y, 158);
            AddQuestion(qx, y + 2, "开机时如果没网，程序会直接主动帮连这个 WiFi（默认 ECJTU-Stu）。\r\n连不上就退回交给 Windows 自己处理，不会反复折腾。");
            y += 40;

            chkAutoStart = new CheckBox();
            chkAutoStart.Text = "开机自启动（登录 Windows 后自动联网）";
            chkAutoStart.Bounds = new Rectangle(x, y, 415, 24);
            Controls.Add(chkAutoStart);
            AddQuestion(qx, y + 2, "勾选后，程序会在 Windows 启动文件夹创建一个快捷方式，\r\n登录 Windows 后自动联网；取消勾选会自动删掉它。\r\n不写注册表、不需要管理员权限。");
            y += 30;

            chkWatchdog = new CheckBox();
            chkWatchdog.Text = "掉线自动重连（后台守护，插拔网线/休眠唤醒后自动恢复）";
            chkWatchdog.Bounds = new Rectangle(x, y, 415, 24);
            Controls.Add(chkWatchdog);
            AddQuestion(qx, y + 2, "勾选后程序会在后台常驻（右下角托盘图标）。\r\n插拔网线、有线无线切换会立即检查（每 5 秒兜底），\r\nWiFi 重连、休眠唤醒导致掉线时自动重新登录。\r\n取消勾选则是连上一次后就退出（不常驻）。");
            y += 34;

            Button btnSave = new Button();
            btnSave.Text = "保存设置";
            btnSave.Bounds = new Rectangle(x, y, 105, 34);
            btnSave.Click += OnSave;
            Controls.Add(btnSave);

            Button btnTest = new Button();
            btnTest.Text = "立即连接";
            btnTest.Bounds = new Rectangle(x + 110, y, 105, 34);
            btnTest.Click += OnTest;
            Controls.Add(btnTest);

            Button btnLog = new Button();
            btnLog.Text = "查看日志";
            btnLog.Bounds = new Rectangle(x + 220, y, 100, 34);
            btnLog.Click += delegate { OpenLog(); };
            Controls.Add(btnLog);

            Button btnHelp = new Button();
            btnHelp.Text = "使用说明";
            btnHelp.Bounds = new Rectangle(x + 325, y, 105, 34);
            btnHelp.Click += delegate { new HelpForm().ShowDialog(this); };
            Controls.Add(btnHelp);

            y += 46;
            lblStatus = new Label();
            lblStatus.Bounds = new Rectangle(x, y, ClientSize.Width - 40, 44);
            lblStatus.Text = "就绪";
            lblStatus.ForeColor = Color.DimGray;
            Controls.Add(lblStatus);

            string tip = "本工具：开机或断线时自动连接校园网。\r\n每项功能的作用和「可能出现的状况」，点【使用说明】查看。";
            Label lblTip = new Label();
            lblTip.Bounds = new Rectangle(x, ClientSize.Height - 54, ClientSize.Width - 40, 46);
            lblTip.Text = tip;
            lblTip.ForeColor = Color.Gray;
            Controls.Add(lblTip);
        }

        void AddLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Bounds = new Rectangle(x, y, 78, 24);
            l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
        }

        TextBox AddText(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Bounds = new Rectangle(x, y, w, 26);
            Controls.Add(t);
            return t;
        }

        Label AddQuestion(int x, int y, string explain)
        {
            Label q = new Label();
            q.Text = "?";
            q.Bounds = new Rectangle(x, y, 18, 20);
            q.TextAlign = ContentAlignment.MiddleCenter;
            q.ForeColor = Color.FromArgb(30, 110, 200);
            q.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            q.Cursor = Cursors.Hand;
            Controls.Add(q);
            toolTip.SetToolTip(q, explain);
            return q;
        }

        void LoadToUi()
        {
            txtUser.Text = Cfg.User;
            txtPass.Text = Cfg.Password;
            txtSsid.Text = Cfg.Ssid;
            chkWifi.Checked = Cfg.AutoWifi;
            chkAutoStart.Checked = AutoStart.IsEnabled();
            chkWatchdog.Checked = Cfg.Watchdog;
            chkEnabled.Checked = Cfg.Enabled;
            int idx = Array.IndexOf(IspValues, Cfg.Isp ?? "");
            cbIsp.SelectedIndex = idx < 0 ? 0 : idx;
        }

        bool ReadFromUi()
        {
            string user = (txtUser.Text ?? "").Trim();
            if (user.Length == 0)
            {
                MessageBox.Show(this, "请填写登录账号", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtUser.Focus();
                return false;
            }
            if ((txtPass.Text ?? "").Length == 0)
            {
                MessageBox.Show(this, "请填写登录密码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPass.Focus();
                return false;
            }
            Cfg.User = user;
            Cfg.Password = txtPass.Text;
            Cfg.Isp = IspValues[cbIsp.SelectedIndex];
            Cfg.Ssid = (txtSsid.Text ?? "").Trim();
            Cfg.AutoWifi = chkWifi.Checked;
            Cfg.AutoStart = chkAutoStart.Checked;
            Cfg.Watchdog = chkWatchdog.Checked;
            Cfg.Enabled = chkEnabled.Checked;
            return true;
        }

        void SetStatus(string text, Color color)
        {
            if (lblStatus != null)
            {
                lblStatus.Text = text;
                lblStatus.ForeColor = color;
            }
        }

        void OnSave(object sender, EventArgs e)
        {
            if (!ReadFromUi()) return;
            try
            {
                Cfg.Save();
                if (Cfg.AutoStart) AutoStart.Enable(); else AutoStart.Disable();
                WatchdogControl.ApplyAfterSave(Cfg.Enabled, Cfg.Watchdog);
                MessageBox.Show(this, "设置已保存到：\r\n" + Config.FilePath, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("设置已保存 " + DateTime.Now.ToString("HH:mm:ss"), Color.Green);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnTest(object sender, EventArgs e)
        {
            if (!ReadFromUi()) return;
            try
            {
                Cfg.Save();
                if (Cfg.AutoStart) AutoStart.Enable(); else AutoStart.Disable();
                WatchdogControl.ApplyAfterSave(Cfg.Enabled, Cfg.Watchdog);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetStatus("正在连接…", Color.DimGray);
            Application.DoEvents();

            Thread t = new Thread(delegate()
            {
                string msg;
                Color color;
                try
                {
                    LoginResult r = Net.PortalLogin(Cfg);
                    msg = r.Message;
                    color = r.Ok ? Color.Green : Color.Firebrick;
                    Log.Write("手动测试: " + r.Message);
                }
                catch (Exception ex)
                {
                    msg = "测试异常：" + ex.Message;
                    color = Color.Firebrick;
                    Log.Write(msg);
                }
                try { BeginInvoke((Action)delegate { SetStatus(msg, color); }); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        public static void OpenLog()
        {
            try
            {
                if (!File.Exists(Log.FilePath)) File.WriteAllText(Log.FilePath, "", Encoding.UTF8);
                Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
            }
            catch (Exception ex) { MessageBox.Show("无法打开日志：" + ex.Message); }
        }

        void RefreshStatusAsync()
        {
            Thread t = new Thread(delegate()
            {
                string text;
                try
                {
                    ProbeResult p = Net.Probe(5000);
                    text = p.Online ? "当前状态：已联网" : ("当前状态：未认证 / 未联网（" + p.Detail + "）");
                }
                catch (Exception ex) { text = "状态检测失败：" + ex.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        SetStatus(text, text.Contains("已联网") ? Color.Green : Color.Firebrick);
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    // ------------------------------------------------------------ 掉线守护(托盘常驻)
    class TrayContext : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly Form hidden;
        EventWaitHandle stopEvent;
        ToolStripMenuItem statusItem;
        int fails;
        int exited;

        public TrayContext()
        {
            hidden = new Form();
            hidden.ShowInTaskbar = false;
            hidden.WindowState = FormWindowState.Minimized;
            hidden.FormBorderStyle = FormBorderStyle.None;
            hidden.Opacity = 0;
            hidden.Size = new Size(1, 1);
            hidden.Show();
            hidden.Hide();

            ContextMenuStrip menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("状态：启动中…");
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("立即重连", null, delegate { Reconnect(); }));
            menu.Items.Add(new ToolStripMenuItem("打开设置", null, delegate
            {
                SettingsForm f = new SettingsForm(Config.Load());
                f.Show();
                f.BringToFront();
            }));
            menu.Items.Add(new ToolStripMenuItem("查看日志", null, delegate { SettingsForm.OpenLog(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出守护", null, delegate { ExitApp(); }));

            icon = new NotifyIcon();
            icon.Icon = AppIcon.Get();
            icon.Text = "校园网自动登录";
            icon.Visible = true;
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate
            {
                SettingsForm f = new SettingsForm(Config.Load());
                f.Show();
                f.BringToFront();
            };

            try
            {
                stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\ECJTU-Net-WatchdogStop");
            }
            catch { }

            try
            {
                // 注销/关机时立刻自己退出, 不会拖慢关机、不会弹"程序阻止关机"
                Microsoft.Win32.SystemEvents.SessionEnding += delegate
                {
                    Log.Write("系统正在注销/关机, 守护退出");
                    try { hidden.BeginInvoke((Action)delegate { ExitApp(); }); }
                    catch { try { ExitApp(); } catch { } }
                };
            }
            catch { }

            try
            {
                // 网络变化(插拔网线/有线无线切换/IP变化) → 立即唤醒守护检查, 不用等轮询
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            }
            catch { }

            Thread loopThread = new Thread(Loop);
            loopThread.IsBackground = true;
            loopThread.Start();
        }

        DateTime lastNetWake = DateTime.MinValue;
        void OnNetworkChanged(object sender, EventArgs e)
        {
            try
            {
                // 防抖: 2 秒内最多唤醒一次, 避免切换瞬间反复触发
                if ((DateTime.Now - lastNetWake).TotalSeconds < 2) return;
                lastNetWake = DateTime.Now;
                Log.Write("检测到网络变化(插拔/切换), 立即唤醒检查…");
                if (stopEvent != null) stopEvent.Set();
            }
            catch { }
        }

        void ApplyStatus(string text)
        {
            try
            {
                if (statusItem != null) statusItem.Text = "状态：" + text;
                string tip = "校园网自动登录 - " + text;
                if (tip.Length > 63) tip = tip.Substring(0, 63);
                icon.Text = tip;
            }
            catch { }
        }

        void SetStatus(string text)
        {
            try
            {
                if (hidden != null && hidden.IsHandleCreated)
                    hidden.BeginInvoke((Action)delegate { ApplyStatus(text); });
            }
            catch { }
        }

        // 守护主循环: 启动后立即检查一次, 之后网络变化立即唤醒 + 每 5 秒兜底;
        // 开机阶段连不上: 只试 2~3 次就退出; 运行中掉线: 一直尝试, 最多 5 分钟/十几次
        void Loop()
        {
            bool first = true;
            bool wasEverOnline = false;
            DateTime failStart = DateTime.MinValue;
            while (true)
            {
                if (!first)
                {
                    try { stopEvent.WaitOne(5000); }
                    catch { break; }
                }
                bool isBoot = first;
                first = false;

                Config cfg = Config.Load();   // 每次读最新设置, 保存后立即生效
                if (!cfg.Enabled || !cfg.Watchdog)
                {
                    Log.Write(cfg.Enabled ? "掉线守护已在设置中关闭, 退出守护" : "自动连接已关闭, 退出守护");
                    break;
                }

                try
                {
                    if (isBoot)
                    {
                        Log.Write("等待网络(45 秒内尝试帮连校园 WiFi)…");
                        if (!Net.EnsureNetwork(cfg, 45))
                        {
                            Log.Write("45 秒内没连上（可能不在学校），守护退出，这次不再开");
                            break;
                        }
                    }

                    // 有网但连的不是校园网(家里 WiFi / 其它网络) → 不认证, 退出;
                    // 没网(掉线) → 不退出, 交给 OneCycle 直接重连(不等 Windows 自动连接)
                    if (Net.HasAnyNetwork() && !Net.IsCampusNetwork())
                    {
                        Log.Write("当前网络不是校园网，无需认证，守护退出");
                        break;
                    }

                    if (OneCycle(cfg))
                    {
                        if (fails > 0) { Log.Write("网络已恢复在线"); }
                        fails = 0;
                        wasEverOnline = true;
                        failStart = DateTime.MinValue;
                    }
                    else
                    {
                        fails++;
                        if (failStart == DateTime.MinValue) failStart = DateTime.Now;
                        if (!wasEverOnline)
                        {
                            // 开机阶段: 试 2~3 次就停, 不赖在后台
                            Log.Write("开机阶段未连上 (" + fails + "/3)");
                            if (fails >= 3)
                            {
                                Log.Write("开机连不上, 已试 3 次, 退出(不再重试)");
                                break;
                            }
                        }
                        else
                        {
                            // 运行中掉线: 一直尝试重连, 最多 5 分钟 / 十几次
                            double mins = (DateTime.Now - failStart).TotalMinutes;
                            Log.Write("掉线重连中 (" + fails + " 次, 约 " + (int)mins + " 分钟)…");
                            if (fails >= 15 || mins >= 5)
                            {
                                Log.Write("已尝试 " + fails + " 次 / 5 分钟, 停止尝试, 守护退出");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("守护异常: " + ex.Message);
                    SetStatus("异常");
                }
            }

            try { hidden.BeginInvoke((Action)delegate { ExitApp(); }); }
            catch { try { ExitApp(); } catch { } }
        }

        // 执行一次检查+认证; 返回 true 表示已在线
        bool OneCycle(Config cfg)
        {
            if (!Net.HasAnyNetwork())
            {
                // 使用过程中掉网: 不等 Windows, 直接帮连校园 WiFi(前 30 秒 4 次);
                // 连不上退回等下一轮(守护最多坚持 5 分钟)
                if (!Net.EnsureNetwork(cfg, 25))
                {
                    SetStatus("等待网络");
                    return false;
                }
            }

            ProbeResult p = Net.Probe(5000);
            if (p.Online)
            {
                SetStatus("已联网");
                return true;
            }

            // 每轮 3 次认证
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                SetStatus("正在认证…");
                LoginResult r = Net.PortalLogin(cfg);
                if (r.Ok)
                {
                    Log.Write("自动登录成功: " + r.Message);
                    SetStatus("已联网");
                    return true;
                }
                Log.Write("自动登录失败(" + attempt + "/3): " + r.Message);
                if (attempt < 3) Thread.Sleep(3000);
            }
            SetStatus("认证失败");
            return false;
        }

        void Reconnect()
        {
            Log.Write("---- 手动重连(立即唤醒守护检查) ----");
            try { if (stopEvent != null) stopEvent.Set(); } catch { }
        }

        void ExitApp()
        {
            if (Interlocked.Exchange(ref exited, 1) != 0) return;
            try { icon.Visible = false; icon.Dispose(); } catch { }
            try { hidden.Dispose(); } catch { }
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ 入口
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                string msg = "程序启动失败：" + ex.GetType().Name + " - " + ex.Message;
                try { Log.Write(msg + Environment.NewLine + ex.StackTrace); } catch { }
                try
                {
                    MessageBox.Show(msg + Environment.NewLine + Environment.NewLine +
                        "数据目录：" + Paths.DataDir, "校园网自动登录",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
        }

        static int Run(string[] args)
        {
            bool silent = false;
            bool configCmd = false;
            bool helpCmd = false;
            string cfgUser = null, cfgPass = null, cfgIsp = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] ?? "").ToLowerInvariant();
                if (a == "/silent" || a == "-silent" || a == "/login" || a == "-login") silent = true;
                else if (a == "/config") configCmd = true;
                else if (a == "/help" || a == "-help") helpCmd = true;
                else if (a.StartsWith("/user:")) cfgUser = args[i].Substring(6);
                else if (a.StartsWith("/pass:")) cfgPass = args[i].Substring(6);
                else if (a.StartsWith("/isp:")) cfgIsp = args[i].Substring(5);
            }

            if (configCmd)
            {
                Config c = Config.Load();
                if (!string.IsNullOrEmpty(cfgUser)) c.User = cfgUser;
                if (!string.IsNullOrEmpty(cfgPass)) c.Password = cfgPass;
                if (cfgIsp != null) c.Isp = cfgIsp;
                c.Save();
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Config cfg = Config.Load();

            if (helpCmd)
            {
                Application.Run(new HelpForm());
                return 0;
            }

            if (silent)
            {
                if (!cfg.Enabled)
                {
                    Log.Write("自动连接已关闭（未勾选「启用自动连接」），本次不运行");
                    return 0;
                }
                if (cfg.Watchdog)
                {
                    // 掉线守护: 常驻托盘, 单实例
                    using (Mutex mtx = new Mutex(false, "Local\\ECJTU-Net-Watchdog"))
                    {
                        bool owned;
                        try { owned = mtx.WaitOne(0); }
                        catch (AbandonedMutexException) { owned = true; }
                        if (!owned) return 0;   // 已经有一个守护在运行
                        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
                        Log.Write("==================== 启动掉线守护 (最低优先级 " + Process.GetCurrentProcess().PriorityClass + ") ====================");
                        Application.Run(new TrayContext());
                        try { mtx.ReleaseMutex(); } catch { }
                        return 0;
                    }
                }
                return Silent.Run(cfg);
            }

            Application.Run(new SettingsForm(cfg));
            return 0;
        }
    }
}
