/* Navicat 18 激活工具（GUI，C# / .NET Framework 4.x）
 *
 * 一个程序搞定两件事：
 *   ① 安装/卸载补丁（给 libcf.dll 就地打补丁 + 替换 navicat.exe 的请求码公钥）
 *   ② 生成激活码（粘贴请求码 + 填邮箱/公司 → 输出激活码）
 *
 * 编译（Windows 自带编译器）：
 *   csc /target:winexe /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll ^
 *       /out:Navicat18Tool.exe Navicat18Tool.cs pdata.cs keys.cs
 */
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    /// <summary>当前进程是否以管理员身份运行</summary>
    private static bool IsAdmin()
    {
        try
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>以管理员身份重新启动自己；成功返回 true（调用方应立刻退出）</summary>
    private static bool TryElevate(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Application.StartupPath
            };
            string extra = "--elevated";
            if (args != null && args.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (string a in args) sb.Append(a).Append(' ');
                sb.Append(extra);
                psi.Arguments = sb.ToString();
            }
            else psi.Arguments = extra;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // ── 管理员检查：不是管理员就自动以管理员身份重启自己 ──
        // 原因：打补丁要写 C:\Program Files\Navicat\... 下的 libcf.dll / navicat.exe，
        //       普通权限会失败。自检入口不需要提权，跳过以免反复弹 UAC。
        bool selftest = args != null && args.Length >= 1 &&
            (args[0] == "--uishot" || args[0] == "--lic" ||
             args[0] == "--devid" || args[0] == "--vfyfile" ||
              args[0] == "--winscan" || args[0] == "--dlgtest" ||
              args[0] == "--mediumtest" || args[0] == "--offline");
        bool tried = args != null && Array.IndexOf(args, "--elevated") >= 0;   // 防无限重启
        if (!selftest && !tried && !IsAdmin())
        {
            if (TryElevate(args)) { Environment.Exit(0); }
        }

        // 隐藏自检入口：验证本地验签实现。
        // （之所以留这个入口：破解者常用的"反射加载后调内部方法"已被 Obf.Hijack 拦掉，
        //   所以自动化回归需要一个"自己人"的出口；不影响正常启动。）
        //   用法: Navicat18Tool.exe --vfy "<code>|<device>|<expire>|<sig>"  -> %TEMP%\nvfy.txt
        // 隐藏自检：把界面渲染成 PNG（供手册/文档使用）。
        //   用法: Navicat18Tool.exe --uishot [main|manual|wait]
        //   输出 %TEMP%\nvui-<名字>.png 与 nvui-<名字>-screen.png + nvui.txt
        //   ★ 优先用 DrawToBitmap 内存渲染：屏幕抓图会被桌面上其它窗口遮挡
        //     （实测抓到过完全不相干的窗口），不能用来做交付截图。
        if (args != null && args.Length >= 1 && args[0] == "--uishot")
        {
            string which = args.Length >= 2 ? args[1].ToLower() : "main";
            try
            {
                Form f;
                if (which == "manual") f = new ManualActForm("user@example.com", "示例科技有限公司");
                else if (which == "wait") f = new WaitForm("正在激活…", "正在识别 Navicat 界面，请稍候…");
                else { which = "main"; f = Guard.CreateMainForm(); }
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(80, 80);
                f.Show();
                for (int i = 0; i < 30; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                try { f.Activate(); Application.DoEvents(); System.Threading.Thread.Sleep(150); } catch { }
                var r = f.Bounds;
                string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nvui-" + which + ".png");
                using (var bmp = new System.Drawing.Bitmap(r.Width, r.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, r.Width, r.Height));
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                // 对照用的屏幕抓图（被遮挡时内容不可信，仅作参考）
                string png2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nvui-" + which + "-screen.png");
                try
                {
                    using (var bmp2 = new System.Drawing.Bitmap(r.Width, r.Height))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp2))
                            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(r.Width, r.Height));
                        bmp2.Save(png2, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch { png2 = "(screen grab failed)"; }
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nvui.txt"),
                    which + " " + r.Width + "x" + r.Height + " | mem=" + png + " | screen=" + png2);
                f.Close();
            }
            catch (Exception ex)
            {
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nvui.txt"), "ERR " + ex.GetType().Name + " " + ex.Message); } catch { }
            }
            return;
        }
        // 隐藏自检：实测降权启动（借 explorer 令牌）。输出 %TEMP%\nvmedium.txt
        if (args != null && args.Length >= 1 && args[0] == "--mediumtest")
        {
            string mp = Path.Combine(Path.GetTempPath(), "nvmedium.txt");
            var msb = new StringBuilder();
            try
            {
                string exe = args.Length >= 2 ? args[1] : @"D:\work\逆向\navicat_lab18\navicat.exe";
                Action<string> ML = delegate(string m) { msb.AppendLine(m); };
                msb.AppendLine("--- 本机 explorer 进程及其权限 ---");
                foreach (System.Diagnostics.Process ep in System.Diagnostics.Process.GetProcessesByName("explorer"))
                    msb.AppendLine("explorer pid=" + ep.Id + "  权限=" + AutoAct.IntegrityOf((uint)ep.Id));
                msb.AppendLine("--- 当前进程权限 ---");
                msb.AppendLine("self 权限=" + AutoAct.IntegrityOf((uint)System.Diagnostics.Process.GetCurrentProcess().Id));
                msb.AppendLine("--- 尝试降权启动 ---");
                bool ok = AutoAct.StartAsMediumUser(exe, System.IO.Path.GetDirectoryName(exe), ML);
                msb.AppendLine("StartAsMediumUser=" + ok);
                System.Threading.Thread.Sleep(7000);
                uint mpid = AutoAct.FirstNavicatPid();
                msb.AppendLine("navicat pid=" + mpid + "  权限=" + AutoAct.IntegrityOf(mpid));
            }
            catch (Exception ex) { msb.AppendLine("ERR " + ex.GetType().Name + " " + ex.Message); }
            File.WriteAllText(mp, msb.ToString());
            return;
        }
        // 隐藏自检：实测「试用提醒」处理（会真的点按钮）。输出 %TEMP%\nvdlg.txt
        if (args != null && args.Length >= 1 && args[0] == "--dlgtest")
        {
            string dp = Path.Combine(Path.GetTempPath(), "nvdlg.txt");
            try { File.WriteAllText(dp, AutoAct.DlgTest(AutoAct.FirstNavicatPid())); }
            catch (Exception ex) { File.WriteAllText(dp, "ERR " + ex.GetType().Name + " " + ex.Message); }
            return;
        }
        // 隐藏自检：只读扫描 Navicat 的顶层窗口/按钮，并说明哪些会被"启动提示框"判据命中。
        // 用途：验证"首次运行提示框"自动处理逻辑不会误伤 Navicat 自己的子窗口。
        if (args != null && args.Length >= 1 && args[0] == "--winscan")
        {
            string wp = Path.Combine(Path.GetTempPath(), "nvwin.txt");
            try { File.WriteAllText(wp, AutoAct.ScanWindows(AutoAct.FirstNavicatPid())); }
            catch (Exception ex) { File.WriteAllText(wp, "ERR " + ex.GetType().Name + " " + ex.Message); }
            return;
        }
        // 隐藏自检：离线签发验证。
        //   用法: Navicat18Tool.exe --offline <请求码文件> [email] [company] -> %TEMP%\nvoff.txt
        //   输出 OFF_OK|<激活码> 或 OFF_FAIL|<原因>；
        //   把激活码交给独立实现的验证脚本（python 用公钥还原 EM 块）即可证明正确性。
        if (args != null && args.Length >= 2 && args[0] == "--offline")
        {
            string op = Path.Combine(Path.GetTempPath(), "nvoff.txt");
            try
            {
                string reqText = File.ReadAllText(args[1], Encoding.UTF8);
                string em = args.Length >= 3 ? args[2] : "";
                string co = args.Length >= 4 ? args[3] : "";
                string m;
                string code = OfflineSign.Make(reqText, em, co, out m);
                File.WriteAllText(op, string.IsNullOrEmpty(code)
                    ? "OFF_FAIL|" + m
                    : "OFF_OK|" + code);
            }
            catch (Exception ex) { File.WriteAllText(op, "OFF_ERR " + ex.GetType().Name + " " + ex.Message); }
            return;
        }
        // 隐藏自检：直接报告本地 lic.bin 的读取与验签结果（决定性判据）
        if (args != null && args.Length >= 1 && args[0] == "--lic")
        {
            string path = Path.Combine(Path.GetTempPath(), "nvlic.txt");
            try
            {
                var st = LicenseGate.Load();
                if (st == null) { File.WriteAllText(path, "LOAD_NULL"); }
                else
                {
                    long r = LicenseGate.Remain(st);
                    File.WriteAllText(path, "LOAD_OK|" + st.Code + "|" + st.Device + "|" + st.Expire
                        + "|" + r + "|siglen=" + (st.Sig == null ? 0 : st.Sig.Length));
                }
            }
            catch (Exception ex) { File.WriteAllText(path, "LOAD_ERR " + ex.GetType().Name + " " + ex.Message); }
            return;
        }
        // 隐藏自检：输出本机设备码（供自动化测试构造合法 lic.bin 用，正常使用不受影响）
        if (args != null && args.Length >= 1 && args[0] == "--devid")
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "nvdev.txt"), LicenseGate.DeviceId()); }
            catch { }
            return;
        }
        if (args != null && args.Length >= 2 && args[0] == "--vfyfile")
        {
            string outp = Path.Combine(Path.GetTempPath(), "nvfy.txt");
            try
            {
                // 从文件读 "<code>|<device>|<expire>|<sig>"（走命令行会被 shell 的 | 拆断）
                var q = File.ReadAllText(args[1], Encoding.UTF8).Trim().Split('|');
                bool ok = q.Length >= 4 && LicenseGate.VerifyExpire(q[0], q[1], long.Parse(q[2]), q[3]);
                File.WriteAllText(outp, ok ? "VERIFY_OK" : "VERIFY_FAIL");
            }
            catch (Exception ex) { File.WriteAllText(outp, "VERIFY_ERR " + ex.GetType().Name + " " + ex.Message); }
            return;
        }
        G.Dark();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#if OFFLINE
        // 离线版：不校验卡密、不需要任何登录，直接进入主界面
