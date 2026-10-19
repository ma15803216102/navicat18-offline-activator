/* genshin.cs - Genshin 风自绘控件库（深靛蓝 + 香槟金 + 八角切角 + 菱形装饰 + 金色扫光）
 * 与 dist/Navicat18Tool-Genshin-Prototype.html 完全一致的视觉语言。
 */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class G
{
    public static readonly Color Navy0 = Color.FromArgb(14, 22, 34);
    public static readonly Color Navy1 = Color.FromArgb(28, 43, 65);
    public static readonly Color Navy2 = Color.FromArgb(42, 62, 92);
    public static readonly Color Navy3 = Color.FromArgb(53, 73, 106);
    public static readonly Color Gold  = Color.FromArgb(211, 188, 142);
    public static readonly Color GoldLt = Color.FromArgb(243, 227, 195);
    public static readonly Color GoldDk = Color.FromArgb(184, 155, 94);
    public static readonly Color Cream = Color.FromArgb(236, 229, 216);
    public static readonly Color Dim   = Color.FromArgb(169, 163, 154);
    public static readonly Color Dim2  = Color.FromArgb(139, 155, 180);
    public static readonly Color Ok    = Color.FromArgb(94, 211, 168);
    public static readonly Color Err   = Color.FromArgb(255, 154, 139);
    public static readonly Color Input = Color.FromArgb(17, 27, 43);

    /// <summary>程序启动时调用一次：启用深色滚动条/通用控件主题</summary>
    public static void Dark()
    {
        try { SetPreferredAppMode(1); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    public static GraphicsPath Cut(Rectangle r, int c)
    {
        var p = new GraphicsPath();
        p.AddLine(r.X + c, r.Y, r.Right - c, r.Y);
        p.AddLine(r.Right - c, r.Y, r.Right, r.Y + c);
        p.AddLine(r.Right, r.Y + c, r.Right, r.Bottom - c);
        p.AddLine(r.Right, r.Bottom - c, r.Right - c, r.Bottom);
        p.AddLine(r.Right - c, r.Bottom, r.X + c, r.Bottom);
        p.AddLine(r.X + c, r.Bottom, r.X, r.Bottom - c);
        p.AddLine(r.X, r.Bottom - c, r.X, r.Y + c);
        p.AddLine(r.X, r.Y + c, r.X + c, r.Y);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Genshin 风窗口：切角 + 双层金边 + 渐变背景 + 星点 + 标题栏 + 拖拽移动 + 关闭按钮</summary>
internal class GWindow : Form
{
    public string Title = "";
    public string SubTitle = "";
    public string Badge = "";

    public GWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = G.Navy0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Region = new Region(G.Cut(new Rectangle(0, 0, Width, Height), 16));
        var btnClose = new GButton { Text = "✕", Primary = false, Left = ClientSize.Width - 50, Top = 10,
                                     Width = 34, Height = 26, TabStop = false };
        btnClose.Click += (s, ev) => Close();
        Controls.Add(btnClose);
        btnClose.BringToFront();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new LinearGradientBrush(ClientRectangle, G.Navy2, G.Navy0, 52f))
            g.FillRectangle(b, ClientRectangle);

        var stars = new[] { new Point(36, 26), new Point(110, 14), new Point(252, 30), new Point(430, 18), new Point(560, 40) };
        foreach (var s in stars)
            using (var sb = new SolidBrush(Color.FromArgb(60, G.GoldLt)))
                g.FillEllipse(sb, s.X, s.Y, 2, 2);

        using (var p1 = G.Cut(new Rectangle(0, 0, Width - 1, Height - 1), 16))
        using (var pen1 = new Pen(Color.FromArgb(118, G.Gold)))
            g.DrawPath(pen1, p1);
        using (var p2 = G.Cut(new Rectangle(6, 6, Width - 13, Height - 13), 12))
        using (var pen2 = new Pen(Color.FromArgb(52, G.Gold)))
            g.DrawPath(pen2, p2);

        // 标题（金渐变文字）
        if (Title != "")
        {
            using (var f = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold))
            {
                var w = (int)g.MeasureString(Title, f).Width + 12;
                using (var path = new GraphicsPath())
                {
                    path.AddString(Title, f.FontFamily, (int)FontStyle.Bold, f.SizeInPoints + 3,
                                   new PointF(62, 12), StringFormat.GenericDefault);
                    using (var br = new LinearGradientBrush(new Rectangle(58, 8, w, 34), G.GoldLt, G.GoldDk, 90f))
                        g.FillPath(br, path);
                }
            }
        }
        // 左上角：程序图标
        if (Icon != null)
        {
            try { g.DrawIcon(Icon, new Rectangle(16, 14, 34, 34)); } catch { }
        }
        if (SubTitle != "")
        {
            using (var f = new Font("Microsoft YaHei UI", 8.5f))
            using (var sb = new SolidBrush(G.Dim2))
                g.DrawString(SubTitle, f, sb, 64, 42);
        }
        // 倒计时徽标已按要求移除

        using (var pen = new Pen(Color.FromArgb(66, G.Gold)))
            g.DrawLine(pen, 20, 64, Width - 20, 64);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 64)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, int m, int w, int l);
}

