/* form_new.cs —— 精简版主界面（步骤化引导 + 剪贴板自动识别 + 注册表定位）
 * 只保留最少文字：按钮=数字+短词，标签在文本框左侧且宽度足够，绝不遮挡。
 */
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Net;
using System.ComponentModel;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Text.RegularExpressions;

internal sealed class MainForm : GWindow
{
    private GTextBox txtDir, txtEmail, txtCompany, txtLog;
    private Label lblStep;
    private int gLastPct = -1;
    private GButton btnMode;

    public MainForm()
    {

        Title = "Navicat 18 激活工具";
        SubTitle = "仅支持 Navicat Premium 18 ｜ 选择目录 → 安装补丁 → 手动开注册页 → 开始激活";
        ClientSize = new Size(760, 634);
        Font = new Font("Microsoft YaHei UI", 9F);

        Label("目录", 24, 84);
        txtDir = Box(24, 106, 520, false);
        Btn("选择目录", 556, 107, 180, PickDir);

        Label("邮箱", 24, 158);
        txtEmail = Box(24, 180, 340, false);
        Label("公司", 380, 158);
        txtCompany = Box(380, 180, 356, false);

        PBtn("自动安装 Navicat", 24, 240, 220, Download);
        PBtn("安装补丁", 256, 240, 180, PatchOnly);
        PBtn("⚡ 开始激活", 448, 240, 288, AutoRun);

        // ── 激活方式（点击切换；独立一行，说明放下一行避免挤压）──
        Label("激活方式", 24, 288);
        btnMode = new GButton
        {
            Text = ModeText(),
            Left = 116, Top = 284, Width = 320, Height = 34, Primary = false
        };
        btnMode.Click += ToggleMode;
        Controls.Add(btnMode);

        // ── 手动激活（请求码 → 激活码）。窗口默认不存在，点这个按钮才弹出 ──
        PBtn("手动激活", 448, 284, 288, OpenManual);

        Hint("※ libcf.dll 最稳；version.dll 更新后仍有效（可能被安全软件误报为毒，属误报）", 24, 322);

        // ── 许可密钥（可自定义；点【安装补丁】会用它接着自动激活）──
        Label("许可密钥", 24, 350);
        txtLicKey = new GTextBox(false) { Left = 24, Top = 370, Width = 520, Height = 38 };
        txtLicKey.Text = LICKEY;
        Controls.Add(txtLicKey);

        Hint("※ 没有装 Navicat 就点「自动安装 Navicat」（多线程下载 + 静默安装）", 24, 418);
        Hint("※ 推荐流程：① 安装补丁 → ② 手动双击打开 Navicat → ③ 帮助→注册（之后的步骤全自动）", 24, 438);

        Label("日志", 24, 468);
        txtLog = Box(24, 490, 712, true);
        txtLog.Height = 100;
        txtLog.ReadOnly = true;

        lblStep = new GLabel
        {
            Left = 24, Top = 600, Width = 712, Height = 18,
            ForeColor = G.Gold,
            Text = "选目录 → 安装补丁 → 手动打开 Navicat（帮助→注册）→ 开始激活"
        };
        Controls.Add(lblStep);


        Log("Navicat 18 激活工具已就绪。");
        // 目录：注册表优先
        txtDir.Text = FindNavicat();
        Log("已定位程序目录。");
        CheckDir(true);
        // 倒计时显示已按要求移除（离线版不展示到期时间）
    }


    // ---------------------------------------------------------- 控件工厂
    private Label Label(string t, int x, int y)
    {
        var l = new GLabel { Text = t, Left = x, Top = y, Height = 20, ForeColor = G.Gold };
        // ★ 宽度按实际文字算，不要固定 200：
        //   GLabel 是 BackColor=Transparent 的 Label，而 WinForms 的"伪透明"只会重绘
        //   父容器背景，不会理会同层级的其它控件 —— 一旦 Label 比文字宽很多，
        //   多出来的部分就会把旁边控件（如"激活方式"旁边的按钮）糊掉一块。
        l.Width = Math.Max(48, TextRenderer.MeasureText(t, l.Font).Width + 6);
        Controls.Add(l);
        return l;
    }

    private GTextBox Box(int x, int y, int w, bool multi)
    {
        var b = new GTextBox(multi) { Left = x, Top = y, Width = w, Height = 36 };
        Controls.Add(b);
        return b;
    }

    private Label Hint(string t, int x, int y)
    {
        var l = new GLabel { Text = t, Left = x, Top = y, Width = 712, Height = 18,
                             ForeColor = G.Dim2, Font = new Font("Microsoft YaHei UI", 8.5F) };
        Controls.Add(l);
        return l;
    }

    private const string LICKEY = "NAVGTPMSYAXVTH7H";
    private GTextBox txtLicKey;

