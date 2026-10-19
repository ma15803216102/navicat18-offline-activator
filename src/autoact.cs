// autoact.cs — 开始激活（一键完成）
// 流程（2026-10-12 按用户要求修正顺序）：
//   ① 定位 Navicat 安装目录（调用方提示 → 运行中进程 → 常见路径）
//   ② ★ 检查/安装补丁 —— 必须在激活【之前】！
//      原因：激活要求 navicat.exe 里的"请求码公钥"已被替换，否则 Navicat 用厂商公钥
//            加密请求码，服务端私钥解不开（报"这段内容无法解密，不是本机 Navicat 的请求码"）
//      关闭 Navicat 只在"需要写文件装补丁"时要求；补丁已装 → 不检查、不打断用户
//   ③ 启动 Navicat（补丁装好后会重启，确保跑的是补丁版；已在运行则不重启）
//      以【普通用户权限】启动（走 explorer），与用户手动双击等价 —— 实测手动打开的
//      那个进程才激活得成功，而直接 Process.Start 会继承工具的管理员权限
//      另外会自动处理 Navicat 首次运行弹出的「是否激活」提示框（它不是没用的：
//      没处理掉时 Navicat 生成的请求码仍用旧公钥加密，激活必定失败）
//      ★ 若本次刚装上补丁，会自动再重启一次 —— 全新安装后 Navicat 第一个进程
//        生成的请求码仍可能是用旧公钥加的（实测），重启一次即正常
//   ④ 监控「注册」界面是否已打开（没开 → 提示用户手动点 帮助→注册）
//   ⑤ 检查是否已激活：已激活 → 跳过激活步骤，但仍会继续
//   ⑥ 未激活 → 走激活流程（填密钥 → Ctrl+Shift 点【激活】→ 读请求码 → 服务端签发 → 填回 → 点【激活】→ 校验）
// 设计要点：全部用标准 Win32 消息/按键，不注入、不读进程内存；**各处等待都压到最短**以保证体验。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class AutoAct
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, StringBuilder lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_SETTEXT = 0x000C;
    const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, BM_CLICK = 0x00F5;
    const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_DOWN = 0x28, VK_UP = 0x26, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    const uint KEYUP = 0x0002;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint GW_OWNER = 4;
    const int SW_RESTORE = 9, SW_SHOW = 5;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const int MSG_TIMEOUT = 3000;

    public const string RegForm = "TRegistrationSubForm";
    public const string ManForm = "TManualActivationSubForm";

    /// <summary>是否输出 [诊断] 明细（默认关闭：日志更干净、也更快）。排查时改成 true。</summary>
    internal static bool ShowDiag = false;
    public const string MainForm = "TNavicatMainForm";

    // ───────────── 基础工具 ─────────────
    static string Cls(IntPtr h) { var sb = new StringBuilder(512); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    static string Txt(IntPtr h) { int n = GetWindowTextLength(h); var sb = new StringBuilder(n + 2); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }

    static string FullText(IntPtr h)
    {
        IntPtr res;
        SendMessageTimeout(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
        int n = res.ToInt32();
        if (n <= 0 || n > 200000) n = 200000;
        var sb = new StringBuilder(n + 2);
        SendMessageTimeout(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
        return sb.ToString();
    }

    public static IntPtr FindTop(uint pid, string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && Cls(h) == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr FindChild(IntPtr parent, string clsPart, string textPart)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            if (Cls(h).IndexOf(clsPart, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (textPart == null || Txt(h).IndexOf(textPart, StringComparison.Ordinal) >= 0)
                { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static List<IntPtr> FindChildren(IntPtr parent, string clsPart)
    {
        var res = new List<IntPtr>();
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l)
        {
            if (Cls(h).IndexOf(clsPart, StringComparison.OrdinalIgnoreCase) >= 0) res.Add(h);
            return true;
        }, IntPtr.Zero);
        return res;
    }

    static int TopOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.Top; }

    static bool LooksB64(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 100) return false;
        int ok = 0;
        foreach (char c in s)
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
                ok++;
        return ok * 100 / s.Length >= 95;
    }

    // ───────────── 键盘/鼠标（延时已压到最短）─────────────
    static void Key(byte vk) { Key(vk, 1); }
    static void Key(byte vk, int times)
    {
        for (int i = 0; i < times; i++)
        {
            keybd_event(vk, 0, 0, IntPtr.Zero);
            Thread.Sleep(22);
            keybd_event(vk, 0, KEYUP, IntPtr.Zero);
            Thread.Sleep(55);
        }
    }

    static void RealClick(int x, int y)
    {
        SetCursorPos(x, y); Thread.Sleep(35);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero); Thread.Sleep(28);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero); Thread.Sleep(90);
    }

    /// 点击：先按住 Ctrl+Shift，发点击消息，再松开（Navicat 的"激活"按钮需要这个组合键）
    static void ClickWithCtrlShift(IntPtr h)
    {
        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        keybd_event(VK_SHIFT, 0, 0, IntPtr.Zero);
        Thread.Sleep(80);
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)1, IntPtr.Zero);
        Thread.Sleep(40);
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(80);
        SendMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);   // 补充：标准按钮才需要，自绘按钮靠上面的鼠标消息
        Thread.Sleep(60);
        keybd_event(VK_SHIFT, 0, KEYUP, IntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, IntPtr.Zero);
    }

    static void PlainClick(IntPtr h)
    {
        PostMessage(h, WM_LBUTTONDOWN, (IntPtr)1, IntPtr.Zero);
        Thread.Sleep(40);
        PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(80);
        SendMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>有没有"在屏幕内"的下拉菜单（判断菜单真的弹出来了）</summary>
    static bool PopupOnScreen(uint pid)
    {
        bool ok = false;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && Cls(h) == "TThemedPopupMenu" && IsWindowVisible(h))
            {
                RECT r; GetWindowRect(h, out r);
                if (r.Top > 0 && r.Top < 3000 && r.Left >= 0 && (r.Right - r.Left) > 40) { ok = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return ok;
    }

    /// <summary>确保窗口非最小化且在前台（主窗口被最小化时，所有点击/按键都会落到屏幕外）</summary>
    static void EnsureForeground(IntPtr h)
    {
        try
        {
            if (IsIconic(h)) { ShowWindow(h, SW_RESTORE); Thread.Sleep(260); }
            ShowWindow(h, SW_SHOW);
            SetForegroundWindow(h);
            Thread.Sleep(180);
        }
        catch { }
    }

    /// <summary>从右往左扫描菜单栏，找最右那个能弹出菜单的 x（= 帮助）。返回 -1 失败。</summary>
    static int FindHelpMenuX(IntPtr hMain, uint pid, Action<string> log)
    {
        RECT rm; GetWindowRect(hMain, out rm);
        int menuY = rm.Top + 54;                       // 实测菜单栏在标题栏下方约 54px
        for (int x = rm.Left + 760; x > rm.Left + 40; x -= 32)
        {
            Key(VK_ESCAPE); Thread.Sleep(45);
            RealClick(x, menuY);
            Thread.Sleep(150);
            if (PopupOnScreen(pid))
            {
                Key(VK_ESCAPE); Thread.Sleep(70);
                log("  找到最右菜单 x=" + x + "（菜单栏 y=" + menuY + "）");
                return x;
            }
        }
        return -1;
    }

    /// <summary>打开「注册」窗口。
    /// 实测（2026-10-11 探针确认）：菜单栏 = 主窗口Top+54；最右侧菜单 = 帮助；
    /// 帮助菜单共 9 项，「注册」= 第 6 项（= 倒数第 4 项）；菜单是自绘的，读不到名字，只能靠坐标+按键。</summary>
    public static bool OpenRegistrationByKeyboard(uint pid, Action<string> log)
    {
        Action<string> L = delegate(string m) { if (log != null) log(m); };
        IntPtr hMain = FindTop(pid, MainForm);
        if (hMain == IntPtr.Zero) { L("! 找不到 Navicat 主窗口"); return false; }

        // 已经开着就直接用
        IntPtr hReg0 = FindRegWindow(pid);
        if (hReg0 != IntPtr.Zero && IsWindowVisible(hReg0)) { L("注册窗口已打开"); return true; }

        L("正在打开「注册」窗口…");
        EnsureForeground(hMain);

        int helpX = -1;
        for (int t = 0; t < 2 && helpX < 0; t++)      // 定位菜单栏最多试 2 轮
            helpX = FindHelpMenuX(hMain, pid, L);
        if (helpX < 0) { L("! 未能定位菜单栏（窗口可能被遮挡）"); return false; }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            EnsureForeground(hMain);
            RECT rr; GetWindowRect(hMain, out rr);
            RealClick(helpX, rr.Top + 54);
            Thread.Sleep(380);
            if (!PopupOnScreen(pid)) { L("  第 " + attempt + " 次：帮助菜单没打开"); continue; }

            if (attempt == 1) Key(VK_DOWN, 6);                    // 第 6 项 = 注册
            else { Key(VK_DOWN, 20); Key(VK_UP, 3); }             // 兜底

            Thread.Sleep(180);
            Key(VK_RETURN);

            for (int i = 0; i < 8; i++)                           // 最多 1.6 秒等窗口出现
            {
                Thread.Sleep(200);
                IntPtr h = FindRegWindow(pid);
                if (h != IntPtr.Zero && IsWindowVisible(h)) { L("注册窗口已打开"); return true; }
            }
            L("  第 " + attempt + " 种按键方式未命中");
        }

        Key(VK_ESCAPE); Key(VK_ESCAPE);
        L("! 没能打开注册窗口");
        return false;
    }

    /// <summary>检测激活状态。
    /// 实测（2026-10-11 截图确认）：已激活时注册窗口显示「非转售(NFR)版 + 键 NAVG-TPMS-YAXV-**** + 【取消激活】」；
    /// 【取消激活】是自绘的（类名 TPanel，读不到文字）→ **不能靠文本判断**。
    /// 可靠判据：已激活 ⇒ 窗口里没有任何密钥输入框（窄 TEdit = 0）；未激活 ⇒ 4~5 个窄 TEdit + 【激活】按钮。
    /// </summary>
    public static string CheckActivated(uint pid, Action<string> log)
    {
        Action<string> L = delegate(string m) { if (log != null) log(m); };

        // 只监控：注册窗口没打开就直接返回 NOREG（不再自动点菜单）
        IntPtr hRegNow = FindRegWindow(pid);
        if (hRegNow == IntPtr.Zero || !IsWindowVisible(hRegNow))
        {
            L("注册窗口未打开");
            return "NOREG";
        }

        for (int round = 0; round < 6; round++)     // 最多 ~1.5 秒
        {
            if (round > 0) Thread.Sleep(150);
            IntPtr hReg = FindRegWindow(pid);
            if (hReg == IntPtr.Zero || !IsWindowVisible(hReg)) continue;

            int narrowEdit = 0, buttons = 0;
            bool hasDeactByText = false;
            EnumChildWindows(hReg, delegate(IntPtr h, IntPtr l)
            {
                string cls = Cls(h);
                string txt = Txt(h);
                if (cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(h))
                {
                    RECT r; GetWindowRect(h, out r);
                    int w = r.Right - r.Left;
                    if (w >= 20 && w <= 110) narrowEdit++;
                }
                if (cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    buttons++;
                    if (txt.IndexOf("取消激活") >= 0) hasDeactByText = true;
                }
                return true;
            }, IntPtr.Zero);

            if (hasDeactByText) { L("检测到【取消激活】按钮 → 已激活"); return "ACTIVATED"; }
            if (narrowEdit >= 2) { L("检测到 " + narrowEdit + " 个密钥输入框 → 未激活"); return "NOT_ACTIVATED"; }

            RECT rw; GetWindowRect(hReg, out rw);
            int wWidth = rw.Right - rw.Left;
            if (round >= 3 && narrowEdit == 0 && buttons <= 3 && wWidth > 0 && wWidth < 640)
            {
                L("窗口 " + wWidth + "x" + (rw.Bottom - rw.Top) + "、无密钥输入框 → 已激活");
                return "ACTIVATED";
            }
        }
        L("界面结构与预期不符，无法判断激活状态");
        return "UNKNOWN";
    }

    /// <summary>把 16 位密钥填到注册窗口的密钥框（4 段等宽输入框），失败则整串填最左边那个。</summary>
    static bool FillLicenseKey(IntPtr hReg, string key)
    {
        key = (key ?? "").Replace("-", "").Trim();
        if (key.Length < 16) return false;

        var edits = new List<IntPtr>();
        EnumChildWindows(hReg, delegate(IntPtr h, IntPtr l)
        {
            if (Cls(h).IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(h))
                edits.Add(h);
            return true;
        }, IntPtr.Zero);
        if (edits.Count == 0) return false;

        var narrow = new List<IntPtr>();
        foreach (IntPtr e in edits)
        {
            RECT r; GetWindowRect(e, out r);
            int w = r.Right - r.Left;
            if (w >= 20 && w <= 110) narrow.Add(e);
        }
        narrow.Sort(delegate(IntPtr a, IntPtr b) { RECT ra, rb; GetWindowRect(a, out ra); GetWindowRect(b, out rb); return ra.Left.CompareTo(rb.Left); });

        IntPtr res;
        if (narrow.Count >= 4)
        {
            for (int i = 0; i < 4; i++)
            {
                SendMessageTimeout(narrow[i], WM_SETTEXT, IntPtr.Zero, key.Substring(i * 4, 4), SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
                Thread.Sleep(45);
            }
            return true;
        }

        IntPtr first = edits[0];
        foreach (IntPtr e in edits) { RECT ra, rb; GetWindowRect(e, out ra); GetWindowRect(first, out rb); if (ra.Left < rb.Left) first = e; }
        SendMessageTimeout(first, WM_SETTEXT, IntPtr.Zero, key, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
        Thread.Sleep(80);
        return true;
    }

    // ───────────── 主流程 ─────────────
    /// <summary>执行开始激活，返回空串=成功，返回 "__ACTIVATED__"=本来就已激活，其他=错误说明。</summary>
    /// <summary>该 Navicat 进程是否【早于补丁】启动（=内存里还是旧公钥，必须重启）。
    /// 判据：进程启动时间 &lt; 补丁相关文件（navicat.exe / libcf.dll）中较新的写入时间。
    /// 为什么需要它：patchedNow 只在"本次刚装补丁"时为真；若补丁早就装好，
    /// 而 Navicat 是补丁之前启动的，工具会误以为可以直接用 → 请求码仍用旧公钥 → 激活失败，
    /// 表现为"必须先关掉 Navicat 再点一次才能成功"。</summary>
    internal static bool NavicatPredatesPatch(Process p, string navDir)
    {
        try
        {
            DateTime patchTime = DateTime.MinValue;
            string[] fs = { "navicat.exe", "libcf.dll" };
            for (int i = 0; i < fs.Length; i++)
            {
                string full = System.IO.Path.Combine(navDir, fs[i]);
                if (System.IO.File.Exists(full))
                {
                    DateTime t = System.IO.File.GetLastWriteTime(full);
                    if (t > patchTime) patchTime = t;
                }
            }
            if (patchTime == DateTime.MinValue) return false;
            return p.StartTime < patchTime.AddSeconds(-3);   // 3 秒容差
        }
        catch { return false; }   // 取不到启动时间就不误判
    }

    public static string Run(string licKey, string navicatExe, string email, string company,
                             Action<string> log, out string req, out string code)
    {
        req = ""; code = "";
        Action<string> L = delegate(string s) { if (log != null) log(s); };

        // ── ① 定位 Navicat 安装目录 ──
        L("=== 第 1 步：定位 Navicat 安装目录 ===");
        string navDir = LocateDir(navicatExe);
        if (navDir == null)
            return "找不到 Navicat 安装目录。请先点「自动安装 Navicat」安装，或用「选择目录」指定正确目录后重试。";
        string navExeReal = System.IO.Path.Combine(navDir, "navicat.exe");
        L("Navicat 目录：" + navDir);

        // ── ② 检查/安装补丁（★ 必须在激活之前）──
        // 原因：激活要求 navicat.exe 里的"请求码公钥"已换成我们自己的，
        //       否则 Navicat 会用厂商公钥加密请求码，服务端私钥解不开。
        // 关于关闭 Navicat：只有"需要写文件装补丁"时才必须关（文件被占用写不进去）；
        //       补丁已经装过 → 就不检查、也不要求关闭，否则用户正开着 Navicat
        //       准备去点「帮助→注册」时会被拦住，导致根本没法激活。
        L("=== 第 2 步：检查/安装补丁（激活前必须完成）===");
        // ★ 按当前选择的激活方式判断补丁是否已装：
        //   libcf 方式   = 文件里公钥已换 + libcf.dll 有 .pmod 节；
        //   version.dll 方式 = 安装目录里有我们的代理 DLL（Navicat 文件保持原样）。
        bool hasPatch = PePatcher.IsInstalled(navDir);
        bool patchedNow = false;   // 本次是否刚刚装上的补丁（决定要不要做"预热重启"）

        if (hasPatch)
        {
            L("补丁已安装 → 不需要关闭 Navicat，跳过关闭检查。");
            // ★ 校验补丁是否还在：Navicat 自身/更新程序有可能把文件改回去，
            //   或代理 DLL 被删除；那时请求码又会用厂商公钥加密（现象与"没打补丁"一模一样）。
            if (!PePatcher.IsInstalled(navDir))
            {
                L("！补丁校验失败（文件可能被还原/覆盖，或代理 DLL 被删）→ 重新安装补丁");
                try
                {
                    PePatcher.Install(navDir, log);
                    patchedNow = true;
                    L("补丁已重新安装。");
                }
                catch (Exception ex) { return "重新安装补丁失败：" + ex.Message; }
            }
            else L("补丁公钥校验通过。");
        }
        else
        {
            Process[] running = Process.GetProcessesByName("navicat");
            if (running.Length > 0)
            {
                L("补丁尚未安装，而安装补丁必须关闭 Navicat。");
                L("检测到 Navicat 正在运行（" + running.Length + " 个进程）。");
                L("→ 请先手动关闭 Navicat，然后再点一次【开始激活】。");
                return "__NAVICAT_RUNNING__";
            }
            L("补丁尚未安装 → 现在安装（Navicat 未运行，可直接写入）。");
            try
            {
                PePatcher.Install(navDir, log);
                patchedNow = true;
                L("补丁安装完成。");
                Thread.Sleep(1200);            // 让补丁文件落盘（原 3000ms，实测 1.2 秒足够）
            }
            catch (Exception ex)
            {
                return "安装补丁失败：" + ex.Message + "（补丁没装上就无法激活）";
            }
        }

        // ── ③ 启动 Navicat（补丁版）──
        L("=== 第 3 步：准备 Navicat（需要你手动双击打开）===");
        // ★ 铁律（多轮实测）：只有【用户手动双击打开】的 Navicat 才能激活成功；
        //   工具自己启动的（Process.Start / explorer.exe <path> / 借 explorer 令牌降权）
        //   都会失败。所以这里不再自动启动，改为引导你双击，并自动检测、自动继续。
        Process[] procs = Process.GetProcessesByName("navicat");

        // ★ 不仅"本次刚装补丁"要重启，"当前进程早于补丁启动"同样要重启 ——
        //   否则它用内存里的旧公钥加密请求码，服务端解不开（表现为要点两次才成功）。
        bool staleProc = procs.Length > 0 && NavicatPredatesPatch(procs[0], navDir);

        if (procs.Length > 0 && !patchedNow && !staleProc)
        {
            L("Navicat 已在运行（权限：" + IntegrityOf((uint)procs[0].Id) + "），直接使用。");
        }
        else
        {
            if (procs.Length > 0)
            {
                if (staleProc)
                    L("当前 Navicat 是在打补丁【之前】启动的（内存里还是旧公钥）→ 需要重启一次。");
                else
                    L("本次刚安装补丁 → 需要 Navicat 重新加载一次（当前这个是补丁前的）。");
                L("##STEP##👉 请关闭 Navicat，再双击打开");
                L("→ 正在关闭当前 Navicat，请稍候手动双击打开…");
                CloseNavicatGracefully(L, 20000);
                Thread.Sleep(2000);
            }
            else
            {
                L("Navicat 没有运行。");
            }
            L("##STEP##👉 请双击打开 Navicat");
            L("→ 请【双击】打开 Navicat（工具会自动检测并继续，不用再点按钮）。");

            bool got = false;
            for (int i = 0; i < 800; i++)                 // 上限仍是 240 秒
            {
                Thread.Sleep(300);                        // 300ms 探测一次：打开后立刻继续，不再白等
                uint pidW = FirstNavicatPid();
                if (pidW != 0 && FindAnyVisibleTop(pidW) != IntPtr.Zero)
                {
                    L("✔ 检测到 Navicat 已打开（pid=" + pidW + "，权限 " + IntegrityOf(pidW) + "），自动继续。");
                    got = true;
                    break;
                }
                if (i % 15 == 14) L("（仍在等待你双击打开 Navicat…）");
            }
            if (!got)
                return "等待你手动打开 Navicat 超时。请双击打开 Navicat 后，再点一次【开始激活】。";
            Thread.Sleep(500);                        // 原 2000ms；后面 WaitNavicatReady 会轮询到就绪
            uint pidR = FirstNavicatPid();
            if (pidR != 0) WaitNavicatReady(pidR, L);
        }

        // 处理「试用提醒」等启动提示框：它是模态框，点它的【注册】就能打开注册界面
        {
            uint pidD = FirstNavicatPid();
            if (pidD != 0)
            {
                Thread.Sleep(800);
                if (HandleStartupDialogsWithRetry(pidD, L))
                    L("启动提示框已处理（点【注册】即可打开注册界面）。");
                else
                    L("没有发现启动提示框，继续。");
            }
        }

        // ★ 这里必须重新取一次：如果第 3 步开头 Navicat 没在运行，上面的 procs 会是空数组，
        //   而"不需要重启"的分支不会重新赋值 → procs[0] 直接抛 IndexOutOfRange（实测崩溃点）
        procs = Process.GetProcessesByName("navicat");
        if (procs.Length == 0)
            return "Navicat 进程不见了（可能刚被关闭），请手动打开 Navicat 后重试。";
        uint pid = (uint)procs[0].Id;

        L("=== 第 4 步：检查「注册」界面是否已打开 ===");
        IntPtr hReg = FindRegWindow(pid);
        bool regOk = hReg != IntPtr.Zero && IsWindowVisible(hReg);
        if (!regOk)
        {
            L("没有检测到「注册」窗口。");
            return "__NOREG__";
        }
        L("注册窗口已打开");

        // ── ③ 检查是否已激活 ──
        L("=== 第 5 步：检查是否已激活 ===");
        string st = CheckActivated(pid, log);
        if (st == "NOREG") return "__NOREG__";
        bool wasActivated = (st == "ACTIVATED");
        if (wasActivated)
        {
            // 已激活：跳过激活步骤，但仍然要检查/安装补丁（第 5 步）
            L("程序已经激活，跳过激活步骤（仍会检查补丁）。");
        }
        else
        {
        // ── ④ 激活流程 ──
        L("=== 第 6 步：开始激活 ===");
        EnsureForeground(hReg);

        IntPtr btnAct = FindChild(hReg, "TCloudButton", "激活");
        if (btnAct == IntPtr.Zero) btnAct = FindChild(hReg, "TButton", "激活");
        if (btnAct == IntPtr.Zero) return "注册界面里找不到【激活】按钮（界面与预期不一致）。";

        if (!string.IsNullOrEmpty(licKey))
        {
            if (FillLicenseKey(hReg, licKey))
            {
                L("已填入密钥，稍候…");
                Thread.Sleep(600);
            }
            else L("! 未能定位密钥输入框（将继续，但可能无法激活）");
        }

        // ══════════ 激活入口的两步点击（实测流程，缺一不可）══════════
        // ① 先【普通点击】激活：Navicat 会去连服务器校验 → 必弹错误框
        //    （自制密钥 → 报 90010003「激活次数上限」之类）
        // ② 关掉那个错误框
        // ③ 再【Ctrl+Shift 点击】激活：这才是"手动激活"的入口
        //    （Navicat 的隐藏快捷方式，打开 TManualActivationSubForm）
        L("正在点击【激活】（第 1 次，普通点击 → 会弹错误提示，稍后自动关掉）…");
        PlainClick(btnAct);   // ★必须用鼠标消息：激活按钮是 Delphi 自绘 TCloudButton，BM_CLICK 无效
        Thread.Sleep(600);

        // 等错误框出现并关掉它（最多 10 秒）
        bool errClosed = false;
        for (int i = 0; i < 66; i++)
        {
            Thread.Sleep(150);
            int c = CloseBlockingDialogs(pid, L);
            if (c > 0) { errClosed = true; break; }
        }
        if (errClosed) L("已关闭错误提示框，准备进入手动激活界面。");
        else L("（未检测到错误提示框，继续下一步）");
        Thread.Sleep(400);

        L("正在(模拟 Ctrl+Shift)点击【激活】（第 2 次 → 打开手动激活界面）…");
        ClickWithCtrlShift(btnAct);

        // ★ 点【激活】后 Navicat 通常先弹一个错误提示框（密钥无效/激活失败之类），
        //   必须把它关掉之后才会进入「手动激活」界面。实测：不关它就会一直卡住，
        //   而且工具会把弹框里的文本误当成请求码 → 服务端报"这段内容无法解密"。
        //   所以这里边等边清理弹框，等待时间也放宽到 12 秒。
        IntPtr hMan = IntPtr.Zero;
        bool closedAny = false;
        for (int i = 0; i < 60; i++)        // 最多 12 秒
        {
            Thread.Sleep(200);

            hMan = FindManWindow(pid);
            if (hMan != IntPtr.Zero && IsWindowVisible(hMan)) break;
            hMan = IntPtr.Zero;

            int c = CloseBlockingDialogs(pid, L);
            if (c > 0) { closedAny = true; Thread.Sleep(300); }   // 关掉了就多等一会儿
        }
        if (hMan == IntPtr.Zero)
            return "没能打开「手动激活」窗口（模拟点击未生效"
                 + (closedAny ? "；期间已自动关闭弹出的提示框，请重试一次" : "") + "）。";
        L("手动激活窗口已打开" + (closedAny ? "（已自动关闭 " + (closedAny ? "若干" : "0") + " 个提示框）" : ""));

        // 读请求码
        var memos = FindChildren(hMan, "Memo");
        if (memos.Count == 0) return "手动激活窗口里找不到输入框。";
        memos.Sort(delegate(IntPtr a, IntPtr b) { return TopOf(a).CompareTo(TopOf(b)); });

        IntPtr hReq = IntPtr.Zero, hCodeBox = IntPtr.Zero;
        string reqRaw = "";
        for (int attempt = 0; attempt < 12 && hReq == IntPtr.Zero; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(220);
                memos = FindChildren(hMan, "Memo");
                memos.Sort(delegate(IntPtr a, IntPtr b) { return TopOf(a).CompareTo(TopOf(b)); });
            }
            for (int i = 0; i < memos.Count; i++)
            {
                string t = FullText(memos[i]).Replace("\r", "").Replace("\n", "").Replace(" ", "");
                if (t.Length >= 200 && LooksB64(t)) { hReq = memos[i]; reqRaw = t; break; }
            }
        }
        if (hReq == IntPtr.Zero) return "没能在界面里认出「请求码」。";

        bool after = false;
        foreach (IntPtr m in memos)
        {
            if (after) { hCodeBox = m; break; }
            if (m == hReq) after = true;
        }
        if (hCodeBox == IntPtr.Zero) hCodeBox = hReq;
        req = reqRaw;
        L("已读到请求码（" + reqRaw.Length + " 字符）");

        string info;
        // ★ KeyGen.Make 失败时是【抛异常】而不是返回空值 —— 必须接住，
        //   否则下面的自愈分支永远不会执行（实测：自动重试因此没触发）。
        try { code = KeyGen.Make(reqRaw, email, company, out info); }
        catch (Exception ex) { info = ex.Message; code = ""; }
        if (string.IsNullOrEmpty(code))
        {
            // ★ 自愈：服务端说"不是本机 Navicat 的请求码"时，说明这个请求码是用【旧公钥】生成的，
            //   多半是补丁刚装好、Navicat 进程还没走到补丁逻辑（重启一次即正常）。
            //   与其让用户反复点，不如自动重启 Navicat 并引导他重新打开注册窗口。
            bool stale = info != null && (info.Contains("无法解密") || info.Contains("请求码")
                                          || info.Contains("签发失败"));
            if (stale)
            {
                L("! " + info);
                L("→ 这个请求码是用【旧公钥】生成的。请手动做一次：");
                L("   1) 关闭 Navicat    2) 用【双击】方式重新打开    3) 再点一次【开始激活】");
                L("   （工具自动重启出来的 Navicat 会是管理员权限，反而更失败，所以这里不自动重启）");
                return "__RESTARTED__";
            }
            return "生成激活码失败：" + info;
        }
        L("已生成激活码");

        IntPtr setRes;
        SendMessageTimeout(hCodeBox, WM_SETTEXT, IntPtr.Zero, code, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out setRes);
        Thread.Sleep(260);
        L("已把激活码填入界面");

        IntPtr btnGo = FindChild(hMan, "TButton", "激活");
        if (btnGo == IntPtr.Zero) btnGo = FindChild(hMan, "TCloudButton", "激活");
        if (btnGo == IntPtr.Zero) return "手动激活窗口里找不到【激活】按钮。";
        PlainClick(btnGo);
        L("已点击【激活】");

        // ★ 成功判据（实测修正）：不能只看"手动激活窗口关没关"——
        //   激活成功后窗口关闭需要更久，2 秒就下结论会误报"未被接受"（实际已成功）。
        //   首选判据 = 注册界面真的进入「已激活」状态；窗口关闭只作次选。
        bool okAct = false;
        for (int i = 0; i < 12; i++)         // 上限仍约 16 秒
        {
            Thread.Sleep(300);                // 探测更快：一确认成功就退出

            string actState = CheckActivated(pid, null);
            if (actState == "ACTIVATED") { L("✔ 注册界面已显示为已激活。"); okAct = true; break; }
            if (actState == "NOT_ACTIVATED") continue;      // 还没生效，继续等

            // 次选：手动激活窗口已经关掉了（且注册窗还在）
            IntPtr again = FindManWindow(pid);
            if (again == IntPtr.Zero || !IsWindowVisible(again)) { okAct = true; break; }
        }
        if (!okAct)
            return "已提交激活码，但没能自动确认结果——请手动看一眼注册界面："
                 + "若已看不到密钥输入框（或出现【取消激活】），就是激活成功。";

        L("激活流程完成");
        }   // ← end else（激活流程，未激活时才执行）

        return wasActivated ? "__ACTIVATED__" : "";
    }

    /// <summary>关掉所有 Navicat 进程后重新启动它（用于让补丁真正生效）</summary>
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int ret);

    /// <summary>查进程的完整性级别（诊断用：确认 explorer 启动的 Navicat 到底是不是普通权限）</summary>
    public static string IntegrityOf(uint pid)
    {
        try
        {
            IntPtr hProc = OpenProcess(0x0400, false, (int)pid);      // PROCESS_QUERY_INFORMATION
            if (hProc == IntPtr.Zero) return "未知";
            IntPtr tok;
            if (!OpenProcessToken(hProc, 0x0008, out tok)) { CloseHandle(hProc); return "未知"; }
            int len;
            GetTokenInformation(tok, 25, IntPtr.Zero, 0, out len);   // TokenIntegrityLevel
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(len);
            int ret;
            bool ok = GetTokenInformation(tok, 25, buf, len, out ret);
            CloseHandle(tok);
            string res = "未知";
            if (ok)
            {
                IntPtr sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                int n = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);
                // ★ 完整性 RID 是 4 字节整数（0x2000 小端存成 00 20 00 00），
                //   不能只读"最后一个字节"（那是 0x00）。
                int rid = System.Runtime.InteropServices.Marshal.ReadInt32(sid, 8 + 4 * n - 4);
                if (rid == 0x1000) res = "Low(低权限)";
                else if (rid == 0x2000) res = "Medium(普通用户 ★与手动双击一致)";
                else if (rid == 0x3000) res = "High(管理员 ★与手动双击不同)";
                else if (rid == 0x4000) res = "System";
                else res = "RID=0x" + rid.ToString("x");
            }
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            CloseHandle(hProc);
            return res;
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs,
        int impLevel, int tokType, out IntPtr newTok);
    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint flags, string app,
        string cmd, uint createFlags, IntPtr env, string cwd,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    const int SecurityImpersonation = 2, TokenPrimary = 1;

    /// <summary>用桌面 explorer.exe（普通权限）的令牌启动 Navicat。
    /// ★ 为什么必须这样：工具自身是管理员，Process.Start 或 explorer.exe 启动出来的
    ///   Navicat 都会是【管理员权限】（实测 High），而用户手动双击是 Medium。
    ///   两者权限不同时，Navicat 生成的请求码仍用厂商公钥 → 服务端报"无法解密"。</summary>
    public static bool StartAsMediumUser(string exe, string workDir, Action<string> L)
    {
        Process[] exps = Process.GetProcessesByName("explorer");
        if (exps.Length == 0) { L("（没有发现 explorer 进程）"); return false; }

        foreach (Process e in exps)
        {
            IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero;
            string lvl = "?";
            try
            {
                lvl = IntegrityOf((uint)e.Id);
                if (lvl.IndexOf("Medium") < 0) continue;          // 只借普通权限的 explorer

                IntPtr hProc = OpenProcess(0x0002 | 0x0008, false, e.Id);   // DUP|QUERY
                if (hProc == IntPtr.Zero) { L("（打不开 explorer 令牌，错误码 " + Marshal.GetLastWin32Error() + "）"); continue; }
                bool okTok = OpenProcessToken(hProc, TOKEN_ALL_ACCESS, out tok);
                CloseHandle(hProc);
                if (!okTok) { L("（OpenProcessToken 失败，错误码 " + Marshal.GetLastWin32Error() + "）"); continue; }
                if (!DuplicateTokenEx(tok, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup))
                { L("（DuplicateTokenEx 失败，错误码 " + Marshal.GetLastWin32Error() + "）"); continue; }

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                bool created = CreateProcessWithTokenW(dup, 0, exe, null, 0, IntPtr.Zero, workDir, ref si, out pi);
                int err = Marshal.GetLastWin32Error();
                if (created)
                {
                    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
                    L("已借 explorer(pid=" + e.Id + ", " + lvl + ") 的令牌以【普通用户权限】启动 Navicat。");
                    return true;
                }
                L("（CreateProcessWithTokenW 失败，错误码 " + err + "）");
            }
            catch (Exception ex) { L("（降权启动异常：" + ex.Message + "）"); }
            finally
            {
                if (tok != IntPtr.Zero) CloseHandle(tok);
                if (dup != IntPtr.Zero) CloseHandle(dup);
            }
        }
        return false;
    }

    /// <summary>等 Navicat 真正加载完成再返回。
    /// ★ 必须等：实测在 splash（About Navicat）阶段就点【注册】时，Navicat 还没走到
    ///   补丁逻辑，生成的请求码仍用厂商公钥 → 服务端报"这段内容无法解密"。
    ///   用户"手动关闭后重开能成功"，就是因为他等到完全加载完才点菜单。</summary>
    static void WaitNavicatReady(uint pid, Action<string> L)
    {
        bool sawMain = false;
        for (int i = 0; i < 180; i++)               // ① 最多 90 秒等主窗体被创建
        {
            Thread.Sleep(500);
            if (FindTop(pid, MainForm) != IntPtr.Zero) { sawMain = true; break; }
            if (i == 40) L("（Navicat 加载较慢，继续等待…）");
        }
        if (sawMain) L("Navicat 主窗体已创建，等启动画面收掉…");
        else L("Navicat 主窗体迟迟未出现（继续，后面还会重试）。");

        for (int i = 0; i < 60; i++)                // ② 最多 30 秒等 splash 消失
        {
            IntPtr hs = FindTop(pid, "TSplashForm");
            if (hs == IntPtr.Zero || !IsWindowVisible(hs)) break;
            Thread.Sleep(500);
        }
        // ③ 原为固定 Thread.Sleep(8000) 求稳，实测太慢。
        //    改成轮询"主窗口可见且未被模态框禁用"（=真正可用）就立刻继续，最多再等 6 秒。
        for (int i = 0; i < 20; i++)
        {
            IntPtr hM = FindTop(pid, MainForm);
            if (hM != IntPtr.Zero && IsWindowVisible(hM) && IsWindowEnabledSafe(hM)) break;
            Thread.Sleep(300);
        }
        L("Navicat 已加载完成。");
    }

    /// <summary>该进程有没有"像样的可见窗口"（不限主窗口）。
    /// ★ 必须这样判：Navicat 首次启动会弹「试用提醒」模态框，此时主窗口还不可见，
    ///   只认主窗口会白等到超时。</summary>
    static IntPtr FindAnyVisibleTop(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            string c = Cls(h);
            if (c == "TPUtilWindow" || c == "tooltips_class32") return true;
            if (c.StartsWith("IME") || c.IndexOf("SoPY", StringComparison.Ordinal) >= 0) return true;
            if (c.IndexOf("MSCTF", StringComparison.Ordinal) >= 0) return true;
            RECT r; GetWindowRect(h, out r);
            if (r.Right - r.Left < 120 || r.Bottom - r.Top < 80) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>轮询处理启动提示框（「试用提醒」等可能晚几秒才弹出来）</summary>
    static bool HandleStartupDialogsWithRetry(uint pid, Action<string> L)
    {
        for (int i = 0; i < 6; i++)                 // 最多 6 轮，约 4.8 秒（原 5x2s=10s）
        {
            if (HandleStartupDialogs(pid, L)) return true;
            Thread.Sleep(800);
        }
        return false;
    }

    /// <summary>pid 是不是 Navicat 进程</summary>
    static bool IsNavicatPid(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName.Equals("navicat", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>这个 TRegistrationSubForm 到底是「标准注册界面」还是「试用提醒」弹窗？
    /// ★ 两者共用同一个 Delphi 窗体类名（实测），只能靠内容区分：
    ///   · 标准注册界面：有「激活」按钮，且有多个密钥输入框
    ///   · 试用提醒：只有 在线购买/注册/试用/关闭，没有密钥输入框
    /// 返回 true = 是试用提醒（需要点掉它）。</summary>
    static bool IsTrialReminder(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        int edits = 0;
        bool hasActivate = false, hasTrial = false, hasLogin = false;
        EnumChildWindows(h, delegate(IntPtr c, IntPtr l)
        {
            string cc = Cls(c);
            if (cc.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(c))
            {
                RECT r; GetWindowRect(c, out r);
                int w = r.Right - r.Left;
                if (w >= 20 && w <= 110) edits++;           // 密钥输入框是窄的
            }
            if (cc.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string t = Txt(c);
                if (t.IndexOf("激活", StringComparison.Ordinal) >= 0) hasActivate = true;
                if (t.IndexOf("试用", StringComparison.Ordinal) >= 0) hasTrial = true;
                if (t.IndexOf("登录", StringComparison.Ordinal) >= 0) hasLogin = true;
            }
            return true;
        }, IntPtr.Zero);

        if (hasActivate || hasLogin || edits >= 2) return false;   // 标准注册界面
        if (hasTrial) return true;                                 // 明摆着的试用提醒
        return true;    // 兜底：既没有激活按钮也没有密钥框 → 按试用提醒处理（点注册是安全的）
    }

    /// <summary>按【无障碍名称】找可见的顶层窗口（不依赖 Delphi 类名）</summary>
    static IntPtr FindTopByUia(uint pid, params string[] keys)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            if (Cls(h) == MainForm) return true;
            if (Uia.IsTarget(h, keys)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>找「注册」窗口：Delphi 类名 或 无障碍名称，任一命中即可</summary>
    public static IntPtr FindRegWindow(uint pid)
    {
        string cls = RegForm;                                  // 用变量，避免被批量改名误伤
        IntPtr h = FindTop(pid, cls);
        // ★ 类名会被「试用提醒」复用，必须排除它，否则会把试用提醒误当成注册界面
        if (h != IntPtr.Zero && IsWindowVisible(h) && !IsTrialReminder(h)) return h;
        return FindTopByUia(pid, "注册", "Registration");
    }

    /// <summary>找「手动激活」窗口：Delphi 类名 或 无障碍名称，任一命中即可</summary>
    static IntPtr FindManWindow(uint pid)
    {
        string cls = ManForm;
        IntPtr h = FindTop(pid, cls);
        if (h != IntPtr.Zero && IsWindowVisible(h)) return h;
        return FindTopByUia(pid, "手动激活", "Manual Activation", "ManualActivation");
    }

    /// <summary>Navicat 关闭时可能弹「确认退出」，用无障碍 Invoke 点掉它</summary>
    static void AnswerExitConfirm(Action<string> L)
    {
        var wins = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != 0 && IsWindowVisible(h) && IsNavicatPid(p) && Cls(h) != MainForm) wins.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr w in wins)
        {
            foreach (string btn in new string[] { "退出", "是", "确定", "Yes", "Exit", "OK" })
            {
                IntPtr b = FindChild(w, "TButton", btn);
                if (b == IntPtr.Zero) b = FindChild(w, "TBitBtn", btn);
                if (b == IntPtr.Zero) b = FindChild(w, "Button", btn);
                if (b != IntPtr.Zero)
                {
                    IntPtr res;
                    SendMessageTimeout(b, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
                    L("（关掉了退出确认框：点了「" + btn + "」）");
                    return;
                }
            }
        }
    }

    /// <summary>只安装补丁（不启动 Navicat、不激活）。
    /// 返回 "" = 成功；"__NAVICAT_RUNNING__" = 需要先关闭 Navicat；其它 = 错误信息。</summary>
    public static string InstallPatchOnly(string navDir, string navicatExe, Action<string> log)
    {
        Action<string> L = delegate(string m) { if (log != null) log(m); };
        try
        {
            string dir = navDir;
            if (string.IsNullOrEmpty(dir) || !System.IO.File.Exists(System.IO.Path.Combine(dir, "navicat.exe")))
                dir = LocateDir(navicatExe);
            if (dir == null) return "找不到 Navicat 安装目录。请先用「选择目录」指定正确目录。";

            L("Navicat 目录：" + dir);

            Process[] running = Process.GetProcessesByName("navicat");
            if (running.Length > 0)
            {
                L("检测到 Navicat 正在运行（" + running.Length + " 个进程）。");
                L("→ 安装补丁必须关闭 Navicat（文件被占用写不进去）。");
                return "__NAVICAT_RUNNING__";
            }

            string exePath = System.IO.Path.Combine(dir, "navicat.exe");
            if (PePatcher.IsInstalled(dir))
            {
                L("补丁已安装且校验通过 → 无需重复安装。");
                return "";
            }

            L("开始安装补丁…");
            PePatcher.Install(dir, log);
            Thread.Sleep(1500);
            if (!PePatcher.IsInstalled(dir))
                return "补丁安装后校验失败（文件可能被占用或代理 DLL 未写入），请关闭 Navicat 后重试。";
            L("补丁安装完成，校验通过。");
            return "";
        }
        catch (Exception ex) { return "安装补丁失败：" + ex.Message; }
    }

    /// <summary>给界面轮询用的进度探测：
    /// 返回 "reg" = 注册界面已打开；"nav" = Navicat 已打开；"" = 还没打开。
    /// 顺带自动点掉「试用提醒」（它挡着主界面，点它的【注册】即可打开注册界面）。</summary>
    public static string WatchState(uint pid)
    {
        if (pid == 0) return "";
        try
        {
            IntPtr hReg = FindRegWindow(pid);
            if (hReg != IntPtr.Zero && IsWindowVisible(hReg)) return "reg";
        }
        catch { }
        try { HandleStartupDialogs(pid, delegate(string m) { }); } catch { }
        return "nav";
    }

    /// <summary>取第一个 Navicat 进程的 pid（没有则 0）</summary>
    public static uint FirstNavicatPid()
    {
        Process[] ps = Process.GetProcessesByName("navicat");
        return ps.Length > 0 ? (uint)ps[0].Id : 0;
    }

    /// <summary>按钮文本是不是「关掉这个提示框」类（绝不匹配「激活/注册/购买」）</summary>
    static bool IsDismissButton(string t)
    {
        if (string.IsNullOrEmpty(t)) return false;
        string u = t.Trim();
        string[] keys = { "取消", "关闭", "稍后", "以后", "跳过", "否", "Cancel", "Close", "Later", "Skip", "Remind" };
        foreach (string k in keys)
            if (u.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>按钮文本是不是「能把注册/激活界面打开」的按钮。
    /// ★ 实测：Navicat 首次运行的提示框按钮是【在线购买 / 注册 / 试用 / 关闭】——
    ///   点【注册】就会打开注册对话框，这正是我们要的入口。</summary>
    static bool IsOpenRegButton(string t)
    {
        if (string.IsNullOrEmpty(t)) return false;
        string u = t.Trim();
        if (u.IndexOf("激活", StringComparison.Ordinal) >= 0) return true;
        if (u.IndexOf("注册", StringComparison.Ordinal) >= 0) return true;
        if (u.IndexOf("Activate", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (u.IndexOf("Register", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>兼容旧名</summary>
    static bool IsActivateButton(string t) { return IsOpenRegButton(t); }

    /// <summary>窗口标题/正文是否像「激活/试用」提示（防止误判普通子窗口）</summary>
    static bool LooksLikeActivationPrompt(string t)
    {
        if (string.IsNullOrEmpty(t)) return false;
        string[] keys = { "激活", "试用", "注册", "购买", "许可", "授权",
                          "Activate", "Activation", "Trial", "License", "Subscribe", "Purchase" };
        foreach (string k in keys)
            if (t.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>静默清理「阻塞式弹框」：点【激活】后 Navicat 常先弹一个错误提示
    /// （密钥无效/激活失败之类），必须关掉它才会进入「手动激活」界面。
    /// 与 HandleStartupDialogs 的区别：不打印诊断、可高频调用（只在自己关了东西时才记一行）。</summary>
    static int CloseBlockingDialogs(uint pid, Action<string> L)
    {
        if (pid == 0) return 0;
        var tops = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) tops.Add(h);
            return true;
        }, IntPtr.Zero);

        int closed = 0;
        foreach (IntPtr h in tops)
        {
            string cls = Cls(h);
            if (cls == MainForm || cls == ManForm || cls == RegForm) continue;   // 这三类要留着

            RECT r; GetWindowRect(h, out r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w < 80 || ht < 50) continue;                                     // 尺寸过小忽略

            // 找一个"确定/OK/关闭/取消"类按钮点掉它
            IntPtr target = IntPtr.Zero;
            string[] want = new string[] { "确定", "OK", "Close", "关闭", "取消", "Cancel", "是", "Yes" };
            foreach (string txt in want)
            {
                IntPtr b = FindChild(h, "TButton", txt);
                if (b == IntPtr.Zero) b = FindChild(h, "TCloudButton", txt);
                if (b != IntPtr.Zero && IsWindowVisible(b)) { target = b; break; }
            }
            if (target == IntPtr.Zero)
            {
                // 退路：任意一个可见按钮
                var btns = FindChildren(h, "TButton");
                foreach (IntPtr b in btns) if (IsWindowVisible(b)) { target = b; break; }
            }

            string title = Txt(h);
            if (target != IntPtr.Zero)
            {
                L("→ 关闭提示框 [" + cls + " \"" + title + "\"]");
                PostMessage(target, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(250);
                closed++;
            }
            else
            {
                // 没有按钮就直接发关闭消息
                L("→ 收起提示框 [" + cls + " \"" + title + "\"]");
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(250);
                closed++;
            }
        }
        return closed;
    }

    /// <summary>诊断并处理 Navicat 的「首次运行 / 是否激活」提示框，返回是否发现过这类窗口。
    /// ★ 这个提示框没被处理掉时，Navicat 生成的请求码仍用【旧公钥】加密，
    ///   服务端必然报"这段内容无法解密"，激活必定失败。
    /// 处理策略：优先点【激活】按钮（直达注册界面），否则点【取消/关闭】把它收起来。</summary>
    static bool HandleStartupDialogs(uint pid, Action<string> L)
    {
        if (pid == 0) return false;
        var tops = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) tops.Add(h);
            return true;
        }, IntPtr.Zero);

        // ★ 调试输出统一在此拦掉：需要排查时把 ShowDiag 改成 true 即可。
        if (!ShowDiag) { Action<string> raw = L; L = delegate(string m) { if (m != null && m.StartsWith("[诊断]")) return; raw(m); }; }

        IntPtr hMain = FindTop(pid, MainForm);
        bool found = false;
        L("[诊断] 主窗口可见=" + (hMain != IntPtr.Zero && IsWindowVisible(hMain))
          + "  主窗口可用(未被模态框禁用)=" + IsWindowEnabledSafe(hMain));

        foreach (IntPtr h in tops)
        {
            string cls = Cls(h);
            string title = Txt(h);
            RECT r; GetWindowRect(h, out r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            bool dlg = GetWindow(h, GW_OWNER) != IntPtr.Zero;

            // 先把每个窗口都记下来（含跳过的），便于事后诊断
            string skip = null;
            if (cls == MainForm) skip = "主窗口";
            else if (w < 80 || ht < 50) skip = "尺寸过小";
            else if (cls == ManForm) skip = "手动激活窗口";
            else if (cls == RegForm)
            {
                // ★ 同一个类名有两种窗口：标准注册界面 vs 试用提醒弹窗（实测）
                if (IsTrialReminder(h)) skip = null;                  // 试用提醒 → 要处理
                else skip = "标准注册界面（保持不动）";
            }
            string uiaName = Uia.WindowName(h);
            L("[诊断] 窗口 " + cls + "  \"" + title + "\"  " + w + "x" + ht
              + (dlg ? "  [对话框]" : "")
              + (cls == RegForm ? (IsTrialReminder(h) ? "  [试用提醒]" : "  [标准注册界面]") : "")
              + (skip != null ? "  → 跳过（" + skip + "）" : "  → 参与判据"));
            if (uiaName.Length > 0 && uiaName != title)
                L("[诊断]   无障碍名称=\"" + uiaName + "\"  → 判为目标窗口=" + Uia.IsTarget(h, "注册", "手动激活", "激活", "注册", "Registration"));

            // 注册/手动激活窗口：dump 子控件结构（判断它是标准注册界面还是"首次运行提示"）
            if (cls == RegForm || cls == ManForm || cls == MainForm)
            {
                int edits = 0, btns = 0;
                var bt = new List<string>();
                EnumChildWindows(h, delegate(IntPtr c, IntPtr l)
                {
                    string cc = Cls(c);
                    if (cc.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 && IsWindowVisible(c)) edits++;
                    if (cc.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        btns++;
                        string tt = Txt(c).Trim();
                        if (tt.Length > 0) bt.Add(tt);
                    }
                    return true;
                }, IntPtr.Zero);
                L("[诊断]   子控件：可见输入框=" + edits + "  按钮数=" + btns
                  + (bt.Count > 0 ? "  按钮文本=" + string.Join(" / ", bt.ToArray()) : ""));
                var uiaBtns = Uia.ButtonNames(h, 200);
                if (uiaBtns.Count > 0)
                    L("[诊断]   无障碍按钮（仅供参考，可能串到同进程其它窗口）="
                      + string.Join(" / ", uiaBtns.ToArray()));
            }

            if (skip != null) continue;

            var btnList = new List<IntPtr>();
            btnList.AddRange(FindChildren(h, "TButton"));
            btnList.AddRange(FindChildren(h, "TBitBtn"));
            btnList.AddRange(FindChildren(h, "TcxButton"));
            var texts = new List<string>();
            foreach (IntPtr bb in btnList) texts.Add(Txt(bb).Trim());

            // 只在"像激活/试用提示"或"是带按钮的模态对话框"时才动手，避免误点 Navicat 子窗口
            bool prompt = LooksLikeActivationPrompt(title);
            foreach (string tx in texts) if (LooksLikeActivationPrompt(tx)) { prompt = true; break; }
            bool byDlg = dlg && btnList.Count > 0 && btnList.Count <= 3 && !IsWindowEnabledSafe(hMain);
            if (!prompt && !byDlg)
            {
                L("[诊断]   → 不像激活提示框（关键词未命中=" + prompt + " 对话框兜底=" + byDlg + "），不动它。");
                continue;
            }

            found = true;
            IntPtr hit = IntPtr.Zero;
            string hitText = "";
            for (int i = 0; i < btnList.Count; i++)
                if (IsActivateButton(texts[i])) { hit = btnList[i]; hitText = texts[i]; break; }
            if (hit == IntPtr.Zero)
                for (int i = 0; i < btnList.Count; i++)
                    if (IsDismissButton(texts[i])) { hit = btnList[i]; hitText = texts[i]; break; }

            if (hit == IntPtr.Zero)
            {
                L("[诊断]   → 没有可安全点击的按钮，保持原样（请手动处理这个提示框）。");
                continue;
            }

            L("→ 点击提示框按钮：\"" + hitText + "\"");
            IntPtr res;
            SendMessageTimeout(hit, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, MSG_TIMEOUT, out res);
            Thread.Sleep(2000);                              // 等界面响应（点「激活」会打开注册界面）
        }
        return found;
    }

    /// <summary>实测入口：先扫描，再真正调用一次 HandleStartupDialogs，最后再扫描一次。
    /// 用途：验证「试用提醒」能不能被识别、点【注册】后注册界面是否真的打开。</summary>
    public static string DlgTest(uint pid)
    {
        var sb = new StringBuilder();
        Action<string> L = delegate(string m) { sb.AppendLine(m); };
        if (pid == 0) return "NO_NAVICAT_PROCESS";
        sb.AppendLine("===== 处理前 =====");
        sb.AppendLine(ScanWindows(pid));
        sb.AppendLine("===== 调用 HandleStartupDialogs =====");
        bool found = false;
        try { found = HandleStartupDialogs(pid, L); }
        catch (Exception ex) { sb.AppendLine("EX " + ex.GetType().Name + " " + ex.Message); }
        sb.AppendLine("found=" + found);
        Thread.Sleep(2500);
        sb.AppendLine();
        sb.AppendLine("===== 处理后 =====");
        try
        {
            sb.AppendLine("注册窗口(类名)=" + FindRegWindow(pid));
            uint pid2 = FirstNavicatPid();
            IntPtr hReg = pid2 != 0 ? FindRegWindow(pid2) : IntPtr.Zero;
            sb.AppendLine("注册窗口可见=" + (hReg != IntPtr.Zero && IsWindowVisible(hReg)));
            if (hReg != IntPtr.Zero) sb.AppendLine(Uia.Describe(hReg, 40));
            sb.AppendLine(ScanWindows(pid2));
        }
        catch (Exception ex2) { sb.AppendLine("EX2 " + ex2.Message); }
        return sb.ToString();
    }

    /// <summary>只读扫描：列出该进程所有可见顶层窗口、按钮，以及它们会不会被"启动提示框"判据命中。
    /// 用于诊断（不做任何点击）。输出到 %TEMP%\nvwin.txt</summary>
    public static string ScanWindows(uint pid)
    {
        if (pid == 0) return "NO_NAVICAT_PROCESS";
        var tops = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid) tops.Add(h);
            return true;
        }, IntPtr.Zero);

        IntPtr hMain = FindTop(pid, MainForm);
        var sb = new StringBuilder();
        sb.AppendLine("pid=" + pid + "  顶层窗口数=" + tops.Count + "  进程权限=" + IntegrityOf(pid));
        sb.AppendLine("主窗口=" + hMain + "  可见=" + (hMain != IntPtr.Zero && IsWindowVisible(hMain))
                      + "  可用(未被子窗口禁用)=" + IsWindowEnabledSafe(hMain));
        sb.AppendLine();
        foreach (IntPtr h in tops)
        {
            string cls = Cls(h);
            RECT r; GetWindowRect(h, out r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            bool vis = IsWindowVisible(h);
            bool dlg = GetWindow(h, GW_OWNER) != IntPtr.Zero;
            sb.AppendLine("[" + cls + "]  标题=\"" + Txt(h) + "\"  " + w + "x" + ht
                          + "  可见=" + vis + "  是对话框=" + dlg);
            string un = Uia.WindowName(h);
            sb.AppendLine("   无障碍名称=\"" + un + "\"  判为目标窗口="
                          + Uia.IsTarget(h, "注册", "手动激活", "激活", "Registration"));
            var ubs = Uia.ButtonNames(h, 200);
            if (ubs.Count > 0) sb.AppendLine("   无障碍按钮=" + string.Join(" / ", ubs.ToArray()));
            if (cls == RegForm || cls == ManForm)
            {
                sb.AppendLine("   —— 完整无障碍元素清单（诊断「激活」按钮在无障碍树里长什么样）——");
                foreach (Uia.Node nd in Uia.Read(h, 400))
                {
                    if (nd.Name.Length == 0 && nd.Type.IndexOf("Edit") < 0) continue;
                    sb.AppendLine("     " + nd.Type + " \"" + nd.Name + "\" cls=" + nd.Class
                                  + (nd.Enabled ? "" : " [禁用]"));
                }
            }
            if (cls == MainForm || cls == RegForm || cls == ManForm) continue;
            if (!vis || w < 80 || ht < 50) { sb.AppendLine("   （不参与判据：被类名/可见性/尺寸过滤掉）"); continue; }

            var btns = new List<IntPtr>();
            btns.AddRange(FindChildren(h, "TButton"));
            btns.AddRange(FindChildren(h, "TBitBtn"));
            btns.AddRange(FindChildren(h, "TcxButton"));
            var texts = new List<string>();
            foreach (IntPtr b in btns) texts.Add(Txt(b).Trim());

            bool prompt = LooksLikeActivationPrompt(Txt(h));
            foreach (string tx in texts) if (LooksLikeActivationPrompt(tx)) { prompt = true; break; }
            bool byDlg = dlg && btns.Count > 0 && btns.Count <= 3 && !IsWindowEnabledSafe(hMain);
            sb.AppendLine("   ★ 会被当成'额外窗口'处理  提示框关键词命中=" + prompt + "  对话框兜底命中=" + byDlg);
            for (int i = 0; i < texts.Count; i++)
                sb.AppendLine("     按钮[" + i + "]=\"" + texts[i] + "\"  点激活=" + IsActivateButton(texts[i])
                              + "  点取消=" + IsDismissButton(texts[i]));
        }
        return sb.ToString();
    }

    /// <summary>安全版 IsWindowEnabled（句柄为 0 时返回 true，避免误判）</summary>
    static bool IsWindowEnabledSafe(IntPtr h)
    {
        if (h == IntPtr.Zero) return true;
        try { return IsWindowEnabled(h); } catch { return true; }
    }

    const uint WM_CLOSE = 0x0010;

    /// <summary>礼貌关闭所有 Navicat：先让它走自己的退出流程（写配置、清理状态），
    /// 超时才强杀。★ 实测：强杀会留下脏状态，用户"手动关闭"之后激活才成功。</summary>
    static void CloseNavicatGracefully(Action<string> L, int waitMs)
    {
        int asked = 0;
        foreach (Process pr in Process.GetProcessesByName("navicat"))
        {
            try
            {
                // ★ 必须发给真正的 TNavicatMainForm。
                //   Process.CloseMainWindow() 发的是 .NET 认定的主窗口，
                //   而 Navicat 那个隐藏的 TApplication(0x0) 窗口会被误认 → 主窗口收不到，只能强杀。
                IntPtr h = FindTop((uint)pr.Id, MainForm);
                if (h != IntPtr.Zero && IsWindowVisible(h))
                {
                    PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    asked++;
                }
                else if (pr.CloseMainWindow()) asked++;
            }
            catch { }
        }
        if (asked > 0) L("已请求 Navicat 正常退出（" + asked + " 个进程），等它自己关闭…");

        for (int i = 0; i < waitMs / 200; i++)
        {
            Thread.Sleep(200);
            if (i == 10 || i == 22 || i == 35) AnswerExitConfirm(L);   // 可能弹「确认退出」
            if (Process.GetProcessesByName("navicat").Length == 0)
            {
                if (asked > 0) L("Navicat 已正常退出。");
                return;
            }
        }
        int killed = 0;
        foreach (Process pr in Process.GetProcessesByName("navicat"))
        {
            try { pr.Kill(); pr.WaitForExit(8000); killed++; } catch { }
        }
        if (killed > 0) L("（有 " + killed + " 个进程没响应正常关闭，已强制结束）");
    }

    /// <summary>启动 Navicat（以【普通用户权限】启动，与用户手动双击等价）。
    /// 工具自己是管理员，直接 Process.Start 会让 Navicat 也拿到管理员权限，
    /// 与用户手动打开时的环境不一致 —— 实测手动打开的那个进程才能激活成功。
    /// 做法：优先借 explorer.exe 降权启动，失败再直接启动。</summary>
    static bool StartNavicat(string navDir, string navExe, Action<string> L)
    {
        // 说明：曾尝试"降权启动"（借 explorer 令牌 / explorer.exe <path>），
        // 实测都得不到普通权限 —— 而且用户手动双击打开的 Navicat 同样是管理员权限，
        // 说明权限并不是成功/失败的区分因素，因此这里直接启动即可。
        try
        {
            var psi = new ProcessStartInfo(navExe);
            psi.WorkingDirectory = navDir;
            Process.Start(psi);
            L("已启动 Navicat。");
        }
        catch (Exception ex) { L("! 启动 Navicat 失败：" + ex.Message); return false; }

        for (int i = 0; i < 150; i++)           // 最多 30 秒
        {
            Thread.Sleep(200);
            Process[] ps = Process.GetProcessesByName("navicat");
            if (ps.Length == 0) continue;
            uint pid = (uint)ps[0].Id;

            IntPtr hm = FindTop(pid, MainForm);
            if (hm != IntPtr.Zero && IsWindowVisible(hm))
            {
                L("Navicat 已启动（主窗口已就绪）。");
                L("Navicat 进程权限：" + IntegrityOf(pid));
                WaitNavicatReady(pid, L);
                return true;
            }

            // ★ 主窗口还被「试用提醒」模态框挡着时也算"起来了" —— 否则会白等到超时。
            //   但起来 ≠ 加载完：必须再调 WaitNavicatReady，否则会在 splash 阶段就去点按钮。
            IntPtr any = FindAnyVisibleTop(pid);
            if (any != IntPtr.Zero)
            {
                L("Navicat 已启动（当前界面：\"" + Txt(any) + "\"），等待加载完成…");
                L("Navicat 进程权限：" + IntegrityOf(pid));
                WaitNavicatReady(pid, L);
                return true;
            }
        }
        L("Navicat 已启动（未等到窗口，可能还在加载）。");
        return true;
    }

    /// <summary>正常关闭再重新启动 Navicat，并顺手处理掉启动提示框</summary>
    static void RestartNavicat(string navDir, string navExe, Action<string> L)
    {
        CloseNavicatGracefully(L, 12000);
        // ★ 关键：Navicat 刚退出时文件/映像缓存可能还没刷新，
        //   马上重启会让新进程仍加载到旧内容（用户实测："启动太快，补丁没生效"）。
        //   用户手动关闭再打开为什么能成功？因为他至少隔了 5-10 秒。
        // ★ 实测依据：用户"手动关闭后再打开"能成功，而工具此前只隔 1.5 秒。
        //   这里放宽到 15 秒，完整模拟"手动关闭 → 稍等 → 手动打开"。
        L("等待 15 秒（模拟手动关闭后再打开的时间间隔，这一步是补丁生效的关键）…");
        Thread.Sleep(15000);
        if (!StartNavicat(navDir, navExe, L)) return;

        uint pid = FirstNavicatPid();
        if (pid != 0)
        {
            Thread.Sleep(1500);
            HandleStartupDialogsWithRetry(pid, L);    // 重启后会重新弹「试用提醒」
        }
        L("Navicat 已重新启动。");
    }

    /// <summary>定位 Navicat 安装目录：调用方提示 → 运行中的进程 → 常见安装路径</summary>
    static string LocateDir(string hintExe)
    {
        try
        {
            if (!string.IsNullOrEmpty(hintExe))
            {
                string d0 = System.IO.Path.GetDirectoryName(hintExe.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(d0) && System.IO.File.Exists(System.IO.Path.Combine(d0, "navicat.exe")))
                    return d0;
            }
        }
        catch { }
        try
        {
            foreach (Process pp in Process.GetProcessesByName("navicat"))
            {
                try
                {
                    string f = pp.MainModule.FileName;
                    string d1 = System.IO.Path.GetDirectoryName(f);
                    if (!string.IsNullOrEmpty(d1) && System.IO.File.Exists(System.IO.Path.Combine(d1, "navicat.exe")))
                        return d1;
                }
                catch { }
            }
        }
        catch { }
        try
        {
            string[] guesses = new string[]
            {
                "C:\\Program Files\\Navicat\\Navicat Premium 18",
                "C:\\Program Files (x86)\\Navicat\\Navicat Premium 18",
                "D:\\Program Files\\Navicat\\Navicat Premium 18",
                "C:\\Program Files\\Navicat\\Navicat Premium 17",
                "D:\\Navicat\\Navicat Premium 18"
            };
            foreach (string g in guesses)
                if (System.IO.File.Exists(System.IO.Path.Combine(g, "navicat.exe"))) return g;
        }
        catch { }
        return null;
    }
}
