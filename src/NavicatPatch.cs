/* Navicat 18 激活补丁（C#，.NET Framework 4.x 自带）—— 安装 / 卸载
 *
 * 作用：
 *   1) 在 libcf.dll 中新增一节 .pmod（补丁代码 + 我们自己的 2048 位验签模数），
 *      并把 RSA 解密函数入口改成相对跳转；运行时补丁只做纯内存拷贝，不调用任何 API；
 *   2) 把 navicat.exe 里的请求码公钥（PEM）替换成我们的，使请求码可用我们的私钥解析；
 *   3) 清理旧的 version.dll 劫持（如存在）。
 *
 * 编译：
 *   csc /target:exe /optimize+ /out:NavicatPatch.exe NavicatPatch.cs pdata.cs
 */
using System;
using System.IO;
using System.Text;

internal static class NavicatPatch
{
    private static int Align(int v, int a) { return (v + a - 1) / a * a; }
    private static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    private static ushort U16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }
    private static void P32(byte[] b, int o, uint v) { BitConverter.GetBytes(v).CopyTo(b, o); }

    /* 在“可执行节”范围内搜索函数序言，避免误匹配数据区 */
    private static int FindPrologue(byte[] d, byte[] pro)
    {
        int pe = (int)U32(d, 0x3C);
        int nsec = U16(d, pe + 6);
        int opt = pe + 24;
        int so = opt + U16(d, pe + 20);
        for (int s = 0; s < nsec; s++)
        {
            int o = so + s * 40;
            uint ch = U32(d, o + 36);
            if ((ch & 0x20000000u) == 0) continue;              /* 只搜可执行节 */
            int rsz = (int)U32(d, o + 16), raw = (int)U32(d, o + 20);
            int end = Math.Min(raw + rsz, d.Length) - pro.Length;
            for (int i = raw; i <= end; i++)
            {
                if (d[i] != pro[0] || d[i + 1] != pro[1] || d[i + 2] != pro[2] || d[i + 3] != pro[3]) continue;
                bool ok2 = true;
                for (int j = 4; j < pro.Length; j++) if (d[i + j] != pro[j]) { ok2 = false; break; }
                if (ok2) return i;
            }
        }
        return -1;
    }

    private static int FindPrologueOld(byte[] d, byte[] pro)
    {
        for (int i = 0; i <= d.Length - pro.Length; i++)
        {
            if (d[i] != pro[0] || d[i + 1] != pro[1] || d[i + 2] != pro[2] || d[i + 3] != pro[3]) continue;
            bool ok = true;
            for (int j = 4; j < pro.Length; j++) if (d[i + j] != pro[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static int FindPem(byte[] d)
    {
        byte[] m = Encoding.ASCII.GetBytes("-----BEGIN PUBLIC KEY-----");
        for (int i = 0; i <= d.Length - m.Length; i++)
        {
            if (d[i] != 0x2D || d[i + 1] != 0x2D || d[i + 2] != 0x2D) continue;
            bool ok = true;
            for (int j = 3; j < m.Length; j++) if (d[i + j] != m[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    /* 在 libcf.dll 中新增节并写入补丁；entryOff 是入口的文件偏移 */
    private static byte[] Apply(byte[] src, int entryOff)
    {
        int pe = (int)U32(src, 0x3C);
        int nsec = U16(src, pe + 6);
        int opt = pe + 24;
        ulong img = BitConverter.ToUInt64(src, opt + 24);
        int alignS = (int)U32(src, opt + 32);
        int alignF = (int)U32(src, opt + 36);
        int so = opt + U16(src, pe + 20);

        int lastVA = 0, lastVsz = 0, lastRaw = 0, lastRsz = 0;
        for (int i = 0; i < nsec; i++)
        {
            int o = so + i * 40;
            lastVsz = (int)U32(src, o + 8);
            lastVA = (int)U32(src, o + 12);
            lastRsz = (int)U32(src, o + 16);
            lastRaw = (int)U32(src, o + 20);
        }

        int newVA = Align(lastVA + lastVsz, alignS);
        byte[] shell = (byte[])PData.GetShell();
        int newVsz = shell.Length;
        int newRsz = Align(newVsz, alignF);
        int newRaw = Align(src.Length, alignF);

        /* 回填补丁尾部 jmp rel32：跳到入口 +21 */
        long jmpVA = (long)img + newVA + 0x5A;
        long back = (long)img + (entryOff + 0) + 0;        /* 占位，下面重算 */
        /* 入口 VA 由文件偏移换算：直接用入口所在节的 VA + 偏移 */
        int entryVA = 0;
        {
            int pe2 = pe, o2 = so;
            for (int i = 0; i < nsec; i++)
            {
                int o = o2 + i * 40;
                int vsz = (int)U32(src, o + 8), va = (int)U32(src, o + 12), rsz = (int)U32(src, o + 16), raw = (int)U32(src, o + 20);
                if (entryOff >= raw && entryOff < raw + rsz) { entryVA = va + (entryOff - raw); break; }
            }
        }
        back = (long)img + entryVA + 21;
        int rel = (int)(back - (jmpVA + 5));
        BitConverter.GetBytes(rel).CopyTo(shell, 0x5B);

        /* 组装新文件 */
        byte[] outb = new byte[newRaw + newRsz];
        Array.Copy(src, outb, src.Length);

        /* 节表新增一项 */
        int hdrOff = so + nsec * 40;
        byte[] hdr = new byte[40];
        Encoding.ASCII.GetBytes(".pmod").CopyTo(hdr, 0);
        BitConverter.GetBytes(newVsz).CopyTo(hdr, 8);
        BitConverter.GetBytes(newVA).CopyTo(hdr, 12);
        BitConverter.GetBytes(newRsz).CopyTo(hdr, 16);
        BitConverter.GetBytes(newRaw).CopyTo(hdr, 20);
        BitConverter.GetBytes((uint)0x60000020).CopyTo(hdr, 36);
        Array.Copy(hdr, 0, outb, hdrOff, 40);
        BitConverter.GetBytes((ushort)(nsec + 1)).CopyTo(outb, pe + 6);
        BitConverter.GetBytes((uint)(newVA + newVsz)).CopyTo(outb, opt + 56);   /* SizeOfImage */

        /* 节数据 */
        Array.Copy(shell, 0, outb, newRaw, newVsz);

        /* 入口 21 字节：E9 rel32 + 16 NOP */
        int rel2 = (int)(((long)img + newVA) - ((long)img + entryVA + 5));
        outb[entryOff] = 0xE9;
        BitConverter.GetBytes(rel2).CopyTo(outb, entryOff + 1);
        for (int i = 5; i < 21; i++) outb[entryOff + i] = 0x90;
        return outb;
    }

    private static string FindDir()
    {
        string[] cands = {
            Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location),
            @"C:\Program Files\Navicat\Navicat Premium 18",
            @"D:\Program Files\Navicat\Navicat Premium 18",
            @"C:\Program Files (x86)\Navicat\Navicat Premium 18"
        };
        foreach (string c in cands)
            if (c != null && File.Exists(Path.Combine(c, "navicat.exe"))) return c;
        return null;
    }

    private static int Main(string[] args)
    {
        string dir = null; bool uninstall = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length) dir = args[++i];
            else if (args[i] == "--uninstall") uninstall = true;
            else if (args[i] == "-h" || args[i] == "--help")
            {
                Console.WriteLine("NavicatPatch.exe [--dir <Navicat目录>] [--uninstall]");
                return 0;
            }
        }
        if (dir == null) dir = FindDir();
        if (dir == null) { Console.Write("请输入 Navicat 安装目录: "); dir = Console.ReadLine(); }
        dir = dir.Trim().Trim('"');
        string libcf = Path.Combine(dir, "libcf.dll");
        string exe = Path.Combine(dir, "navicat.exe");
        if (!File.Exists(libcf) || !File.Exists(exe)) { Console.WriteLine("该目录下没有 navicat.exe / libcf.dll：" + dir); return 1; }

        if (uninstall)
        {
            foreach (string[] pair in new[] { new[] { "libcf.dll", "libcf.dll.orig" }, new[] { "navicat.exe", "navicat.exe.orig" } })
            {
                string dst = Path.Combine(dir, pair[0]), bak = Path.Combine(dir, pair[1]);
                if (File.Exists(bak)) { File.Copy(bak, dst, true); Console.WriteLine("已还原 " + pair[0]); }
                else Console.WriteLine("未找到备份 " + pair[1]);
            }
            Console.WriteLine("卸载完成。");
            return 0;
        }

        /* 备份（仅首次） */
        foreach (string f in new[] { "libcf.dll", "navicat.exe" })
        {
            string src = Path.Combine(dir, f), bak = src + ".orig";
            if (!File.Exists(bak)) { File.Copy(src, bak); Console.WriteLine("已备份 " + f + " -> " + f + ".orig"); }
        }

        byte[] d = File.ReadAllBytes(libcf);
        int pos = FindPrologue(d, PData.GetPrologue());
        if (pos < 0) { Console.WriteLine("版本不匹配：未在 libcf.dll 中找到预期函数序言，请把 libcf.dll 提供给作者适配。"); return 2; }
        Console.WriteLine("版本校验通过（入口文件偏移 0x" + pos.ToString("X") + "）");

        byte[] patched = Apply(d, pos);
        File.WriteAllBytes(libcf, patched);
        Console.WriteLine("已写入补丁版 libcf.dll（新增节 .pmod，大小 " + patched.Length + " 字节）");

        byte[] b = File.ReadAllBytes(exe);
        int idx = FindPem(b);
        if (idx < 0) Console.WriteLine("警告：navicat.exe 中未找到 PEM 公钥（可能已替换），跳过");
        else
        {
            Array.Copy(PData.GetPem(), 0, b, idx, PData.GetPem().Length);
            File.WriteAllBytes(exe, b);
            Console.WriteLine("已替换请求码公钥（偏移 0x" + idx.ToString("X") + "，" + PData.GetPem().Length + " 字节）");
        }

        string vd = Path.Combine(dir, "version.dll");
        if (File.Exists(vd)) { File.Move(vd, vd + ".bak"); Console.WriteLine("旧的 version.dll 已改名备份"); }

        Console.WriteLine();
        Console.WriteLine("安装完成：启动 Navicat → 帮助 → 注册 → Ctrl+Shift 点「激活」，");
        Console.WriteLine("把请求码交给 NavicatKeygen.exe 生成激活码即可。");
        return 0;
    }
}