    /// <summary>跨线程安全地更新界面</summary>
    private void Ui(Action a)
    {
        if (InvokeRequired) BeginInvoke(a); else a();
    }



    // ---- Navy Professional 设计令牌（与 dist/Navicat18Tool-UI-Prototype.html 一致）----
    private static readonly Color CLR_PRIMARY = Color.FromArgb(30, 58, 95);
    private static readonly Color CLR_SECOND  = Color.FromArgb(37, 99, 235);
    private static readonly Color CLR_ACCENT  = Color.FromArgb(5, 150, 105);
    private static readonly Color CLR_DANGER  = Color.FromArgb(220, 38, 38);
    private static readonly Color CLR_BG      = Color.FromArgb(248, 250, 252);
    private static readonly Color CLR_FG      = Color.FromArgb(15, 23, 42);
    private static readonly Color CLR_MUTED   = Color.FromArgb(100, 116, 139);
    private static readonly Color CLR_MUTEBG  = Color.FromArgb(241, 245, 249);
    private static readonly Color CLR_BORDER  = Color.FromArgb(228, 231, 235);

    /// <summary>给控件加圆角（WinForms 用 Region 模拟）</summary>
    private static void Round(Control c, int r)
    {
        try
        {
            using (var p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int d = r * 2;
                p.AddArc(0, 0, d, d, 180, 90);
                p.AddArc(c.Width - d, 0, d, d, 270, 90);
                p.AddArc(c.Width - d, c.Height - d, d, d, 0, 90);
                p.AddArc(0, c.Height - d, d, d, 90, 90);
                p.CloseFigure();
                c.Region = new Region(p);
            }
        }
        catch { }
    }

    private void Btn(string t, int x, int y, int w, EventHandler h)
    {
        var b = new GButton { Text = t, Left = x, Top = y, Width = w, Height = 34, Primary = false };
        b.Click += h;
        Controls.Add(b);
    }

    private void PBtn(string t, int x, int y, int w, EventHandler h)
    {
        var b = new GButton { Text = t, Left = x, Top = y, Width = w, Height = 34, Primary = true };
        b.Click += h;
        Controls.Add(b);
    }

    private void Log(string s)
    {
        txtLog.AppendText(s + "\r\n");
    }

    private void Step(string s)
    {
        lblStep.Text = s;
        // ★ 同步到「正在激活」等待窗（如果开着），省得用户一直盯着小字日志
        if (gWait != null) { try { gWait.SetText(s); } catch { } }
    }

    // ---------------------------------------------------------- 异步等待提示窗

    private WaitForm gWait;   // null = 当前没显示

    /// <summary>弹出「正在激活」等待窗。用 Show（非模态）而不是 ShowDialog，
    /// 所以不会阻塞激活线程；用户把它关掉也不会中断激活。</summary>
    private void ShowWait(string title, string text)
    {
        Ui(delegate()
        {
            try
            {
                if (gWait != null) { gWait.SetTitle(title); gWait.SetText(text); return; }
                gWait = new WaitForm(title, text);
                gWait.FormClosed += delegate { gWait = null; };
                gWait.Show(this);
            }
            catch { }
        });
    }

    /// <summary>收掉等待窗。任何出口都要调用 —— 否则屏幕上会留一个假的「正在激活」。</summary>
    private void HideWait()
    {
        Ui(delegate()
        {
            try
            {
                if (gWait == null) return;
                var w = gWait;
                gWait = null;
                w.Close();
            }
            catch { }
        });
    }