/// <summary>Genshin 风按钮：Primary=香槟金渐变+扫光；否则=深蓝底+金边+金色字</summary>
internal class GButton : Control
{
    public bool Primary = true;
    private bool _hover;
    private int _shine = int.MinValue;
    private readonly Timer _tmr;

    public GButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        _tmr = new Timer { Interval = 14 };
        _tmr.Tick += (s, e) =>
        {
            _shine += 16;
            if (_shine > Width + 100) { _shine = int.MinValue; _tmr.Stop(); }
            Invalidate();
        };
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 6 && Height > 6)
            Region = new Region(G.Cut(new Rectangle(0, 0, Width, Height), 4));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        if (Primary) { _shine = -Width / 2; _tmr.Start(); }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false; _shine = int.MinValue; _tmr.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var path = G.Cut(r, 4);

        if (!Enabled)
        {
            using (var b = new SolidBrush(Color.FromArgb(90, G.Navy2)))
                g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(70, G.Gold)))
                g.DrawPath(pen, path);
        }
        else if (Primary)
        {
            using (var b = new LinearGradientBrush(r,
                _hover ? Color.FromArgb(255, 242, 214) : Color.FromArgb(247, 235, 209),
                _hover ? Color.FromArgb(218, 192, 138) : Color.FromArgb(192, 165, 114), 90f))
                g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(80, 43, 36, 21)))
                g.DrawPath(pen, path);

            if (_shine != int.MinValue)
            {
                using (var gp = new GraphicsPath())
                {
                    gp.AddPolygon(new[] { new Point(_shine, -2), new Point(_shine + 34, -2),
                                          new Point(_shine + 8, Height + 2), new Point(_shine - 26, Height + 2) });
                    using (var region = new Region(path))
                    {
                        g.Clip = region;
                        using (var sb = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                            g.FillPath(sb, gp);
                        g.ResetClip();
                    }
                }
            }
        }
        else
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(150, G.Navy3), Color.FromArgb(210, G.Navy0), 90f))
                g.FillPath(b, path);
            using (var pen = new Pen(_hover ? Color.FromArgb(230, G.Gold) : Color.FromArgb(105, G.Gold)))
                g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, r,
            !Enabled ? G.Dim : (Primary ? Color.FromArgb(43, 36, 21) : G.GoldLt),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Genshin 风输入框：深色底 + 金色边框 + 聚焦发光（多行可选）</summary>
internal class GTextBox : RichTextBox
{
    private readonly bool _multi;

    public GTextBox(bool multi)
    {
        _multi = multi;
        BorderStyle = BorderStyle.None;
        BackColor = G.Input;
        ForeColor = G.Cream;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        DetectUrls = false;
        if (multi)
        {
            ScrollBars = RichTextBoxScrollBars.Vertical;
            WordWrap = true;
        }
        else
        {
            ScrollBars = RichTextBoxScrollBars.None;
            WordWrap = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_multi && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return))
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { }
        Center();
        Pad();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Center();
        Pad();                 // Center() 会重置格式矩形，需重设
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Pad();
    }

    /// <summary>水平居中（RichTextBox 用 SelectionAlignment，多行/单行都支持）</summary>
    private void Center()
    {
        try
        {
            int s = SelectionStart, l = SelectionLength;
            SelectAll();
            SelectionAlignment = HorizontalAlignment.Center;
            SelectionStart = s;
            SelectionLength = l;
        }
        catch { }
    }

    /// <summary>垂直：单行居中，多行顶部对齐（EM_SETRECT）</summary>
    private void Pad()
    {
        try
        {
            RECT r;
            if (_multi)
            {
                r = new RECT { Left = 8, Top = 6, Right = Math.Max(24, Width - 8), Bottom = Math.Max(22, Height - 6) };
            }
            else
            {
                int lh = Font.Height;
                int top = Math.Max(2, (Height - lh) / 2);
                r = new RECT { Left = 8, Top = top, Right = Math.Max(24, Width - 8), Bottom = Math.Max(top + lh, Height - 2) };
            }
            SendMessage(Handle, 0x00B3, IntPtr.Zero, ref r);   // EM_SETRECT（0x00B2 是 EM_GETRECT）
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string subApp, string subId);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}

internal class GLabel : Label
{
    public GLabel()
    {
        BackColor = Color.Transparent;
        ForeColor = G.Gold;
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoSize = false;
    }
}
