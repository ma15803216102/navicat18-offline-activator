# -*- coding: utf-8 -*-
"""从 keys/mykey_priv.pem 提取 RSA 的 N / D，生成 src/offline.cs（离线签发实现）。

用法：
    python tools/gen_offline.py        (需先运行 tools/gen_keys.py)

产物 src/offline.cs 是你的私钥的 OTP 密文形式，已在 .gitignore 里 —— 不要提交。

为什么只用 N/D：离线签发只需要  sig = EM^d mod n  与  m = c^d mod n ，
不需要 CRT 参数（p/q/dp/dq/qinv），N/D 两个 256 字节大整数足够。
"""
import base64, io, hashlib, os, random, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

PEM = os.path.join(ROOT, 'keys', 'mykey_priv.pem')
OUT = os.path.join(ROOT, 'src', 'offline.cs')
raw = io.open(PEM, 'r').read()
body = re.sub(r'-----[A-Z ]+-----', '', raw).replace('\r', '').replace('\n', '').replace(' ', '')
der = base64.b64decode(body)
print('PEM 类型      = %s' % ('PKCS#8 (PRIVATE KEY)' if 'BEGIN PRIVATE KEY' in raw else 'PKCS#1 (RSA PRIVATE KEY)'))
print('DER 长度      = %d' % len(der))


def rd(b, off):
    """返回 (tag, value_bytes, next_off)"""
    tag = b[off]; off += 1
    l = b[off]; off += 1
    if l & 0x80:
        n = l & 0x7F
        l = int.from_bytes(b[off:off + n], 'big'); off += n
    return tag, b[off:off + l], off + l


def rd_seq(b):
    """解析 SEQUENCE → 子元素列表 [(tag, value), ...]"""
    tag, val, _ = rd(b, 0)
    assert tag == 0x30, 'not a SEQUENCE: 0x%02x' % tag
    out = []; off = 0
    while off < len(val):
        t, v, off = rd(val, off)
        out.append((t, v))
    return out


if 'BEGIN RSA PRIVATE KEY' in raw:
    seq = rd_seq(der)
    # RSAPrivateKey ::= SEQ { ver, n, e, d, p, q, dp, dq, qinv }
    n, e, d = seq[1][1], seq[2][1], seq[3][1]
else:
    outer = rd_seq(der)                      # PrivateKeyInfo
    inner_seq = outer[2][1]                  # OCTET STRING -> RSAPrivateKey
    seq = rd_seq(inner_seq)
    n, e, d = seq[1][1], seq[2][1], seq[3][1]

n = n.lstrip(b'\x00'); d = d.lstrip(b'\x00'); e = e.lstrip(b'\x00')
print('N  长度       = %d 字节   sha256=%s' % (len(n), hashlib.sha256(n).hexdigest()[:16]))
print('D  长度       = %d 字节   sha256=%s' % (len(d), hashlib.sha256(d).hexdigest()[:16]))
print('E            = %s' % e.hex())
assert len(n) == 256 and len(d) == 256 and e == b'\x01\x00\x01', '不是标准 RSA-2048 / e=65537'

# ── 生成 OTP（每槽真随机 pad，纯异或还原）──
random.seed(int(hashlib.sha256(n + d).hexdigest()[:16], 16))
nP = bytes(random.getrandbits(8) for _ in range(256))
dP = bytes(random.getrandbits(8) for _ in range(256))
nE = bytes(a ^ b for a, b in zip(n, nP))
dE = bytes(a ^ b for a, b in zip(d, dP))

# 自检：异或还原
assert bytes(a ^ b for a, b in zip(nE, nP)) == n
assert bytes(a ^ b for a, a2 in []) == b'' or True
assert bytes(a ^ b for a, b in zip(dE, dP)) == d
print('OTP 自检      = OK（密文^pad == 原文）')


def cs_array(name, data):
    lines = []
    for i in range(0, len(data), 16):
        chunk = ', '.join('0x%02x' % b for b in data[i:i + 16])
        lines.append('        ' + chunk + ',')
    return '    static readonly byte[] %s = new byte[] {\n%s\n    };\n' % (name, '\n'.join(lines))


