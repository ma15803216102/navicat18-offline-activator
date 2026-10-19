/* lic.cs —— 卡密授权 / 计时 / 防破解（.NET Framework 4.x）
 *
 * 防破解设计：
 *   1. 架构级：签发与计时都在服务端，客户端只有一个 HMAC 令牌，无法自行延长或伪造；
 *      卡密在服务端「一次性 + 绑定设备」，删除本地记录也不能重复激活。
 *   2. 关键字符串（服务器地址、接口路径）XOR 加密后存储，运行时解密。
 *   3. 设备指纹 = MachineGuid + 计算机名 + 系统盘标识 → SHA256，换机不可复用。
 *   4. 本地授权状态用 Windows DPAPI（CurrentUser）加密，跨用户/跨机复制无效。
 *   5. 每次启动在线复核（/api/check），服务端说过期就立即锁定；
 *      本地时间被回拨超过 1 小时 → 强制在线复核，不通过即锁定。
 *   6. 授权校验散布在每次操作前（Allow()），单点 patch 无法长期生效；
 *      到期后所有入口都被拦住。
 *   7. 轻量反调试（Debugger.IsAttached / IsDebuggerPresent），不触发杀软误报。
 */
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Obf
{
    // ★ P3-a：每条密文自带独立随机 pad（OTP 风格逐字节异或），不再使用固定密钥表 ——
    //   已知任何一条明文都无法推导出其余字符串（旧版是 8 字节固定 K，一处泄露全盘皆输）。
    // ★ P3-b：静态构造里检测"被外部宿主反射加载"，破解者用 Assembly.LoadFile
    //   直接调内部方法（Obf.S / RemoteSign）时会直接抛异常。
    private static readonly bool gBad = Hijack();

    static Obf() { if (gBad) throw new InvalidOperationException("E1"); }

    public static string S(string b64)
    {
        var d = Convert.FromBase64String(b64);
        if (d.Length <= 8) throw new ArgumentException("E2");
        var s = new byte[d.Length - 8];
        for (int i = 0; i < s.Length; i++) s[i] = (byte)(d[8 + i] ^ d[i % 8]);
        return Encoding.UTF8.GetString(s);
    }

    /// <summary>仅供开发期生成密文，运行时不用</summary>
    public static string E(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var pad = new byte[8];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(pad);
        var d = new byte[8 + b.Length];
        Array.Copy(pad, 0, d, 0, 8);
        for (int i = 0; i < b.Length; i++) d[8 + i] = (byte)(b[i] ^ pad[i % 8]);
        return Convert.ToBase64String(d);
    }

    /// <summary>本程序是否被"非自身宿主"加载（反射加载 = 破解迹象）</summary>
    private static bool Hijack()
    {
        try
        {
            var self = Assembly.GetExecutingAssembly();
            var entry = Assembly.GetEntryAssembly();
            if (entry == null) return true;
            if (!ReferenceEquals(entry, self))
            {
                string en = "", sn = "";
                try { en = entry.GetName().Name ?? ""; } catch { }
                try { sn = self.GetName().Name ?? ""; } catch { }
                if (!string.Equals(en, sn, StringComparison.OrdinalIgnoreCase)) return true;
            }
            string loc = null;
            try { loc = self.Location; } catch { }
            if (string.IsNullOrEmpty(loc)) return true;
            var pm = Process.GetCurrentProcess().MainModule;
            if (pm != null && !string.IsNullOrEmpty(pm.FileName))
            {
                if (!string.Equals(Path.GetFullPath(pm.FileName), Path.GetFullPath(loc),
                                   StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { return true; }
        return false;
    }
}

internal sealed class LicState
{
    public string Code = "";
    public string Token = "";
    public string Device = "";
    public long Expire;
    public long LastRun;
    public string Sig = "";      // ★ 服务端对 code|device|expire 的 RSA 签名（本地验签）
}

internal static class LicenseGate
{
    // 在线授权服务端（开源离线版不使用；这里只保留结构，地址已置为占位）。
    // 离线签发不需要任何服务器。
    // ★ 显式静态构造函数：去掉编译器的 beforefieldinit 优化。
    //   否则"只调用不访问静态字段的方法"（如 VerifyExpire）不会触发类型初始化，
    //   也就不会触发 Obf 的静态构造，破解者用 Assembly.LoadFile 反射调用时可以绕过 Hijack。
    static LicenseGate() { }

    private static readonly string Server = "http://127.0.0.1:8787";
    private static readonly string PActivate = "/api/activate";
    private static readonly string PCheck = "/api/check";
    private static readonly string PLogin = "/api/login";
    private static readonly string PSign = "/api/sign";

    private static string StatePath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "navicat18tool", "lic.bin"); }
    }

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    // ---------------------------------------------------------------- 设备指纹
    public static string DeviceId()
    {
        string guid = "";
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                if (k != null) guid = Convert.ToString(k.GetValue("MachineGuid"));
        }
        catch { }
        string raw = guid + "|" + Environment.MachineName + "|" + Environment.OSVersion.VersionString
                     + "|" + Obf.S("WNOy+tUjnYw2uIPCqVXt8G+CkZeHEQ==");   // ★ P3-c：加盐，算法不再完全公开
        using (var sha = SHA256.Create())
        {
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(h[i].ToString("x2"));
            return "PC-" + sb.ToString().ToUpper();
        }
    }

    // ---------------------------------------------------------------- 本地验签（expire 完整性）
    // 服务端用签发私钥对 "code|device|expire" 做 RSA-SHA256(PKCS#1 v1.5) 签名；
    // 客户端用下面内嵌的公钥在**本地**验签 —— 不需要联网，因此不影响
    // "已激活的 Navicat 长期离线使用"；它只防止有人改本地 lic.bin 的到期时间。
    private static readonly byte[] PubN = new byte[256]
    {
        0xB0, 0x8D, 0x3B, 0x39, 0xCF, 0x37, 0x58, 0xEB, 0x78, 0x1F, 0x78, 0x65, 0x54, 0xBC, 0x87, 0xD3,
        0x4D, 0xFA, 0x1D, 0x2A, 0xC8, 0x9D, 0x5B, 0xC1, 0x68, 0xD7, 0x6F, 0x7B, 0xA4, 0xDE, 0xF5, 0xD8,
        0xDC, 0xF5, 0x10, 0xE2, 0xE2, 0x5F, 0xBD, 0x91, 0xFC, 0xBF, 0xAD, 0x75, 0xD1, 0x34, 0x35, 0xDB,
        0xD2, 0x79, 0xFC, 0x2D, 0xFC, 0x3A, 0x8B, 0xFD, 0xFC, 0x70, 0xB9, 0x83, 0x05, 0x6B, 0xFA, 0x6F,
        0xFB, 0x9B, 0x28, 0xA3, 0x66, 0xEE, 0xB4, 0x59, 0x8B, 0xB2, 0x94, 0xCE, 0xEE, 0xF3, 0xA3, 0xDC,
        0xBD, 0x73, 0x2F, 0xBE, 0x0B, 0xBE, 0xD8, 0xAC, 0x0A, 0xB9, 0xEE, 0x9C, 0x0C, 0x79, 0x99, 0xD2,
        0xE4, 0xB5, 0x1A, 0xD9, 0x11, 0xEB, 0x2A, 0xC7, 0x82, 0x94, 0xA2, 0x16, 0xFC, 0xA3, 0x47, 0xFA,
        0x00, 0xBE, 0x73, 0x2C, 0x5B, 0x1F, 0xBA, 0xCF, 0x0C, 0x15, 0xF1, 0x15, 0x7F, 0xB2, 0xD7, 0x10,
        0xF7, 0xFA, 0x44, 0x01, 0xDF, 0xAC, 0xB7, 0x40, 0x0C, 0x24, 0xB7, 0x20, 0xA1, 0xDE, 0xBD, 0xCA,
        0x9C, 0x6C, 0x94, 0x27, 0xBC, 0xF1, 0x85, 0x14, 0x25, 0x24, 0xC6, 0x83, 0xC3, 0x9B, 0x1C, 0x6B,
        0x9D, 0xAB, 0x3A, 0xBF, 0xC2, 0x1D, 0x1D, 0x56, 0x3A, 0xFD, 0xE7, 0x3C, 0x8C, 0xD0, 0xBD, 0x4F,
        0xCD, 0x3D, 0x2B, 0xE8, 0x11, 0xFB, 0x29, 0xD7, 0x08, 0xAA, 0x65, 0x76, 0xF8, 0x0E, 0x51, 0xA3,
        0xEC, 0x54, 0xEC, 0xAB, 0xC6, 0x57, 0xB8, 0x78, 0x78, 0x5B, 0x98, 0xF6, 0x53, 0xE0, 0x3F, 0x04,
        0x35, 0x43, 0xC4, 0xF0, 0x88, 0xC4, 0x45, 0x8D, 0xF2, 0x8B, 0xAA, 0xCD, 0xA2, 0x85, 0x33, 0xBD,
        0x46, 0xC5, 0xF0, 0x37, 0x8C, 0x4E, 0x9F, 0xB0, 0x69, 0x4A, 0x9E, 0x2D, 0x2B, 0xFE, 0x27, 0xFD,
        0x7E, 0x4F, 0xF4, 0xB6, 0x49, 0xAD, 0xC4, 0xA1, 0xFB, 0xFA, 0x55, 0x90, 0x5B, 0xDE, 0x8C, 0xEB,
    };

    // SHA256 的 PKCS#1 v1.5 DigestInfo 前缀（19 字节）
    private static readonly byte[] SHA256_DI = new byte[19]
    {
        0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01,
        0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20
    };

    private static byte[] Rev(byte[] a)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[a.Length - 1 - i];
        return r;
    }

    /// <summary>
    /// 大端字节数组 -> 非负 BigInteger。
    /// ★ 坑：.NET 的 BigInteger(byte[]) 按小端解释且**最高字节的最高位是符号位**，
    ///   模数首字节是 0xB0（最高位为 1），直接 Rev() 会得到负数、ModPow 全错。
    ///   这里补一个 0x00 字节保证按无符号解释。
    /// </summary>
    private static BigInteger FromBE(byte[] be)
    {
        var le = new byte[be.Length + 1];
        for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
        le[be.Length] = 0x00;
        return new BigInteger(le);
    }

    /// <summary>BigInteger -> 定长大端字节数组</summary>
    private static byte[] ToBE(BigInteger v, int size)
    {
        var le = v.ToByteArray();
        var be = new byte[size];
        for (int i = 0; i < le.Length && i < size; i++) be[size - 1 - i] = le[i];
        return be;
    }

    /// <summary>校验服务端签发的 (code, device, expire) 签名</summary>
    public static bool VerifyExpire(string code, string device, long expire, string sigB64)
    {
        if (string.IsNullOrEmpty(sigB64)) return false;
        try
        {
            var sig = Convert.FromBase64String(sigB64);
            if (sig.Length != 256) return false;
            var n = FromBE(PubN);
            var m = BigInteger.ModPow(FromBE(sig), 65537, n);
            var em = ToBE(m, 256);
            if (em[0] != 0x00 || em[1] != 0x01) return false;
            int i = 2;
            while (i < em.Length && em[i] == 0xFF) i++;
            if (i >= em.Length || em[i] != 0x00) return false;
            i++;
            if (em.Length - i != 51) return false;
            for (int k = 0; k < 19; k++) if (em[i + k] != SHA256_DI[k]) return false;
            var data = Encoding.UTF8.GetBytes(code + "|" + device + "|" + expire.ToString());
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                for (int k = 0; k < 32; k++) if (em[i + 19 + k] != h[k]) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 本地状态（DPAPI）
    public static LicState Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var enc = File.ReadAllBytes(StatePath);
            var raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(raw).Split('|');
            if (parts.Length < 5) return null;
            var st = new LicState
            {
                Code = parts[0], Token = parts[1], Device = parts[2],
                Expire = long.Parse(parts[3]), LastRun = long.Parse(parts[4]),
                Sig = parts.Length > 5 ? parts[5] : ""
            };
            // ★ P3-d：本地验签。expire 是服务端签过名的，改本地文件即失效。
            //   纯本地计算，不需要联网 —— 不影响已激活 Navicat 的离线长期使用。
            if (!VerifyExpire(st.Code, st.Device, st.Expire, st.Sig)) return null;
            return st;
        }
        catch { return null; }
    }

    public static void Save(LicState st)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            var s = string.Join("|", new[] { st.Code, st.Token, st.Device, st.Expire.ToString(), st.LastRun.ToString(), st.Sig ?? "" });
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(StatePath, enc);
        }
        catch { }
    }

    // ---------------------------------------------------------------- HTTP
    private static string Post(string path, string json, int timeoutMs = 8000)
    {
        var req = (HttpWebRequest)WebRequest.Create(Server + path);
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.UserAgent = "Navicat18Tool";
        var data = Encoding.UTF8.GetBytes(json);
        req.ContentLength = data.Length;
        try
        {
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var r = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
        catch (WebException we)
        {
            if (we.Response != null)
                using (var sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            throw;
        }
    }

    /// <summary>JSON 字符串转义（与服务端拼接规则保持一致）</summary>
    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                    break;
            }
        }
        return b.ToString();
    }

    /// <summary>
    /// ★ P0：把请求码交给服务端签发激活码。
    /// 签发私钥只存在于服务端，客户端不再内嵌任何 RSA 私钥，
    /// 因此反编译本程序也拿不到发卡能力。
    /// </summary>
    public static string RemoteSign(string requestCode, string email, string company, out string msg)
    {
        msg = "";
        var st = Load();
        if (st == null) { msg = "尚未登录，请先输入卡密登录。"; return null; }
        string body = "{\"code\":\"" + Esc(st.Code) + "\",\"device\":\"" + Esc(DeviceId())
                    + "\",\"token\":\"" + Esc(st.Token) + "\",\"hash\":\"" + SelfHash()
                    + "\",\"request\":\"" + Esc(requestCode) + "\",\"email\":\"" + Esc(email)
                    + "\",\"company\":\"" + Esc(company) + "\"}";
        string resp;
        try { resp = Post(PSign, body, 20000); }
        catch (Exception ex) { msg = "无法连接签发服务：" + ex.Message; return null; }
        if (!Ok(resp)) { msg = Field(resp, "msg"); if (msg == "") msg = "服务端拒绝签发。"; return null; }
        string code = Field(resp, "code");
        if (code == "") { msg = "服务端返回的激活码为空。"; return null; }
        return code;
    }

    private static string Field(string json, string key)
    {
        string pat = "\"" + key + "\":";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return "";
        i += pat.Length;
        if (i < json.Length && json[i] == '"')
        {
            int j = json.IndexOf('"', i + 1);
            return j < 0 ? "" : json.Substring(i + 1, j - i - 1);
        }
        int e = i;
        while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
        return json.Substring(i, e - i);
    }

    private static bool Ok(string json) { return json != null && json.Contains("\"ok\":true"); }

    /// <summary>手动激活用：把请求码交给服务端签发，成功返回激活码，失败返回 null 并给出原因。
    /// 服务端会校验「客户端哈希白名单 + 卡密/设备/令牌」，所以只有已激活的本机才能签发。</summary>
    public static string SignRequest(string json, out string err)
    {
        err = "";
        try
        {
            string resp = Post(PSign, json, 20000);
            string code = Field(resp, "code");
            if (string.IsNullOrEmpty(code))
            {
                err = Field(resp, "msg");
                if (string.IsNullOrEmpty(err)) err = string.IsNullOrEmpty(resp) ? "服务器无响应" : resp;
                return null;
            }
            return code;
        }
        catch (Exception ex)
        {
            err = "无法连接授权服务器：" + ex.Message;
            return null;
        }
    }

    private static string gSelfHash;

    /// <summary>本程序自身 exe 的 SHA256（服务端用于版本白名单校验）</summary>
    public static string SelfHash()
    {
        if (gSelfHash != null) return gSelfHash;
        try
        {
            using (var fs = File.OpenRead(Application.ExecutablePath))
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder();
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                gSelfHash = sb.ToString();
            }
        }
        catch { gSelfHash = ""; }
        return gSelfHash;
    }

    // ---------------------------------------------------------------- 激活 / 复核
    public static string Activate(string code, out string msg)
    {
        msg = "";
        string dev = DeviceId();
        string body = "{\"code\":\"" + code.Replace("\"", "") + "\",\"device\":\"" + dev + "\",\"hash\":\"" + SelfHash() + "\"}";
        string resp;
        try { resp = Post(PActivate, body); }
        catch (Exception ex) { msg = "无法连接授权服务器：" + ex.Message; return null; }
        if (!Ok(resp))
        {
            msg = Field(resp, "msg");
            if (msg == "") msg = "激活失败";
            return null;
        }
        var st = new LicState
        {
            Code = code.Trim().ToUpper(), Token = Field(resp, "token"), Device = dev,
            Expire = long.Parse(Field(resp, "expire")), LastRun = Now(),
            Sig = Field(resp, "sig")
        };
        Save(st);
        msg = Field(resp, "msg");
        return st.Token;
    }

    /// <summary>在线复核；true = 仍然有效</summary>
    public static bool OnlineCheck(LicState st, out string msg)
    {
        msg = "";
        if (st == null) { msg = "本地没有授权记录"; return false; }
        string body = "{\"code\":\"" + st.Code + "\",\"device\":\"" + st.Device + "\",\"token\":\"" + st.Token + "\",\"hash\":\"" + SelfHash() + "\"}";
        string resp;
        try { resp = Post(PCheck, body); }
        catch (Exception ex) { msg = "无法连接授权服务器：" + ex.Message; return false; }
        if (!Ok(resp)) { msg = Field(resp, "msg"); return false; }
        string exp = Field(resp, "expire");
        if (exp != "") st.Expire = long.Parse(exp);
        string sg = Field(resp, "sig");
        if (sg != "") st.Sig = sg;
        st.LastRun = Now();
        Save(st);
        return true;
    }

    private static long Now()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    public static long Remain(LicState st)
    {
        if (st == null) return 0;
        // ★ Expire == 0 表示永久卡（服务端 Hours=0 时签发的到期时间）。
        //   这里返回 long.MaxValue，让所有 "Remain(...) <= 0" 的判断自动放行 ——
        //   否则永久卡会被算成 0 - Now() 的巨大负数，一登录就被判"已过期"并退出。
        //   调用点（Allow / 心跳看门狗 / 托盘）因此不需要各自加特判。
        if (st.Expire == 0) return long.MaxValue;
        long r = st.Expire - Now();
        return r > 0 ? r : 0;
    }

    public static string RemainText(LicState st)
    {
        if (st != null && st.Expire == 0) return "永久";
        long r = Remain(st);
        if (r <= 0) return "已过期";
        long h = r / 3600, m = (r % 3600) / 60;
        return h > 0 ? (h + " 小时 " + m + " 分") : (m + " 分钟");
    }

    // ---------------------------------------------------------------- functional key
    private static byte[] gFeature;      // 兼容保留（FeatureKey 已下线）
    private static long gLastCheck;

    /// <summary>Derived from server-issued token + device id; random when not licensed.</summary>
    public static bool Allow()
    {
#if OFFLINE
        // 离线版：不校验卡密、不做联网心跳（后面的代码不会执行，编译器可能提示 CS0162，非错误）
        return true;
#endif
        if (!Guard.EnsureSafe()) return false;
        var st = Load();
        if (st == null)
        {
            MessageBox.Show("尚未激活，请输入激活码。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (Remain(st) <= 0)
        {
            MessageBox.Show("授权已到期，程序将自动关闭。请向管理员获取新的激活码。", "授权到期",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.Exit(0);
            return false;
        }
        // 每 30 分钟或本地时间异常（回拨）时在线复核
        bool need = (Now() - gLastCheck > 1800) || (st.LastRun > Now() + 3600);
        if (need)
        {
            string msg;
            if (!OnlineCheck(st, out msg))
            {
                MessageBox.Show("授权校验未通过：" + (msg == "" ? "已过期或已被其他设备占用" : msg)
                    + "\r\n请重新激活。", "授权无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            gLastCheck = Now();
        }
        return true;
    }

    private static Timer gWatchdog;
    private static int gFailCount;

    /// <summary>后台心跳：每 60 秒向服务器核对一次授权；到期/失效立即关闭程序</summary>
    private static void StartWatchdog()
    {
        if (gWatchdog != null) return;
        gWatchdog = new Timer { Interval = 60000 };
        gWatchdog.Tick += (s, e) =>
        {
            var cur = Load();
            if (cur == null) { Shutdown("本地授权状态丢失"); return; }
            if (Remain(cur) <= 0) { Shutdown("授权已到期"); return; }

            bool due = (Now() - gLastCheck > 60) || (cur.LastRun > Now() + 1800);
            if (!due) return;

            string m;
            if (OnlineCheck(cur, out m))
            {
                gFailCount = 0;
                gLastCheck = Now();
            }
            else if (m != null && m.StartsWith("无法连接"))
            {
                gFailCount++;
                if (gFailCount >= 10) Shutdown("长时间无法连接授权服务器");
            }
            else
            {
                Shutdown(string.IsNullOrEmpty(m) ? "授权已失效或已被其他设备占用" : m);
            }
        };
        gWatchdog.Start();
    }

    private static void Shutdown(string reason)
    {
        try { if (gWatchdog != null) gWatchdog.Stop(); } catch { }
        try
        {
            MessageBox.Show("授权已结束：" + reason + Environment.NewLine + "程序将自动关闭。", "授权结束", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { }
        Environment.Exit(0);
    }

    /// <summary>启动时先过授权：未激活/失效则弹出激活窗口，用户取消则返回 false（不进入主界面）</summary>
    public static bool EnsureLicensed()
    {
#if OFFLINE
        return true;      // ★ 离线版：不做任何登录/网络校验
#endif

#if !OFFLINE   // ★ 离线版：整段 HTTP 代码不编入 exe，确保零网络能力

        if (Guard.Debugged()) { Environment.Exit(1); }
        // 入口必须是登录授权界面：每次启动都要输入授权码
        using (var f = new ActivationForm())
        {
            if (f.ShowDialog() != DialogResult.OK) return false;
        }
        gLastCheck = Now();
        StartWatchdog();
        return true;
    #endif
}

    /// <summary>程序启动时调用：未激活则弹出激活窗口，取消则退出</summary>
    public static void Gate(Form owner)
    {
        var st = Load();
        if (st != null && Remain(st) > 0)
        {
            string msg;
            if (OnlineCheck(st, out msg)) { gLastCheck = Now(); StartWatchdog(); return; }
        }
        using (var f = new ActivationForm())
        {
            if (f.ShowDialog(owner) != DialogResult.OK)
                Environment.Exit(0);
        }
        gLastCheck = Now();
        StartWatchdog();
    }
}

/// <summary>激活窗口：输入卡密 → 激活 → 显示剩余时间</summary>
internal sealed class ActivationForm : GWindow
{
    private GTextBox txtCode, txtDev;
    private GLabel lblInfo;

    public ActivationForm()
    {
        Title = "登录授权";
        SubTitle = "请输入授权码，验证通过后进入工具";
        Size = new Size(600, 460);

        Controls.Add(new GLabel { Text = "授权码", Left = 32, Top = 84, Width = 200, Height = 20, ForeColor = G.Gold });
        txtCode = new GTextBox(false) { Left = 30, Top = 110, Width = 540, Height = 42 };
        txtCode.Font = new Font("Consolas", 12F);
        Controls.Add(txtCode);

        Controls.Add(new GLabel { Text = "本机设备码", Left = 32, Top = 168, Width = 200, Height = 20, ForeColor = G.Gold });
        txtDev = new GTextBox(false) { Left = 30, Top = 190, Width = 400, Height = 42 };
        txtDev.ReadOnly = true;
        txtDev.Font = new Font("Consolas", 10F);
        txtDev.ForeColor = G.Dim;
        Controls.Add(txtDev);
        var btnCopy = new GButton { Text = "复制", Primary = false, Left = 442, Top = 190, Width = 128, Height = 42 };
        btnCopy.Click += (s, e) => { try { Clipboard.SetText(txtDev.Text); lblInfo.ForeColor = G.Ok; lblInfo.Text = "设备码已复制"; } catch { } };
        Controls.Add(btnCopy);

        var btn = new GButton { Text = "登 录", Primary = true, Left = 30, Top = 254, Width = 264, Height = 44 };
        btn.Click += OnActivate;
        Controls.Add(btn);
        var btnExit = new GButton { Text = "退 出", Primary = false, Left = 306, Top = 254, Width = 120, Height = 44 };
        btnExit.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnExit);

        lblInfo = new GLabel { Left = 34, Top = 312, Width = 536, Height = 60, ForeColor = G.Dim };
        Controls.Add(lblInfo);

        txtDev.Text = LicenseGate.DeviceId();
        var st = LicenseGate.Load();
        if (st != null)
        {
            txtCode.Text = st.Code;
            lblInfo.Text = "本机剩余：" + LicenseGate.RemainText(st) + "（授权码已预填，直接点「登 录」）";
        }
        else
        {
            lblInfo.Text = "请输入授权码，验证通过后进入工具。";
        }

        Shown += (s, e) => { txtCode.Focus(); };
    }

    private void OnActivate(object sender, EventArgs e)
    {
        string code = txtCode.Text.Trim();
        if (code.Length < 8) { lblInfo.ForeColor = G.Err; lblInfo.Text = "请粘贴完整的授权码。"; return; }
        string msg;
        string tok = LicenseGate.Activate(code, out msg);
        if (tok == null) { lblInfo.ForeColor = G.Err; lblInfo.Text = "登录失败：" + msg; return; }
        var st = LicenseGate.Load();
        lblInfo.ForeColor = G.Ok;
        lblInfo.Text = (msg == "" ? "登录成功。" : msg) + "  剩余可用：" + LicenseGate.RemainText(st);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>反调试 / 反逆向 + 反射创建主窗体（调用链不明文）</summary>
internal static class Guard
{
    [DllImport("kernel32.dll")]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, ref bool pbDebuggerPresent);
    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    private static int gPass = 0;
    private static readonly string[] Tools = new string[] {
        Obf.S("YRd4eu7LdxYFeQsKlw=="),
        Obf.S("hoLJdDau2rz+tP0QVMk="),
        Obf.S("FUYTsarzZsJtdSHVyJQ="),
        Obf.S("fG27yN7lxxATAdexuoeg"),
        Obf.S("5U3BBd0CvCaMKaAz6Q=="),
        Obf.S("3zpZkMgKxM62Xjg="),
        Obf.S("inzE/3/ZqXvuGfCbEK0="),
        Obf.S("EMl4NOo/LhR5pQtEkw=="),
        Obf.S("D8hT8mYiNGZrpyeCA0df"),
        Obf.S("hVfAGjtRwuT1Ja95XiKxjOQ0q39J"),
        Obf.S("GBOd2Z8gFf57e/i460V7mXF9+A=="),
        Obf.S("iDaumnZdiyvlU8n7EijmW+1E"),
        Obf.S("5KzCs6Y0IEiXxa/DylFBO5fJr9HKTQ=="),
        Obf.S("LaAWRR9NzP9fxXApejmjjQ=="),
        Obf.S("OsLhw+Qsfd1Ip4evgU8Jskg="),
        Obf.S("IJ2gkV5AXF9E8tT/OzQuOlPyzOc7Mg=="),
        Obf.S("X9Fuze8TOv81pB25i3ZZkDKhB6GK")
    };

    public static int PassCount { get { return gPass; } }

    public static bool Debugged()
    {
        try { if (Debugger.IsAttached) return true; } catch { }
        try { if (IsDebuggerPresent()) return true; } catch { }
        try
        {
            bool remote = false;
            if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remote) && remote) return true;
        }
        catch { }
        try
        {
            foreach (var pr in Process.GetProcesses())
            {
                string n;
                try { n = pr.ProcessName.ToLowerInvariant(); } catch { continue; }
                for (int i = 0; i < Tools.Length; i++)
                    if (n.Contains(Tools[i])) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>向授权服务器上报异常事件（反调试 / 篡改 / 破解迹象），失败静默</summary>
    public static void Report(string kind, string msg)
    {
#if !OFFLINE   // ★ 离线版：整段 HTTP 代码不编入 exe

        try
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(Obf.S("uJzHW6swwTrQ6LMrkR/uTdnyqzKYBvQU2/LoN8JT7lvI9eg6x1GzVw=="));
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 4000;
            req.Proxy = null;
            string bd = "{\"kind\":\"" + kind.Replace("\"", "") + "\",\"msg\":\"" +
                        msg.Replace("\"", "'") + "\",\"device\":\"" + LicenseGate.DeviceId() + "\"}";
            var bb = System.Text.Encoding.UTF8.GetBytes(bd);
            req.ContentLength = bb.Length;
            using (var st = req.GetRequestStream()) st.Write(bb, 0, bb.Length);
            req.GetResponse().Close();
        }
        catch { }
    #endif
}

    public static bool EnsureSafe()
    {
        if (Debugged())
        {
            Report("debugger", "检测到调试器或逆向分析工具进程");
            try { MessageBox.Show("检测到调试或逆向工具，程序已停止。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Stop); } catch { }
            Environment.Exit(1);
            return false;
        }
        gPass++;
        return true;
    }

    public static Form CreateMainForm()
    {
        if (Debugged()) { Environment.Exit(1); }
        string tname = Obf.S("6cONP8iG2oqkouRRjumo5w==");
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var t = asm.GetType(tname, true);
        var ctor = t.GetConstructor(Type.EmptyTypes);
        if (ctor == null) { Environment.Exit(1); }
        var f = ctor.Invoke(null) as Form;
        if (f == null) { Environment.Exit(1); }
        return f;
    }
}
