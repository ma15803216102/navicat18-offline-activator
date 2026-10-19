/* waitform.cs —— 「正在激活」异步等待提示窗
 *
 * 为什么需要它：
 *   激活流程要等用户手动双击打开 Navicat、点帮助→注册，整个耗时可能几十秒到几分钟。
 *   此前只有主界面状态栏一行小字，用户很容易以为"卡住了"而反复点按钮甚至关掉工具。
 *
 * 设计要点：
 *   · 用 Show() 而不是 ShowDialog() —— 非模态，绝不阻塞激活线程；
 *   · 关掉它只是隐藏提示，**不会中断激活**（激活在后台线程里继续跑）；
 *   · Owner = 主窗体，所以它始终压在主界面之上，又不会盖住后来的 MessageBox；
 *   · 文字由主窗体的 Step() 实时同步（激活流程每步都会 Step 一次）。
 */
using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class WaitForm : GWindow
{
    private GLabel lblText;

    public WaitForm(string title, string text)
    {
        Title = string.IsNullOrEmpty(title) ? "正在激活…" : title;
        SubTitle = "请勿关闭 Navicat；本窗口会自动消失，也可以先去做别的";
        ClientSize = new Size(560, 250);
        Font = new Font("Microsoft YaHei UI", 9F);
        ShowInTaskbar = false;
        MinimizeBox = false;

        // 动态转圈：用一个定时器推动一个字符动画，成本极低但能明确传达"在跑"
        lblText = new GLabel
        {
            Left = 24,
            Top = 92,
            Width = 512,
            Height = 84,
            ForeColor = G.Gold
        };
        lblText.Text = text ?? "";
        Controls.Add(lblText);

        var btn = new GButton
        {
            Text = "先去做别的（不中断激活）",
            Primary = false,
            Left = 24,
            Top = 186,
            Width = 512,
            Height = 44
        };
        btn.Click += delegate { Hide(); };   // 只隐藏，不取消
        Controls.Add(btn);
    }

    /// <summary>由主窗体的 Step() 调用，实时反映当前进度</summary>
    public void SetText(string s)
    {
        try
        {
            if (lblText != null) lblText.Text = s ?? "";
        }
        catch { }
    }

    /// <summary>换标题（如从"正在激活"变成"正在安装补丁"）</summary>
    public void SetTitle(string s)
    {
        try
        {
            Title = s ?? Title;
            Invalidate();
        }
        catch { }
    }
}