    // ---------------------------------------------------------- ② 目录
    private static string FindNavicat()
    {
        string[] appPaths = {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\navicat.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\navicat.exe"
        };
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (string sub in appPaths)
                try
                {
                    using (var k = root.OpenSubKey(sub))
                    {
                        if (k == null) continue;
                        string v = Convert.ToString(k.GetValue(""));
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return Path.GetDirectoryName(v);
                    }
                }
                catch { }

        string[] unins = {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (string basePath in unins)
                try
                {
                    using (var k = root.OpenSubKey(basePath))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetSubKeyNames())
                            using (var sk = k.OpenSubKey(name))
                            {
                                if (sk == null) continue;
                                string dn = Convert.ToString(sk.GetValue("DisplayName"));
                                string loc = Convert.ToString(sk.GetValue("InstallLocation"));
                                if (dn != null && dn.IndexOf("Navicat", StringComparison.OrdinalIgnoreCase) >= 0
                                    && !string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(loc, "navicat.exe")))
                                    return loc;
                            }
                    }
                }
                catch { }

        return @"C:\Program Files\Navicat\Navicat Premium 18";
    }

    private bool Admin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return true;
        }
        catch { }
        if (MessageBox.Show(this, "写入 Program Files 需要管理员权限，是否以管理员身份重启？", "提示",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
                Environment.Exit(0);
            }
            catch { }
        }
        return false;
    }


    // ---------------------------------------------------------- 目录校验
    /// <summary>读取 navicat.exe 的 PE 位数</summary>
    private static string PeBits(string exe)
    {
        try
        {
            using (var fs = File.OpenRead(exe))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt16() != 0x5A4D) return "";
                fs.Position = 0x3C;
                int off = br.ReadInt32();
                if (off <= 0 || off > fs.Length - 6) return "";
                fs.Position = off;
                if (br.ReadUInt32() != 0x00004550) return "";
                ushort machine = br.ReadUInt16();
                if (machine == 0x8664) return "x64";
                if (machine == 0x014C) return "x86";
                return "";
            }
        }
        catch { return ""; }
    }

    /// <summary>校验该目录是不是 Navicat Premium 18。ok=false 时 info 是失败原因</summary>
    private static bool VerifyDir(string dir, out string info)
    {
        info = "";
        if (string.IsNullOrWhiteSpace(dir)) { info = "目录为空"; return false; }
        dir = dir.Trim().Trim('"').TrimEnd('\\');
        string exe = Path.Combine(dir, "navicat.exe");
        if (!File.Exists(exe)) { info = "该目录下没有 navicat.exe"; return false; }

        // A. 读 PE 版本资源
        string prod = "", fver = "", pver = "", comp = "", copy = "";
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exe);
            prod = (vi.ProductName ?? "").Trim();
            fver = (vi.FileVersion ?? "").Trim();
            pver = (vi.ProductVersion ?? "").Trim();
            comp = (vi.CompanyName ?? "").Trim();
            copy = (vi.LegalCopyright ?? "").Trim();
        }
        catch { }

        // B. 原始字节兜底（版本资源缺失/被改时）
        bool rawNav = false;
        try
        {
            using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(fs.Length, 8L * 1024 * 1024);
                var buf = new byte[len];
                int got = 0;
                while (got < len) { int n = fs.Read(buf, got, len - got); if (n <= 0) break; got += n; }
                var latin = Encoding.GetEncoding(28591).GetString(buf, 0, got);
                rawNav = latin.IndexOf("Navicat", StringComparison.OrdinalIgnoreCase) >= 0
                      || latin.IndexOf("PremiumSoft", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { }

        // C. 是不是 Navicat
        bool isNav = prod.IndexOf("Navicat", StringComparison.OrdinalIgnoreCase) >= 0
                  || comp.IndexOf("PremiumSoft", StringComparison.OrdinalIgnoreCase) >= 0
                  || copy.IndexOf("Navicat", StringComparison.OrdinalIgnoreCase) >= 0
                  || rawNav;
        if (!isNav) { info = "这不是 Navicat（未找到 Navicat / PremiumSoft 标识）"; return false; }

        // D. 主版本必须是 18
        string verSrc = fver != "" ? fver : pver;
        int major = 0;
        try { var m = Regex.Match(verSrc, "\\d+"); if (m.Success) int.TryParse(m.Value, out major); } catch { }
        if (major != 0 && major != 18)
        { info = "版本不匹配：检测到 Navicat " + major + "，本工具只支持 Navicat Premium 18"; return false; }

        // E. 关键依赖
        if (!File.Exists(Path.Combine(dir, "libcc.dll")))
        { info = "目录里没有 libcc.dll，不像是 Navicat 18 的安装目录"; return false; }

        string bits = PeBits(exe);
        info = (prod != "" ? prod : "Navicat Premium")
             + " " + (verSrc != "" ? verSrc : "18.x")
             + (bits != "" ? " (" + bits + ")" : "")
             + (comp != "" ? " · " + comp : "");
        return true;
    }

    /// <summary>校验并反馈（silent=true 时只写日志，不弹窗）</summary>
    private bool CheckDir(bool silent)
    {
        string info;
        if (VerifyDir(txtDir.Text, out info))
        {
            Log("✔ 目录校验通过：" + info);
            return true;
        }
        Log("✘ 目录校验失败：" + info);
        if (!silent)
            MessageBox.Show(this, info + "\n\n请选择 Navicat Premium 18 的安装目录（目录里应有 navicat.exe 与 libcc.dll）。",
                "目录校验失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void Download(object s, EventArgs e)
    {
        if (!LicenseGate.Allow()) return;
        // 多个下载源，逐个探测（官网链接偶尔会变，留备用可显著降低"自动安装"失败率）
        string[] sources = new[]
        {
            "https://dn.navicat.com/download/navicat18_premium_cs_x64.exe",
            "https://download.navicat.com/download/navicat18_premium_cs_x64.exe"
        };
        string tmp = Path.Combine(Path.GetTempPath(), "navicat18_premium_cs_x64.exe");
        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 16;
            long total = 0; bool ranges = false;
            string url = null;
            foreach (string cand in sources)
            {
                try
                {
                    var hreq = (HttpWebRequest)WebRequest.Create(cand);
                    hreq.Method = "HEAD"; hreq.Timeout = 15000; hreq.UserAgent = "Navicat18Tool";
                    using (var hr = (HttpWebResponse)hreq.GetResponse())
                    {
                        long cl = hr.ContentLength;
                        if (cl > 1024 * 1024)
                        {
                            total = cl;
                            ranges = string.Equals(hr.Headers["Accept-Ranges"], "bytes", StringComparison.OrdinalIgnoreCase);
                            url = cand;
                            break;
                        }
                    }
                }
                catch (Exception ex) { Log("下载源不可用（" + cand + "）：" + ex.Message); }
            }
            if (url == null) { Step("所有下载源都不可用，请到官网手动下载"); return; }
            Log("使用下载源：" + url);
            if (total <= 0) { Step("无法获取安装包大小，请手动下载"); return; }

            int parts = ranges ? 8 : 1;
            using (var fs0 = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Write)) fs0.SetLength(total);
            Log("开始下载 Navicat 安装包（" + parts + " 线程，" + total + " 字节）。");
            long done = 0;
            var threads = new List<System.Threading.Thread>();
            for (int i = 0; i < parts; i++)
            {
                long st = total * i / parts, en = total * (i + 1) / parts - 1;
                var th = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.UserAgent = "Navicat18Tool";
                        req.AddRange(st, en);
                        req.Timeout = 30000; req.ReadWriteTimeout = 30000;
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var rs = resp.GetResponseStream())
                        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.Write))
                        {
                            fs.Seek(st, SeekOrigin.Begin);
                            var buf = new byte[131072];
                            int n;
                            while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                            {
                                fs.Write(buf, 0, n);
                                long d = System.Threading.Interlocked.Add(ref done, n);
                                int pct = (int)(d * 100 / total);
                                if (pct != gLastPct)
                                {
                                    gLastPct = pct;
                                    Ui(() => Step("正在下载 Navicat 安装包 " + pct + "%（" + parts + " 线程）"));
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Ui(() => Log("分块下载出错：" + ex.Message)); }
                });
                th.IsBackground = true; threads.Add(th); th.Start();
            }

            var waiter = new System.Threading.Thread(() =>
            {
                foreach (var th in threads) th.Join();
                long len = new FileInfo(tmp).Length;
                if (len != total) { Ui(() => { Step("下载不完整，请重试"); Log("大小不符：" + len + "/" + total); }); return; }
                try
                {
                    using (var fs = File.OpenRead(tmp))
                    {
                        var hd = new byte[2];
                        if (fs.Read(hd, 0, 2) < 2 || hd[0] != 0x4D || hd[1] != 0x5A)
                        { Ui(() => { Step("下载到的不是安装包，请手动到官网下载"); Log("安装包校验失败。"); }); return; }
                    }
                }
                catch { }
                Ui(() => { Step("下载完成，正在静默安装 Navicat（会弹出一次管理员授权）…"); Log("安装包已下载完成，开始静默安装。"); });
                bool ok = false;
                try
                {
                    // 安装包已实测为 Inno Setup 打包(PE 内命中 Inno Setup 标识)，
                    // 支持标准静默参数：/VERYSILENT = 完全无界面，/SP- = 跳过"准备安装"页
                    var psi = new ProcessStartInfo(tmp, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
                    { UseShellExecute = true, Verb = "runas" };
                    var sp = Process.Start(psi);
                    try { sp.WaitForExit(); } catch { }
                    // Inno Setup 的静默安装会 fork 出独立子进程，WaitForExit 往往提前返回；
                    // 若此时就去查目录一定会误判"安装失败"→回退向导。所以改为轮询等文件真正落地。
                    Ui(() => Log("安装程序已启动，正在等待它把文件写完…"));
                    string found = null;
                    for (int w = 0; w < 90; w++)            // 最多等 180 秒
                    {
                        System.Threading.Thread.Sleep(2000);
                        try
                        {
                            string d = FindNavicat();
                            if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "navicat.exe")))
                            { found = d; break; }
                        }
                        catch { }
                    }
                    if (found != null) { string fd = found; Ui(() => { txtDir.Text = fd; }); ok = true; }
                    else
                    {
                        try { Ui(() => { txtDir.Text = FindNavicat(); }); } catch { }
                        ok = false;
                    }
                }
                catch (Exception ex) { Ui(() => Log("静默安装调用失败：" + ex.Message)); }

                if (ok)
                {
                    Ui(() => { Step("Navicat 安装完成 → 到 Navicat 点 帮助→注册，再点「⚡ 开始激活」"); });
                    Ui(() => { Log("静默安装完成，安装目录已自动刷新。"); });
                    Ui(() => { Log("下一步：打开 Navicat → 帮助 → 注册 → 回本工具点【⚡ 开始激活】（会自动装补丁并激活）"); });
                }
                else
                {
                    Ui(() => { Step("静默安装未完成 → 已弹出安装向导，请手动点下一步装完"); Log("已回退到向导安装，请按向导完成。"); });
                    try { Process.Start(tmp); } catch (Exception ex) { Ui(() => Log("打开安装向导失败：" + ex.Message)); }
                }
            });
            waiter.IsBackground = true;
            waiter.Start();
        }
        catch (Exception ex) { Log("下载失败：" + ex.Message); }
    }

    private void PickDir(object s, EventArgs e)
    {
        using (var d = new FolderBrowserDialog())
        {
            d.Description = "选择 Navicat 目录（含 navicat.exe）";
            if (d.ShowDialog() == DialogResult.OK) txtDir.Text = d.SelectedPath;
        }
        if (!CheckDir(false)) { Step("目录校验未通过，请重新选择"); return; }
        Step("目录校验通过");
    }

    // ---------------------------------------------------------- ② 装补丁

    // ---------------------------------------------------------- ③ 启动

    // ---------------------------------------------------------- ⑤ 剪贴板自动识别

    // ---------------------------------------------------------- ⑥ 生成


    // ---------------------------------------------------------- ⚡ 开始激活
    // 前提：用户已点过 Navicat 的「帮助 → 注册」（注册窗口可见）
    /// <summary>只装补丁：装完后引导用户手动启动 Navicat 并打开注册页面</summary>

    /// <summary>当前使用的许可密钥：优先取输入框，为空则用内置默认值。</summary>
    private string CurLicKey()
    {
        try
        {
            if (txtLicKey != null)
            {
                string t = txtLicKey.Text.Trim();
                if (t.Length >= 8) return t;
            }
        }
        catch { }
        return LICKEY;
    }

    // ---------------------------------------------------------- 激活方式选择

    /// <summary>当前方式的显示文本（只放文件名：自绘按钮会截断过长文本）</summary>
    private string ModeText()
    {
        return PePatcher.Mode == PePatcher.PatchMode.VersionDll
            ? "version.dll"
            : "libcf.dll";
    }

    /// <summary>点击切换激活方式。两种方式不可同时生效，切换后必须重新安装补丁。
    /// 注意：GButton 是自绘控件，改 Text 后必须显式 Invalidate 才会重绘。</summary>
    private void ToggleMode(object s, EventArgs e)
    {
        // 只有内嵌了真正的代理 DLL 才能切到 version.dll 方式；
        // 否则（构建机上没有 MinGW）装进 Navicat 目录的会是一个不可用的空 DLL。
        if (PePatcher.Mode == PePatcher.PatchMode.Libcf && !ProxyData.Available)
        {
            Log("⚠️ 这个 exe 没有内嵌代理 DLL，无法使用【version.dll】方式。");
            Log("   原因：构建这个 exe 的机器上没有 MinGW gcc，代理 DLL 编译不出来。");
            Log("   请继续用【libcf.dll】方式（两种方式的激活效果相同）。");
            Log("   要用 version.dll 方式：在装了 MinGW 的机器上重跑 build\\build_offline.cmd，");
            Log("   脚本会自动编译代理 DLL 并内嵌进来。");
            MessageBox.Show(this,
                "这个 exe 没有内嵌代理 DLL，无法使用【version.dll】方式。\n\n" +
                "请继续使用【libcf.dll】方式（两种方式的激活效果相同）。\n\n" +
                "要用 version.dll 方式：在装了 MinGW-w64 的机器上重新运行\n" +
                "build\\build_offline.cmd —— 脚本会自动编译并内嵌代理 DLL。",
                "该方式不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        PePatcher.Mode = PePatcher.Mode == PePatcher.PatchMode.Libcf
            ? PePatcher.PatchMode.VersionDll
            : PePatcher.PatchMode.Libcf;
        btnMode.Text = ModeText();
        btnMode.Invalidate();          // 自绘控件：强制重绘，否则看起来"点了没反应"
        btnMode.Update();
        if (lblStep != null)
            lblStep.Text = "选目录 → 安装补丁 → 手动打开 Navicat（帮助→注册）→ 开始激活　【当前方式：" + ModeText() + "】";

        if (PePatcher.Mode == PePatcher.PatchMode.VersionDll)
        {
            Log("══════ 激活方式已切换为【version.dll】（运行时补丁）══════");
            Log("  特点：不修改 navicat.exe / libcf.dll 的磁盘文件，Navicat 官方更新不会移除它。");
            Log("  代价：若新版 libcf.dll 被重编译，运行时可能定位不到目标函数");
            Log("        （会写 %TEMP%\\nvproxy.log 提示，不会静默失败）。");
            Log("  ⚠️ 提醒：这种方式会被部分安全软件【误报为病毒】，原因如下 ——");
            Log("     Windows 加载程序时会先在程序自己目录里找同名 DLL（系统既定搜索顺序，");
            Log("     不是漏洞）；我们放的 version.dll 把功能原样转发给系统那个，再在");
            Log("     【内存里】替换 Navicat 的许可校验。这三点行为与恶意软件的「DLL 劫持」");
            Log("     一致，杀软只认行为特征、分不清意图，所以可能拦它 —— 属于误报。");
            Log("     它不联网、不改系统文件、不改 Navicat 磁盘文件，删掉即卸载。");
            Log("     处置：把 Navicat 安装目录加入安全软件的信任区/排除项即可，不必关杀软。");
        }
        else
        {
            Log("══════ 激活方式已切换为【libcf.dll】（文件级补丁）══════");
            Log("  特点：直接改文件，兼容性最确定。");
            Log("  代价：Navicat 官方更新会覆盖这两个文件，补丁随之丢失，需要重打。");
        }
        Log("※ 切换方式后请重新点【安装补丁】（会自动清理另一种方式的残留）。");
    }

    /// <summary>弹出手动激活窗口（请求码 → 激活码）。
    /// 不点主界面那个按钮，这个界面就完全不会出现（默认隐藏）。
    /// ★ 把主界面第 2 步填过的邮箱/公司带进去（要写进激活码的 N/O 字段），
    ///   用户也可以在弹出的窗口里改。</summary>
    private void OpenManual(object s, EventArgs e)
    {
        using (var f = new ManualActForm(txtEmail.Text.Trim(), txtCompany.Text.Trim()))
            f.ShowDialog(this);
    }

    private void PatchOnly(object s, EventArgs e)
    {
        if (!LicenseGate.Allow()) return;
        Step("🛠 正在安装补丁…");
        Log("=== 安装补丁 ===");
        if (PePatcher.Mode == PePatcher.PatchMode.VersionDll)
        {
            // 用 version.dll 的人一定会点这里，所以在此再提示一次（不必切换也能看到）
            Log("※ 当前方式为 version.dll：它可能被安全软件【误报为病毒】，属误报。");
            Log("  原理：Windows 会优先在程序自己目录里找同名 DLL（系统既定顺序），");
            Log("  我们这份把功能转发给系统那个，再在【内存里】替换许可校验 ——");
            Log("  行为与「DLL 劫持」相似，杀软只认特征、分不清意图，所以会拦。");
            Log("  它不联网、不改系统文件、不改 Navicat 磁盘文件；加入信任区即可。");
            Log("  注意：version.dll 方式的代理 DLL 是构建时内嵌进 exe 的（build\\build_offline.cmd 自动编译）；");
            Log("        这个 exe 里没有代理 DLL 时，切换方式会被拒绝。");
            Log("        它不修改 Navicat 的任何文件，官方更新后仍然有效。");
        }
        var th = new System.Threading.Thread(delegate()
        {
            try
            {
                try { Ui(delegate() { txtDir.Text = FindNavicat(); }); } catch { }
                System.Threading.Thread.Sleep(150);
                string dir = txtDir.Text.Trim().Trim('"');
                string navExe = System.IO.Path.Combine(dir, "navicat.exe");

                string err = AutoAct.InstallPatchOnly(dir, navExe, delegate(string m) { Ui(delegate() { Log(m); }); });

                if (err == "__NAVICAT_RUNNING__")
                {
                    Ui(delegate()
                    {
                        Step("⚠️ 请先关闭 Navicat");
                        Log("⚠️ 安装补丁需要先关闭 Navicat。请关闭后再点【安装补丁】。");
                        MessageBox.Show(this,
                            "安装补丁需要先关闭 Navicat（文件被占用写不进去）。\n\n" +
                            "请关闭 Navicat 后，再点一次【安装补丁】。",
                            "请先关闭 Navicat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                    return;
                }
                if (err != "")
                {
                    Ui(delegate()
                    {
                        Step("⚠️ " + err);
                        Log("⚠️ " + err);
                        MessageBox.Show(this, err, "安装补丁失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                    return;
                }

                // ★ 补丁装好后【直接接着激活】，不再让用户回去点一次【开始激活】
                //   （AutoRun 内部会：等待双击打开 Navicat → 自动打注册窗 → 自动填入上方
                //     输入框里的许可密钥 → Ctrl+Shift 点激活 → 取请求码 → 生成激活码）
                Ui(delegate()
                {
                    Step("✅ 补丁已安装 → 正在接着激活，请按提示双击打开 Navicat");
                    // ★ 明确的「安装成功」提示：此前这里只写日志，用户常以为没装上而
                    //   反复点【安装补丁】。用模态框让他确认一次，再进入激活。
                    MessageBox.Show(this,
                        "补丁安装成功！\n\n" +
                        "接下来完成 Navicat 激活：\n" +
                        "  1. 请手动【双击】打开 Navicat\n" +
                        "  2. 在 Navicat 里点：帮助 → 注册\n" +
                        "     （若弹出「试用提醒」，点其中的【注册】按钮）\n\n" +
                        "点「确定」后，工具会自动完成剩下的步骤（约 10~30 秒）。",
                        "✅ 补丁安装成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Log("");
                    Log("══════ 补丁已安装，接着自动完成激活 ══════");
                    Log("  ① 请手动【双击】打开 Navicat（不要用本工具启动）");
                    Log("  ② 在 Navicat 里点：帮助 → 注册");
                    Log("     （若弹出「试用提醒」，点其中的【注册】按钮即可）");
                    Log("  ③ 之后本工具自动完成：填密钥 → 点激活 → 生成激活码");
                    Log("══════════════════════════════════════");
                    Log("（正在等待 Navicat 打开，本工具会自动检测并继续，无需再点按钮）");

                    // ★ 放进同一个 UI 委托里：等用户点掉上面那个「补丁安装成功」之后
                    //   才开始激活、才弹等待窗 —— 否则弹窗与等待窗会同时糊在屏幕上。
                    //   AutoRun 自己只启动后台线程，不会卡住 UI。
                    AutoRun(s, e);
                });
                return;
            }
            catch (Exception ex)
            {
                Ui(delegate() { Step("⚠️ 安装补丁异常"); Log("安装补丁异常：" + ex.Message); });
            }
        });
        th.IsBackground = true;
        th.Start();
    }

    /// <summary>后台轮询用户手动操作的进度（检测到 Navicat / 注册界面就更新状态栏提示）</summary>
    private void WatchManualLaunch()
    {
        var th = new System.Threading.Thread(delegate()
        {
            bool toldNav = false, toldReg = false;
            for (int i = 0; i < 900; i++)          // 最多 15 分钟
            {
                System.Threading.Thread.Sleep(1000);
                uint pid = AutoAct.FirstNavicatPid();
                if (pid == 0) { toldNav = false; continue; }

                string st = AutoAct.WatchState(pid);
                if (st == "reg")
                {
                    if (!toldReg)
                    {
                        toldReg = true;
                        Ui(delegate()
                        {
                            Step("② 注册界面已打开 → 现在点【⚡ 开始激活】");
                            Log("✔ 检测到注册界面已打开 → 现在点【⚡ 开始激活】即可完成激活。");
                        });
                    }
                }
                else if (st == "nav" && !toldNav)
                {
                    toldNav = true;
                    Ui(delegate()
                    {
                        Step("① 已检测到 Navicat → 请点 帮助 → 注册");
                        Log("✔ 检测到 Navicat 已打开 → 请在 Navicat 里点：帮助 → 注册");
                    });
                }
            }
        });
        th.IsBackground = true;
        th.Start();
    }

    /* ★ 防重入包装：PatchOnly 装完补丁会自动调用本方法，用户也可能再点【开始激活】；
       不加锁会两个线程并发跑（日志成倍重复、互相干扰）。 */
    private static int gAutoRunning = 0;

    private void AutoRun(object s, EventArgs e)
    {
        if (System.Threading.Interlocked.CompareExchange(ref gAutoRunning, 1, 0) != 0)
        {
            Log("（激活流程已在运行中，忽略本次点击）");
            return;
        }
        try { AutoRunCore(s, e); }
        finally { System.Threading.Interlocked.Exchange(ref gAutoRunning, 0); }
    }

    private void AutoRunCore(object s, EventArgs e)
    {
        if (!LicenseGate.Allow()) return;
        string email = txtEmail.Text.Trim(), company = txtCompany.Text.Trim();
        Step("⚡ 开始激活：正在识别 Navicat 界面…");
        Log("=== 开始激活 ===");
        var th = new System.Threading.Thread(delegate()
        {
            // ★ 异步等待提示：非模态窗口，激活期间一直显示当前进度（由 Step 实时刷新）
            ShowWait("正在激活…", "正在识别 Navicat 界面，请稍候…");
            try
            {
                string req, code;
                // 重新定位一次目录（用户可能刚装完 Navicat，或换过安装位置）
                try { Ui(delegate() { txtDir.Text = FindNavicat(); }); } catch { }
                System.Threading.Thread.Sleep(150);
                string reqDir = txtDir.Text.Trim().Trim('"');
                string navExe = System.IO.Path.Combine(reqDir, "navicat.exe");
                // ★ 日志里以 ##STEP## 开头的行会同步更新界面上的状态提示，
                //   这样"请双击打开 Navicat"这类指引不必靠用户翻日志。
                Action<string> onLog = delegate(string m)
                {
                    Ui(delegate()
                    {
                        if (m != null && m.StartsWith("##STEP##")) Step(m.Substring(8));
                        else Log(m);
                    });
                };
                string err = AutoAct.Run(CurLicKey(), navExe, email, company, onLog, out req, out code);

                if (err == "__NAVICAT_RUNNING__")
                {
                    Ui(delegate()
                    {
                        Step("⚠️ 补丁尚未安装，请先关闭 Navicat");
                        Log("⚠️ 补丁还没装，而安装补丁必须关闭 Navicat（文件被占用写不进去）。");
                        Log("   检测到 Navicat 正在运行 → 本次无法继续。");
                        Log("   请先手动关闭 Navicat，然后再点一次【开始激活】。");
                        Log("   （补丁装好之后，就不会再要求你关闭 Navicat 了）");
                        MessageBox.Show(this,
                            "补丁还没有安装，而安装补丁必须关闭 Navicat（文件被占用写不进去）。\n\n" +
                            "检测到 Navicat 正在运行，所以这次没法继续。\n\n" +
                            "请先手动关闭 Navicat（在 Navicat 里点：文件 → 退出），\n" +
                            "然后重新点击【开始激活】。\n\n" +
                            "（补丁装好之后，就不会再要求你关闭 Navicat 了）",
                            "请先关闭 Navicat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
                else if (err == "__RESTARTED__")
                {
                    Ui(delegate()
                    {
                        Step("⚠️ 请求码仍是旧公钥生成的");
                        Log("⚠️ 请手动做一次：");
                        Log("   1) 关闭 Navicat");
                        Log("   2) 用【双击】方式重新打开 Navicat（不要用管理员方式）");
                        Log("   3) 再点一次【开始激活】");
                        Log("   （一般一次就好：因为工具自动启动的 Navicat 是管理员权限）");
                        MessageBox.Show(this,
                            "这个请求码是用【旧公钥】生成的。\n\n" +
                            "请手动操作一次：\n" +
                            "  1. 关闭 Navicat\n" +
                            "  2. 用【双击】方式重新打开 Navicat\n" +
                            "     （不要用管理员方式运行）\n" +
                            "  3. 再点【⚡ 开始激活】",
                            "请手动重开一次 Navicat", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
                else if (err == "__NOREG__")
                {
                    Ui(delegate()
                    {
                        Step("⚠️ 请先手动打开注册界面");
                        Log("⚠️ 没有检测到「注册」窗口。");
                        Log("   请在 Navicat 里手动点：帮助 → 注册，让它显示出来，然后再点一次本按钮。");
                        MessageBox.Show(this,
                            "没有检测到「注册」窗口。\n\n请先在 Navicat 里手动点一下：\n帮助 → 注册\n\n（注册窗口显示出来后）再点【⚡ 开始激活】。",
                            "请先打开注册界面", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                else if (err == "__ACTIVATED__")
                {
                    Ui(delegate()
                    {
                        Step("✅ 已激活，且补丁已检查");
                        Log("✅ 当前已是激活状态，无需重复激活。");
                        Log("   补丁检查/安装也已执行（若之前未装，本次已自动安装并重启 Navicat）。");
                        MessageBox.Show(this, "当前 Navicat 已经处于激活状态，无需再次激活。\n\n补丁检查/安装已完成 —— 若之前未安装，本次已自动安装并重启了 Navicat。\n\n（注册窗口里能看到【取消激活】按钮即为已激活）",
                            "已激活", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                else if (err.Length == 0)
                {
                    Ui(delegate()
                    {
                        try { Clipboard.SetText(code); } catch { }
                        Log("—— 请求码 ——"); Log(req);
                        Log("—— 激活码（已复制到剪贴板）——"); Log(code);
                        Step("✅ 激活完成（含补丁）");
                        Log("✅ 激活完成！");
                        Log("   已自动检查/安装补丁；若本次是新装补丁，Navicat 已被自动重启。");
                        MessageBox.Show(this, "激活已完成！\n\nNavicat 里应显示【企业版 · 永久】。\n\n补丁也已自动检查/安装（若本次新装，Navicat 已自动重启）。",
                            "激活成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    });
                }
                else
                {
                    Ui(delegate()
                    {
                        Step("激活未完成");
                        Log("✘ " + err);
                        MessageBox.Show(this, err + "\n\n可以改用下面的手动流程。",
                            "开始激活", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                }
            }
            catch (Exception ex)
            {
                Ui(delegate() { Log("开始激活异常：" + ex.Message); });
            }
            finally
            {
                // ★ 无论成功、失败、异常还是提前 return，都必须把等待窗收掉 ——
                //   否则屏幕上会留一个假的「正在激活」。
                HideWait();
            }
        });
        th.IsBackground = true;
        th.Start();
    }



    private void Uninstall(object s, EventArgs e)
    {
        if (!LicenseGate.Allow()) return;
        if (!Admin()) return;
        try
        {
            PePatcher.Uninstall(txtDir.Text.Trim().Trim('"'), Log);
            Step("已还原 Navicat 原文件");
        }
        catch (Exception ex) { Log("卸载失败：" + ex.Message); }
    }
}