#else
        if (!LicenseGate.EnsureLicensed()) return;   // ★ 先登录（卡密），通过后才创建主界面
#endif
        Application.Run(Guard.CreateMainForm());
    }
}

/* ---------------- 补丁逻辑 ---------------- */
internal static class PePatcher
{
    private static int Align(int v, int a) { return (v + a - 1) / a * a; }
    private static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    private static ushort U16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }

    private static void WriteAllBytesRetry(string path, byte[] data, Action<string> log)
    {
        for (int i = 1; i <= 5; i++)
        {
            try { File.WriteAllBytes(path, data); return; }
            catch (IOException)
            {
                log("文件仍被占用，重试 " + i + "/5 …");
                System.Threading.Thread.Sleep(1200 * i);
            }
        }
        File.WriteAllBytes(path, data);
    }

    /// <summary>激活方式：
    ///   Libcf      —— 文件级补丁：给 libcf.dll 新增 .pmod 节并替换 navicat.exe 的公钥。
    ///                 缺点：Navicat 官方更新会覆盖这两个文件，补丁随之丢失。
    ///   VersionDll —— 运行时补丁：只在安装目录放一个代理 version.dll，
    ///                 由它在宿主进程内改内存。不修改 Navicat 任何文件，
    ///                 因此官方更新不会移除它（version.dll 不在 KnownDLLs，exe 同目录优先）。</summary>
    public enum PatchMode { Libcf = 0, VersionDll = 1 }

    /// <summary>当前选择的激活方式（由 UI 设置）</summary>
    public static PatchMode Mode = PatchMode.Libcf;

    /// <summary>代理 DLL 在安装目录里的文件名</summary>
    public const string ProxyFileName = "version.dll";

    public static string ProxyPath(string dir) { return Path.Combine(dir, ProxyFileName); }

    /// <summary>libcf.dll 里是否已有 .pmod 节（= 文件级补丁已装）</summary>
    public static bool HasPmod(string libcf)
    {
        try
        {
            if (!File.Exists(libcf)) return false;
            byte[] d = File.ReadAllBytes(libcf);
            int pe = (int)U32(d, 0x3C);
            if (pe <= 0 || pe + 0x18 > d.Length) return false;
            int nsec = U16(d, pe + 6);
            int so = pe + 24 + U16(d, pe + 20);
            for (int s = 0; s < nsec; s++)
            {
                int o = so + s * 40;
                if (o + 8 > d.Length) break;
                if (d[o] == '.' && d[o + 1] == 'p' && d[o + 2] == 'm' && d[o + 3] == 'o' && d[o + 4] == 'd')
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>代理 DLL 是否已是当前这一份（逐字节比对，避免半截/旧版残留）</summary>
    public static bool IsProxyInstalled(string dir)
    {
        try
        {
            string vd = ProxyPath(dir);
            if (!File.Exists(vd)) return false;
            byte[] b = File.ReadAllBytes(vd);
            byte[] want = ProxyData.GetDll();
            if (b.Length != want.Length) return false;
            for (int i = 0; i < b.Length; i++) if (b[i] != want[i]) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>按当前方式判断补丁是否已经装好</summary>
    public static bool IsInstalled(string dir)
    {
        if (Mode == PatchMode.VersionDll) return IsProxyInstalled(dir);
        return VerifyPem(Path.Combine(dir, "navicat.exe")) && HasPmod(Path.Combine(dir, "libcf.dll"));
    }

    /// <summary>结束占用安装目录文件的 Navicat 进程（写文件前必须做）</summary>
    public static int KillNavicat(Action<string> log)
    {
        int killed = 0;
        foreach (var pr in System.Diagnostics.Process.GetProcessesByName("navicat"))
        {
            try { pr.Kill(); pr.WaitForExit(8000); killed++; } catch { }
        }
        if (killed > 0)
        {
            log("已关闭正在运行的 Navicat（" + killed + " 个进程），等待文件解锁…");
            System.Threading.Thread.Sleep(1500);
        }
        return killed;
    }

    /// <summary>安装 version.dll 方式（运行时补丁，不修改 Navicat 的任何文件）</summary>
    public static void InstallProxy(string dir, Action<string> log)
    {
        string exe = Path.Combine(dir, "navicat.exe");
        string libcf = Path.Combine(dir, "libcf.dll");
        if (!File.Exists(exe) || !File.Exists(libcf))
            throw new Exception("该目录下没有 navicat.exe / libcf.dll：" + dir);

        // ★ 与文件级补丁互斥：若之前用的是 libcf 方式，先还原回去。
        //   否则 libcf 里的 .pmod 节会干扰运行时补丁（代理已排除该节，但还原更干净）。
        if (HasPmod(libcf) || VerifyPem(exe))
        {
            log("检测到已安装的 libcf 文件级补丁，先还原 Navicat 原始文件…");
            Uninstall(dir, log);
        }

        KillNavicat(log);

        byte[] dll = ProxyData.GetDll();
        string vd = ProxyPath(dir);
        WriteAllBytesRetry(vd, dll, log);
        log("已释放代理 " + ProxyFileName + "（" + dll.Length + " 字节）");

        // 回读校验：确认写进去的就是这一份（写失败会让补丁完全无效却看不出来）
        if (!IsProxyInstalled(dir))
            throw new Exception("代理 DLL 写入校验失败：" + ProxyFileName + " 内容与预期不一致");

        log("运行方式：不修改 navicat.exe / libcf.dll，由代理在 Navicat 进程内完成补丁。");
        log("优点：Navicat 官方更新不会移除它（更新只替换它自己的文件）。");
    }

    /// <summary>卸载 version.dll 方式</summary>
    public static void UninstallProxy(string dir, Action<string> log)
    {
        string vd = ProxyPath(dir);
        if (!File.Exists(vd)) { log("未发现 " + ProxyFileName + "，跳过"); return; }
        KillNavicat(log);
        // 代理被 Navicat 加载时文件被占用，删不掉就改名为 .removed 待下次清理
        try { File.Delete(vd); log("已删除 " + ProxyFileName); }
        catch
        {
            try
            {
                string t = vd + ".removed";
                if (File.Exists(t)) File.Delete(t);
                File.Move(vd, t);
                log("代理文件被占用，已改名 " + ProxyFileName + ".removed（重启后可手动删除）");
            }
            catch (Exception ex) { log("删除 " + ProxyFileName + " 失败：" + ex.Message); }
        }
    }

    /// <summary>按当前选择的激活方式安装补丁</summary>
    public static void Install(string dir, Action<string> log)
    {
        if (Mode == PatchMode.VersionDll) { InstallProxy(dir, log); return; }
        InstallLibcf(dir, log);
    }

    /// <summary>文件级补丁：给 libcf.dll 加 .pmod 节 + 替换 navicat.exe 的公钥</summary>
    private static void InstallLibcf(string dir, Action<string> log)
    {
        string libcf = Path.Combine(dir, "libcf.dll");
        string exe = Path.Combine(dir, "navicat.exe");
        if (!File.Exists(libcf) || !File.Exists(exe))
            throw new Exception("该目录下没有 navicat.exe / libcf.dll：" + dir);

        // 写 libcf.dll / navicat.exe 前必须先结束占用它们的 Navicat 进程，
        // 否则会报「文件正由另一进程使用」。
        KillNavicat(log);

        foreach (string f in new[] { "libcf.dll", "navicat.exe" })
        {
            string src = Path.Combine(dir, f), bak = src + ".orig";
            if (!File.Exists(bak)) { File.Copy(src, bak); log("已备份 " + f + " -> " + f + ".orig"); }
        }

        byte[] d = File.ReadAllBytes(libcf);
        int pos = FindPrologue(d, PData.GetPrologue());
        if (pos < 0) throw new Exception("版本不匹配：未在 libcf.dll 的可执行节中找到预期函数序言，请把 libcf.dll 提供给作者适配。");
        log("版本校验通过。");

        byte[] patched = Apply(d, pos);
        WriteAllBytesRetry(libcf, patched, log);
        log("已写入补丁版 libcf.dll。");

        byte[] b = File.ReadAllBytes(exe);
        int idx = FindPem(b);
        if (idx < 0) log("警告：navicat.exe 中未找到 PEM 公钥（可能已替换），跳过");
        else
        {
            byte[] pem = PData.GetPem();
            Array.Copy(pem, 0, b, idx, pem.Length);
            WriteAllBytesRetry(exe, b, log);

            // ★ 回读校验：确认公钥真的写进去了（写失败会导致"请求码无法解密"，且报错点很晚才发现）
            bool pemOk = false;
            try
            {
                byte[] chk = File.ReadAllBytes(exe);
                int idx2 = FindPem(chk);
                if (idx2 >= 0)
                {
                    pemOk = true;
                    for (int i = 0; i < pem.Length; i++)
                        if (chk[idx2 + i] != pem[i]) { pemOk = false; break; }
                }
            }
            catch { }
            if (pemOk) log("公钥替换校验通过。");
            else throw new Exception("公钥替换校验失败：navicat.exe 里的请求码公钥没有换成我们的（写入未生效）");
        }

        log("现在可以：启动 Navicat → 帮助 → 注册（密钥 NAVGTPMSYAXVTH7H）→ Ctrl+Shift 点激活 → 用请求码生成激活码。");
    }

    /// <summary>校验 navicat.exe 里的请求码公钥是否仍是我们替换进去的那把。
    /// 用途：Navicat 自身/更新程序可能把文件改回去，那时现象与"没打补丁"完全一样。</summary>
    public static bool VerifyPem(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return false;
            byte[] b = File.ReadAllBytes(exe);
            int idx = FindPem(b);
            if (idx < 0) return false;
            byte[] pem = PData.GetPem();
            if (idx + pem.Length > b.Length) return false;
            for (int i = 0; i < pem.Length; i++)
                if (b[idx + i] != pem[i]) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>卸载补丁（两种方式都清理，避免切换方式后残留互相干扰）</summary>
    public static void Uninstall(string dir, Action<string> log)
    {
        bool any = false;
        foreach (string[] p in new[] { new[] { "libcf.dll", "libcf.dll.orig" }, new[] { "navicat.exe", "navicat.exe.orig" } })
        {
            string dst = Path.Combine(dir, p[0]), bak = Path.Combine(dir, p[1]);
            if (File.Exists(bak)) { File.Copy(bak, dst, true); log("已还原 " + p[0]); any = true; }
            else log("未找到备份 " + p[1] + "，跳过");
        }
        // 运行时方式：删掉代理 DLL
        string vd = ProxyPath(dir);
        if (File.Exists(vd))
        {
            try { File.Delete(vd); log("已删除 " + ProxyFileName); any = true; }
            catch { log("删除 " + ProxyFileName + " 失败（可能正被 Navicat 占用，请关闭后重试）"); }
        }
        string vdOld = Path.Combine(dir, "version.dll.bak");
        if (File.Exists(vdOld)) log("提示：存在 version.dll.bak（早期劫持版残留），可手动删除。");
        if (!any) log("未发现需要卸载的补丁。");
    }

    private static int FindPrologue(byte[] d, byte[] pro)
    {
        int pe = (int)U32(d, 0x3C);
        int nsec = U16(d, pe + 6);
        int opt = pe + 24;
        int so = opt + U16(d, pe + 20);
        for (int s = 0; s < nsec; s++)
        {
            int o = so + s * 40;
            if ((U32(d, o + 36) & 0x20000000u) == 0) continue;
            int rsz = (int)U32(d, o + 16), raw = (int)U32(d, o + 20);
            int end = Math.Min(raw + rsz, d.Length) - pro.Length;
            for (int i = raw; i <= end; i++)
            {
                bool ok = true;
                for (int j = 0; j < pro.Length; j++) if (d[i + j] != pro[j]) { ok = false; break; }
                if (ok) return i;
            }
        }
        return -1;
    }

    private static int FindPem(byte[] d)
    {
        byte[] m = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----");
        for (int i = 0; i <= d.Length - m.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < m.Length; j++) if (d[i + j] != m[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static byte[] Apply(byte[] src, int entryOff)
    {
        int pe = (int)U32(src, 0x3C);
        int nsec = U16(src, pe + 6);
        int opt = pe + 24;
        ulong img = BitConverter.ToUInt64(src, opt + 24);
        int alignS = (int)U32(src, opt + 32);
        int alignF = (int)U32(src, opt + 36);
        int so = opt + U16(src, pe + 20);

        int lastVA = 0, lastVsz = 0;
        int entryVA = 0;
        for (int i = 0; i < nsec; i++)
        {
            int o = so + i * 40;
            int vsz = (int)U32(src, o + 8), va = (int)U32(src, o + 12), rsz = (int)U32(src, o + 16), raw = (int)U32(src, o + 20);
            lastVsz = vsz; lastVA = va;
            if (entryOff >= raw && entryOff < raw + rsz) entryVA = va + (entryOff - raw);
        }

        int newVA = Align(lastVA + lastVsz, alignS);
        byte[] shell = PData.GetShell();
        int newVsz = shell.Length;
        int newRsz = Align(newVsz, alignF);
        int newRaw = Align(src.Length, alignF);

        long jmpVA = (long)img + newVA + 0x5A;
        long back = (long)img + entryVA + 21;
        BitConverter.GetBytes((int)(back - (jmpVA + 5))).CopyTo(shell, 0x5B);

        byte[] outb = new byte[newRaw + newRsz];
        Array.Copy(src, outb, src.Length);

        byte[] hdr = new byte[40];
        Encoding.ASCII.GetBytes(".pmod").CopyTo(hdr, 0);
        BitConverter.GetBytes(newVsz).CopyTo(hdr, 8);
        BitConverter.GetBytes(newVA).CopyTo(hdr, 12);
        BitConverter.GetBytes(newRsz).CopyTo(hdr, 16);
        BitConverter.GetBytes(newRaw).CopyTo(hdr, 20);
        BitConverter.GetBytes((uint)0x60000020).CopyTo(hdr, 36);
        Array.Copy(hdr, 0, outb, so + nsec * 40, 40);
        BitConverter.GetBytes((ushort)(nsec + 1)).CopyTo(outb, pe + 6);
        BitConverter.GetBytes((uint)(newVA + newVsz)).CopyTo(outb, opt + 56);

        Array.Copy(shell, 0, outb, newRaw, newVsz);

        int rel2 = (int)(((long)img + newVA) - ((long)img + entryVA + 5));
        outb[entryOff] = 0xE9;
        BitConverter.GetBytes(rel2).CopyTo(outb, entryOff + 1);
        for (int i = 5; i < 21; i++) outb[entryOff + i] = 0x90;
        return outb;
    }
}

/* ---------------- 激活码生成 ---------------- */
internal static class KeyGen
{
    private static byte[] Rev(byte[] a)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[a.Length - 1 - i];
        return r;
    }

    private static BigInteger Big(byte[] littleEndian)
    {
        var t = new byte[littleEndian.Length + 1];
        Array.Copy(littleEndian, t, littleEndian.Length);
        return new BigInteger(t);
    }

    private static byte[] Fixed(BigInteger v, int size)
    {
        var b = v.ToByteArray();
        var r = new byte[size];
        Array.Copy(b, r, Math.Min(b.Length, size));
        return r;
    }

    private static bool Field(string json, string key, out string val)
    {
        val = null;
        string pat = "\"" + key + "\":\"";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return false;
        i += pat.Length;
        int j = json.IndexOf('"', i);
        if (j < 0) return false;
        val = json.Substring(i, j - i);
        return true;
    }

    /// <summary>
    /// 轻量前置检查：只判断「像不像一段请求码」（base64 / 256 字节）。
    /// 真正的判定（能否用私钥解开、是不是本机 Navicat 的）由服务端完成 ——
    /// 客户端已不再内嵌任何 RSA 私钥。
    /// </summary>
    public static bool LooksLikeRequest(string text, out string kind, out string err)
    {
        kind = ""; err = "";
        string t = (text ?? "").Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
        if (t.Length < 100) { err = "内容太短（当前 " + t.Length + " 字符），可能没复制完整"; return false; }
        byte[] ct;
        try { ct = Convert.FromBase64String(t); }
        catch { err = "不是合法的 base64 文本"; return false; }
        if (ct.Length != 256) { err = "长度 " + ct.Length + " 字节（应为 256）"; return false; }
        kind = "request";
        return true;
    }

    /// <summary>
    /// ★ P0：不再本地签发。把请求码交给服务端，由服务端用私钥生成激活码。
    /// 这样反编译本程序也无法提取私钥、无法自行发卡。
    /// </summary>
    public static string Make(string requestCode, string email, string company, out string info)
    {
        info = "";
        string req = (requestCode ?? "").Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
        if (req.Length < 100)
            throw new Exception("请先粘贴完整的请求码。\r\n请在 Navicat 的手动激活窗口里复制【请求码】那一栏（较长的一栏）。");
        byte[] ct;
        try { ct = Convert.FromBase64String(req); }
        catch { throw new Exception("请求码不是合法的 base64 文本。"); }
        if (ct.Length != 256)
            throw new Exception("请求码长度异常（" + ct.Length + " 字节，应为 256）。");

        string msg;
        // ★ 离线版（/define:OFFLINE）：用内嵌私钥在本地签发，不依赖服务器、不需要卡密。
        //   在线版 OfflineSign.Available 恒为 false，这段等价于不存在（exe 里也没有任何私钥字节）。
        if (OfflineSign.Available)
        {
            string off = OfflineSign.Make(req, email, company, out msg);
            if (!string.IsNullOrEmpty(off)) return off;
            throw new Exception(string.IsNullOrEmpty(msg) ? "离线签发失败。" : msg);
        }

        string code = LicenseGate.RemoteSign(req, email, company, out msg);
        if (string.IsNullOrEmpty(code))
            throw new Exception(string.IsNullOrEmpty(msg) ? "服务端签发失败，请稍后重试。" : msg);
        return code;
    }
}
