// 无障碍（UI Automation / 微软官方辅助功能接口）读取窗口信息。
// 用途：不依赖 Delphi 的窗口类名，直接用"无障碍名称/控件类型"判断
//       「这是不是目标窗口」（注册 / 手动激活 / 激活提示），并可用
//       InvokePattern 做标准无障碍点击。
//
// 说明：Navicat 的主菜单栏是自绘的（无障碍树里没有 menuitem），
//       但对话框与按钮都是标准 VCL 控件 —— 无障碍能正确读到名称与类型。
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Automation;

internal static class Uia
{
    public sealed class Node
    {
        public string Name = "";
        public string Type = "";
        public string Class = "";
        public bool Enabled = true;
    }

    /// <summary>读窗口的无障碍子树（用 CacheRequest 一次取回，避免逐元素跨进程往返）</summary>
    public static List<Node> Read(IntPtr hwnd, int maxNodes)
    {
        var list = new List<Node>();
        if (hwnd == IntPtr.Zero) return list;
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return list;

            var cr = new CacheRequest();
            cr.Add(AutomationElement.NameProperty);
            cr.Add(AutomationElement.ControlTypeProperty);
            cr.Add(AutomationElement.ClassNameProperty);
            cr.Add(AutomationElement.IsEnabledProperty);
            cr.TreeScope = TreeScope.Element | TreeScope.Descendants;

            using (cr.Activate())
            {
                AutomationElementCollection all =
                    root.FindAll(TreeScope.Element | TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement e in all)
                {
                    if (list.Count >= maxNodes) break;
                    var n = new Node();
                    try
                    {
                        n.Name = e.Cached.Name == null ? "" : e.Cached.Name;
                        n.Type = e.Cached.ControlType == null ? "" : e.Cached.ControlType.ProgrammaticName;
                        n.Class = e.Cached.ClassName == null ? "" : e.Cached.ClassName;
                        n.Enabled = e.Cached.IsEnabled;
                    }
                    catch { }
                    list.Add(n);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>读窗口自身的无障碍名称（判断"是不是目标窗口"的首选依据）</summary>
    public static string WindowName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            AutomationElement el = AutomationElement.FromHandle(hwnd);
            if (el == null) return "";
            return el.Current.Name == null ? "" : el.Current.Name;
        }
        catch { return ""; }
    }

    /// <summary>窗口的无障碍名称里是否含任一关键字（= 是不是目标窗口）</summary>
    public static bool IsTarget(IntPtr hwnd, params string[] keys)
    {
        string n = WindowName(hwnd);
        if (n.Length == 0) return false;
        foreach (string k in keys)
            if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>列出窗口里所有按钮的无障碍名称</summary>
    public static List<string> ButtonNames(IntPtr hwnd, int maxNodes)
    {
        var res = new List<string>();
        foreach (Node n in Read(hwnd, maxNodes))
        {
            if (n.Type != null && n.Type.EndsWith("Button", StringComparison.Ordinal))
                if (n.Name.Length > 0) res.Add(n.Name);
        }
        return res;
    }

    /// <summary>按无障碍名称（子串）查找按钮并用 InvokePattern 点击。
    /// 这是无障碍标准动作，比 PostMessage(BM_CLICK) 更可靠（对方只需实现 UIA/IAccessible）。</summary>
    public static bool InvokeButton(IntPtr hwnd, string namePart)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            AutomationElementCollection btns = root.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement b in btns)
            {
                string nm = "";
                try { nm = b.Current.Name == null ? "" : b.Current.Name; } catch { }
                if (nm.Length == 0 || nm.IndexOf(namePart, StringComparison.Ordinal) < 0) continue;
                object pat;
                if (b.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
                {
                    ((InvokePattern)pat).Invoke();
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>窗口的无障碍摘要（诊断日志用）。只保留有名称或有意义的节点，避免刷屏。</summary>
    public static string Describe(IntPtr hwnd, int max)
    {
        var sb = new StringBuilder();
        var nodes = Read(hwnd, max);
        int shown = 0;
        foreach (Node n in nodes)
        {
            string t = n.Type == null ? "" : n.Type;
            if (t.IndexOf("Pane", StringComparison.Ordinal) >= 0) continue;
            if (t.IndexOf("Custom", StringComparison.Ordinal) >= 0) continue;
            if (n.Name.Length == 0 && t.IndexOf("Button", StringComparison.Ordinal) < 0) continue;
            sb.Append("UIA:").Append(t).Append(" \"").Append(n.Name).Append("\"");
            if (n.Class.Length > 0) sb.Append(" cls=").Append(n.Class);
            if (!n.Enabled) sb.Append(" [禁用]");
            sb.AppendLine();
            if (++shown >= max) break;
        }
        return sb.ToString();
    }
}