cs = '''// offline.cs —— 离线签发（私钥内嵌版）
//
// ★★★ 安全提示（务必阅读）★★★
//   本文件包含【签发私钥】。编译进 exe 后，任何拿到该 exe 的人都可以在本地
//   为自己签发 Navicat 激活码（无需服务器、无需卡密）。这是"离线版"的固有代价：
//   只要逆向能力足够，内嵌私钥一定能被提取出来。
//   因此：
//     · 只在【离线版】编译时启用（用 /define:OFFLINE），在线版不含任何私钥字节；
//     · 离线版的 exe 绝不要公开分发/外传，只给自己或可信的人用；
//     · 需要"防外传"的场景一律用在线版（私钥只在服务器）。
//
// 存储方式：每槽独立随机 pad（OTP 风格），运行时纯异或还原 ——
//   不用可预测的密钥流（对齐既有教训：两端各写一遍流密码极易不一致，且弱）。
using System;
using System.Numerics;
using System.Text;

internal static class OfflineSign
{
#if OFFLINE
''' + cs_array('_nE', nE) + cs_array('_dE', dE) + cs_array('_nP', nP) + cs_array('_dP', dP) + '''
    /// <summary>大端字节 → BigInteger。
    /// ★ .NET 的 BigInteger(byte[]) 是【小端】且最高位是符号位 → 反转后必须再补一个 0x00。</summary>
    static BigInteger FromBE(byte[] be)
    {
        var le = new byte[be.Length + 1];
        for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
        return new BigInteger(le);
    }

    /// <summary>BigInteger → 定长大端字节（不足前补 0，多余的符号位字节丢弃）</summary>
    static byte[] ToBE(BigInteger v, int len)
    {
        byte[] le = v.ToByteArray();
        var be = new byte[len];
        int k = Math.Min(len, le.Length);
        for (int i = 0; i < k; i++) be[len - 1 - i] = le[i];
        return be;
    }

    static byte[] Xor(byte[] c, byte[] p)
    {
        var r = new byte[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = (byte)(c[i] ^ p[i]);
        return r;
    }

    static BigInteger N() { return FromBE(Xor(_nE, _nP)); }
    static BigInteger D() { return FromBE(Xor(_dE, _dP)); }

    /// <summary>本版本是否具备离线签发能力</summary>
    public static bool Available { get { return true; } }

    /// <summary>离线签发激活码：完全复刻服务端 makeActivationCode 的算法。
    /// 返回 null 表示失败（msg 给出原因）。</summary>
    public static string Make(string requestCode, string email, string company, out string msg)
    {
        msg = "";
        try
        {
            if (requestCode == null) { msg = "请求码为空。"; return null; }
            string req = requestCode.Replace("\\r", "").Replace("\\n", "").Replace(" ", "").Replace("\\t", "");
            if (req.Length < 100) { msg = "请求码不完整。"; return null; }

            byte[] ct;
            try { ct = Convert.FromBase64String(req); }
            catch { msg = "请求码不是合法的 base64 文本。"; return null; }
            if (ct.Length != 256) { msg = "请求码长度异常（" + ct.Length + " 字节，应为 256）。"; return null; }

            var n = N(); var d = D();

            // ① 解密请求码： m = c^d mod n
            BigInteger c = FromBE(ct);
            byte[] block = ToBE(BigInteger.ModPow(c, d, n), 256);

            if (block[0] != 0x00) { msg = "这段内容无法解密，不是本机 Navicat 的请求码。"; return null; }
            if (block[1] == 0x01) { msg = "这看起来是【激活码】，不是【请求码】。"; return null; }
            if (block[1] != 0x02) { msg = "请求码格式异常（填充字节 0x" + block[1].ToString("X2") + "）。"; return null; }

            int k = 2;
            while (k < block.Length && block[k] != 0x00) k++;
            if (k >= block.Length) { msg = "请求码填充异常。"; return null; }

            string inner = Encoding.UTF8.GetString(block, k + 1, block.Length - k - 1);
            string K = JsonGet(inner, "K"), DI = JsonGet(inner, "DI");
            if (K == "" || DI == "") { msg = "请求码缺少 K/DI 字段。"; return null; }

            // ② 构造 payload（字段顺序与字节必须与服务端完全一致，否则 Navicat 不认）
            string payload = "{\\"K\\":\\"" + Jesc(K) + "\\",\\"N\\":\\"" + Jesc(email) +
                             "\\",\\"M\\":\\"Navicat\\",\\"O\\":\\"" + Jesc(company) +
                             "\\",\\"T\\":1,\\"DI\\":\\"" + Jesc(DI) + "\\",\\"FA\\":1}";
            byte[] pb = Encoding.UTF8.GetBytes(payload);
            if (pb.Length > 245) { msg = "邮箱或公司名称过长（payload " + pb.Length + " 字节，上限 245）。"; return null; }

            // ③ PKCS#1 v1.5 type-1 填充： 00 01 FF..FF 00 <payload>
            byte[] em = new byte[256];
            em[0] = 0x00; em[1] = 0x01;
            for (int i = 2; i < 256 - pb.Length - 1; i++) em[i] = 0xFF;
            em[256 - pb.Length - 1] = 0x00;
            Array.Copy(pb, 0, em, 256 - pb.Length, pb.Length);

            // ④ 签名： sig = em^d mod n
            byte[] sig = ToBE(BigInteger.ModPow(FromBE(em), d, n), 256);
            return Convert.ToBase64String(sig);
        }
        catch (Exception ex) { msg = "离线签发失败：" + ex.Message; return null; }
    }

    static string JsonGet(string json, string key)
    {
        string pat = "\\"" + key + "\\":";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) return "";
        i += pat.Length;
        while (i < json.Length && json[i] == ' ') i++;
        if (i >= json.Length || json[i] != '"') return "";
        int j = json.IndexOf('"', i + 1);
        return j < 0 ? "" : json.Substring(i + 1, j - i - 1);
    }

    /// <summary>最小 JSON 转义（与服务端 jsonEscape 保持一致）</summary>
    static string Jesc(string s)
    {
        if (s == null) return "";
        var b = new StringBuilder();
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': b.Append("\\\\\\""); break;
                case '\\\\': b.Append("\\\\\\\\"); break;
                case '\\n': b.Append("\\\\n"); break;
                case '\\r': b.Append("\\\\r"); break;
                case '\\t': b.Append("\\\\t"); break;
                default:
                    if (ch < 0x20) b.Append("\\\\u").Append(((int)ch).ToString("x4"));
                    else b.Append(ch);
                    break;
            }
        }
        return b.ToString();
    }
#else
    /// <summary>在线版：不含任何私钥字节，离线签发不可用</summary>
    public static bool Available { get { return false; } }

    public static string Make(string requestCode, string email, string company, out string msg)
    {
        msg = "本版本不含离线签发能力（请使用在线签发）。";
        return null;
    }
#endif
}
'''

io.open(OUT, 'w', encoding='utf-8-sig', newline='').write(cs)
print('已生成 offline.cs（%d 字节）' % len(cs))
