/* manual.cs —— 手动激活（请求码 → 激活码）
 *
 * 用途：自动激活流程失败、或客户把请求码发过来时，手工完成一次签发。
 *   · 离线版（/define:OFFLINE）：本地签发（内置私钥，OfflineSign.Make）
 *   · 在线版：用自己的已激活卡密 + 令牌，向服务端换激活码（/api/sign）
 *
 * 入口：主界面【手动激活】按钮弹出；不点就完全不出现（默认隐藏）。
 *
 * ★ 邮箱 / 公司会写进激活码的 N / O 字段（Navicat 的注册信息里会显示），
 *   所以这里也提供输入框，默认取主界面第 2 步填过的值，允许在弹窗里改。
 */
using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class ManualActForm : GWindow
{
    private GTextBox txtReq, txtAct, txtEmail, txtCompany;
    private GLabel lblMsg;
    private GButton btnGen;

    // ★ 记住上一次填过的邮箱/公司。
    //   主界面没填时若把空值灌进来，"关掉窗口再打开"就会把刚填的内容清空；
    //   所以规则是：主界面有值 → 用主界面的；没有 → 沿用这里上次记住的。
    private static string s_lastEmail = "";
    private static string s_lastCompany = "";

    /// <summary>无参构造（留给自检用）</summary>
    public ManualActForm() : this("", "") { }

    /// <summary>email / company 由主界面带入（第 2 步填过的值）。
    /// 传空字符串表示"主界面没填"，此时不覆盖、沿用上次记住的值。</summary>
    public ManualActForm(string email, string company)
    {
        Title = "手动激活";
        SubTitle = "把 Navicat 的「请求码」粘进来 → 生成「激活码」→ 粘回 Navicat";
        ClientSize = new Size(760, 700);
        Font = new Font("Microsoft YaHei UI", 9F);

        int y = 84;

        // ── ① 请求码 ──
        Controls.Add(new GLabel
        {
            Text = "① 请求码（在 Navicat 的注册窗口里点「复制」得到的那一大段）",
            Left = 24, Top = y, Width = 712, Height = 20, ForeColor = G.Gold
        });
        y += 24;
        txtReq = new GTextBox(true) { Left = 24, Top = y, Width = 712, Height = 118 };
        txtReq.Font = new Font("Consolas", 9F);
        Controls.Add(txtReq);
        y += 128;

        // ── 邮箱 / 公司（写进激活码的 N / O 字段）──
        Controls.Add(new GLabel
        {
            Text = "② 邮箱（激活码的 N 字段，可留空）",
            Left = 24, Top = y, Width = 340, Height = 18, ForeColor = G.Gold
        });
        Controls.Add(new GLabel
        {
            Text = "③ 公司（激活码的 O 字段，可留空）",
            Left = 380, Top = y, Width = 356, Height = 18, ForeColor = G.Gold
        });
        y += 20;
        // ★ 主界面有值就复制过来；没有就不处理（沿用上次记住的，不拿空值覆盖）
        string em = string.IsNullOrEmpty(email) ? s_lastEmail : email.Trim();
        string co = string.IsNullOrEmpty(company) ? s_lastCompany : company.Trim();
        s_lastEmail = em;
        s_lastCompany = co;

        txtEmail = new GTextBox(false) { Left = 24, Top = y, Width = 340, Height = 34 };
        txtEmail.Text = em;
        Controls.Add(txtEmail);
        txtCompany = new GTextBox(false) { Left = 380, Top = y, Width = 356, Height = 34 };
        txtCompany.Text = co;
        Controls.Add(txtCompany);

        // 关窗时把用户最终填的内容记下来，供下次"主界面没填"时沿用
        FormClosed += delegate
        {
            try
            {
                s_lastEmail = (txtEmail.Text ?? "").Trim();
                s_lastCompany = (txtCompany.Text ?? "").Trim();
            }
            catch { }
        };
        y += 46;

        btnGen = new GButton { Text = "生成激活码", Primary = true, Left = 24, Top = y, Width = 300, Height = 44 };
        btnGen.Click += OnGen;
        Controls.Add(btnGen);

        var btnPaste = new GButton { Text = "粘贴请求码", Primary = false, Left = 336, Top = y, Width = 200, Height = 44 };
        btnPaste.Click += delegate
        {
            try { txtReq.Text = Clipboard.GetText(); Msg("已从剪贴板粘贴，点「生成激活码」。", G.Dim); }
            catch { Msg("读剪贴板失败，请手动粘贴（Ctrl+V）。", G.Err); }
        };
        Controls.Add(btnPaste);

        var btnClose = new GButton { Text = "关 闭", Primary = false, Left = 548, Top = y, Width = 188, Height = 44 };
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);
        y += 58;

        // ── ④ 激活码 ──
        Controls.Add(new GLabel
        {
            Text = "④ 激活码（生成后复制，粘回 Navicat 的注册窗口）",
            Left = 24, Top = y, Width = 712, Height = 20, ForeColor = G.Gold
        });
        y += 24;
        txtAct = new GTextBox(true) { Left = 24, Top = y, Width = 712, Height = 118, ReadOnly = true };
        txtAct.Font = new Font("Consolas", 9F);
        Controls.Add(txtAct);
        y += 128;

        var btnCopy = new GButton { Text = "复制激活码", Primary = true, Left = 24, Top = y, Width = 300, Height = 44 };
        btnCopy.Click += delegate
        {
            if (txtAct.Text == null || txtAct.Text.Length == 0) { Msg("还没有生成激活码。", G.Err); return; }
            try { Clipboard.SetText(txtAct.Text); Msg("已复制 —— 粘回 Navicat 的注册窗口即可。", G.Ok); }
            catch { Msg("复制失败，请手动选中复制。", G.Err); }
        };
        Controls.Add(btnCopy);

        lblMsg = new GLabel { Left = 340, Top = y + 6, Width = 396, Height = 44, ForeColor = G.Dim };
        Controls.Add(lblMsg);

        y += 56;
        Controls.Add(new GLabel
        {
            Text = "※ 邮箱/公司会写进激活码并在 Navicat 的注册信息里显示；留空则表示不填。",
            Left = 24, Top = y, Width = 712, Height = 18, ForeColor = G.Dim
        });
        Controls.Add(new GLabel
        {
            Text = "※ 一个请求码只能签一次；换了 Navicat 版本或重装后请求码会变，需要重新签。",
            Left = 24, Top = y + 20, Width = 712, Height = 18, ForeColor = G.Dim
        });
#if OFFLINE
        Controls.Add(new GLabel
        {
            Text = "※ 当前为离线版：激活码由本机内置私钥签发，不联网。",
            Left = 24, Top = y + 40, Width = 712, Height = 18, ForeColor = G.Dim
        });
#else
        Controls.Add(new GLabel
        {
            Text = "※ 当前为在线版：激活码由服务器用签发私钥生成（本机不含私钥）。",
            Left = 24, Top = y + 40, Width = 712, Height = 18, ForeColor = G.Dim
        });
#endif
    }

    private void Msg(string s, Color c)
    {
        lblMsg.ForeColor = c;
        lblMsg.Text = s;
    }

    private void OnGen(object s, EventArgs e)
    {
        string req = (txtReq.Text ?? "");
        req = req.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "").Trim();
        if (req.Length < 100)
        {
            Msg("请先粘贴完整的请求码（一大段字符，约 300 多字）。", G.Err);
            return;
        }
        // ★ 邮箱/公司：写进激活码的 N / O 字段
        string email = (txtEmail.Text ?? "").Trim();
        string company = (txtCompany.Text ?? "").Trim();

        btnGen.Enabled = false;
        try
        {
#if OFFLINE
            string m;
            string code = OfflineSign.Make(req, email, company, out m);
            if (string.IsNullOrEmpty(code))
            {
                Msg("生成失败：" + m, G.Err);
                return;
            }
            txtAct.Text = code;
            Msg("已生成（本机离线签发）。复制后粘回 Navicat。", G.Ok);
#else
            var st = LicenseGate.Load();
            if (st == null)
            {
                Msg("本机尚未激活，无法签发。请先用授权码登录。", G.Err);
                return;
            }
            string json = "{\"hash\":\"" + Esc(LicenseGate.SelfHash()) + "\""
                        + ",\"code\":\"" + Esc(st.Code) + "\""
                        + ",\"device\":\"" + Esc(st.Device) + "\""
                        + ",\"token\":\"" + Esc(st.Token) + "\""
                        + ",\"request\":\"" + Esc(req) + "\""
                        + ",\"email\":\"" + Esc(email) + "\""
                        + ",\"company\":\"" + Esc(company) + "\"}";
            string err = null;
            string code = LicenseGate.SignRequest(json, out err);
            if (string.IsNullOrEmpty(code))
            {
                Msg("签发失败：" + (string.IsNullOrEmpty(err) ? "无响应" : err), G.Err);
                return;
            }
            txtAct.Text = code;
            Msg("已生成（服务端签发）。复制后粘回 Navicat。", G.Ok);
#endif
        }
        catch (Exception ex)
        {
            Msg("出错了：" + ex.Message, G.Err);
        }
        finally
        {
            btnGen.Enabled = true;
        }
    }

    /// <summary>JSON 字符串转义（只处理会破坏 JSON 的字符；请求码是 base64 也用得上）</summary>
    private static string Esc(string s)
    {
        if (s == null) return "";
        var b = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') b.Append('\\').Append(c);
            else if (c < ' ') b.Append(' ');
            else b.Append(c);
        }
        return b.ToString();
    }
}
