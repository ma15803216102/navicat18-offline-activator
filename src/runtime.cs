/* Navicat 18 运行时补丁（无文件 / 无 DLL 落盘）
 *
 * 目的：不再往 Navicat 安装目录写 version.dll —— 直接对**已经在运行**的 navicat.exe
 *       进程做内存补丁（改 PEM、给 libcf.dll 的 RSA 解密函数入口装跳板）。
 *
 * 做的事：
 *   1) OpenProcess + 找到主模块（navicat.exe）与 libcf.dll 的基址
 *   2) 扫主模块映像里的请求码公钥 PEM（460 字节）→ 原地替换成 PemData.Pem
 *   3) 扫 libcf.dll 可执行节里的 21 字节函数序言 → 在目标进程内分配一块靠近它的
 *      可执行内存（±2GB 内），写入 .pmod 跳板（内嵌模数），把入口改成 E9 rel32
 *
 * 为什么不用注入 DLL：代理 DLL 的全部工作就是上面两件事，跨进程直接读写就够了；
 * 这样既不需要 version.dll 文件，也不需要远程线程加载模块。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class RuntimePatch
{
    // ---------------------------------------------------------------- P/Invoke
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProtect, out uint oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }

    private const uint PROCESS_ALL = 0x1F0FFF;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_EXECUTE_READWRITE = 0x40, PAGE_READWRITE = 0x04;
    private const int PEM_LEN = 460;
    private static readonly byte[] PEM_MAGIC = new byte[] { 0x2D, 0x2D, 0x2D, 0x2D, 0x2D, 0x42, 0x45, 0x47, 0x49, 0x4E, 0x20, 0x50, 0x55, 0x42, 0x4C, 0x49, 0x43, 0x20, 0x4B, 0x45, 0x59, 0x2D, 0x2D, 0x2D, 0x2D, 0x2D };

    // ---------------------------------------------------------------- 模块
    private sealed class Mod
    {
        public string Name;
        public IntPtr Base;
        public uint Size;
        public string Path;
    }

    private static List<Mod> Modules(uint pid)
    {
        var list = new List<Mod>();
        IntPtr snap = CreateToolhelp32Snapshot(0x8 | 0x10, pid);
        if (snap == (IntPtr)(-1)) return list;
        try
        {
            var me = new MODULEENTRY32W();
            me.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
            if (Module32FirstW(snap, ref me))
            {
                do
                {
                    var m = new Mod();
                    m.Name = me.szModule;
                    m.Base = me.modBaseAddr;
                    m.Size = me.modBaseSize;
                    m.Path = me.szExePath;
                    list.Add(m);
                } while (Module32NextW(snap, ref me));
            }
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // ---------------------------------------------------------------- 内存读写
    private static byte[] Read(IntPtr hp, IntPtr addr, int len)
    {
        var buf = new byte[len];
        IntPtr got;
        if (!ReadProcessMemory(hp, addr, buf, (IntPtr)len, out got)) return null;
        return buf;
    }

    /// <summary>读取跨不可读页时二分重试；读不到的部分填 0（PEM/序言都是纯 ASCII，不会误匹配）。</summary>
    private static byte[] ReadSafe(IntPtr hp, IntPtr addr, int len)
    {
        byte[] buf = Read(hp, addr, len);
        if (buf != null) return buf;
        if (len <= 0x10000) return null;
        int half = len / 2;
        byte[] a = ReadSafe(hp, addr, half);
        byte[] b = ReadSafe(hp, (IntPtr)(addr.ToInt64() + half), len - half);
        if (a == null && b == null) return null;
        var r = new byte[len];
        if (a != null) Array.Copy(a, 0, r, 0, a.Length);
        if (b != null) Array.Copy(b, 0, r, half, b.Length);
        return r;
    }

    private static bool Write(IntPtr hp, IntPtr addr, byte[] data)
    {
        IntPtr wrote;
        return WriteProcessMemory(hp, addr, data, (IntPtr)data.Length, out wrote);
    }

    private static bool SetProtect(IntPtr hp, IntPtr addr, int len, uint prot, out uint old)
    {
        return VirtualProtectEx(hp, addr, (IntPtr)len, prot, out old);
    }

    // ---------------------------------------------------------------- 找 PEM
    private static IntPtr FindPem(IntPtr hp, Mod mod)
    {
        // 按节扫（PEM 常在 .rsrc/.rdata；直接扫整个映像会踩节间未映射页，整块读失败）
        foreach (var sec in Sections(hp, mod))
        {
            int size = (int)Math.Min(sec.VSize == 0 ? sec.RawSize : sec.VSize,
                                     sec.RawSize == 0 ? sec.VSize : sec.RawSize);
            if (size < PEM_LEN) continue;
            long secBase = mod.Base.ToInt64() + sec.VA;
            const int CHUNK = 512 * 1024;
            for (int off = 0; off < size; off += CHUNK - PEM_LEN)
            {
                int len = Math.Min(CHUNK, size - off);
                if (len <= PEM_LEN) break;
                byte[] buf = ReadSafe(hp, (IntPtr)(secBase + off), len);
                if (buf == null) continue;
                for (int i = 0; i + PEM_LEN <= len; i++)
                {
                    bool hit = true;
                    for (int k = 0; k < PEM_MAGIC.Length; k++)
                    {
                        if (buf[i + k] != PEM_MAGIC[k]) { hit = false; break; }
                    }
                    if (!hit) continue;
                    string tail = System.Text.Encoding.ASCII.GetString(buf, i + PEM_LEN - 26, 24);
                    if (tail == "-----END PUBLIC KEY-----")
                        return (IntPtr)(secBase + off + i);
                }
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- PE 节
    private struct Sec
    {
        public string Name;
        public uint VA;
        public uint VSize;
        public uint RawOff;
        public uint RawSize;
        public uint Chars;
    }

    private static List<Sec> Sections(IntPtr hp, Mod mod)
    {
        var list = new List<Sec>();
        byte[] hdr = Read(hp, mod.Base, 0x1000);
        if (hdr == null) return list;
        int e_lfanew = BitConverter.ToInt32(hdr, 0x3C);
        if (e_lfanew <= 0 || e_lfanew + 24 > hdr.Length) return list;
        if (BitConverter.ToInt32(hdr, e_lfanew) != 0x00004550) return list;
        int nsec = BitConverter.ToUInt16(hdr, e_lfanew + 6);
        int optSize = BitConverter.ToUInt16(hdr, e_lfanew + 20);
        int secOff = e_lfanew + 24 + optSize;
        byte[] secTab = Read(hp, (IntPtr)(mod.Base.ToInt64() + secOff), nsec * 40);
        if (secTab == null) return list;
        for (int k = 0; k < nsec; k++)
        {
            int o = k * 40;
            var s = new Sec();
            s.Name = System.Text.Encoding.ASCII.GetString(secTab, o, 8).TrimEnd('\0');
            s.VSize = BitConverter.ToUInt32(secTab, o + 8);
            s.VA = BitConverter.ToUInt32(secTab, o + 12);
            s.RawSize = BitConverter.ToUInt32(secTab, o + 16);
            s.RawOff = BitConverter.ToUInt32(secTab, o + 20);
            s.Chars = BitConverter.ToUInt32(secTab, o + 36);
            list.Add(s);
        }
        return list;
    }

    // ---------------------------------------------------------------- 找函数入口
    private static IntPtr FindPrologue(IntPtr hp, Mod mod, byte[] pro)
    {
        foreach (var s in Sections(hp, mod))
        {
            if (s.Name == ".pmod") continue;                        // 文件级补丁的 shell 里也有一份序言
            if ((s.Chars & 0x20000000) == 0) continue;              // 只扫可执行节
            int size = (int)Math.Min(s.VSize == 0 ? s.RawSize : s.VSize, s.RawSize == 0 ? s.VSize : s.RawSize);
            if (size <= 0) continue;
            const int CHUNK = 8 * 1024 * 1024;
            for (int off = 0; off < size; off += CHUNK - pro.Length)
            {
                int len = Math.Min(CHUNK, size - off);
                if (len <= pro.Length) break;
                byte[] buf = ReadSafe(hp, (IntPtr)(mod.Base.ToInt64() + s.VA + off), len);
                if (buf == null) break;
                for (int i = 0; i + pro.Length <= len; i++)
                {
                    bool hit = true;
                    for (int k = 0; k < pro.Length; k++)
                    {
                        if (buf[i + k] != pro[k]) { hit = false; break; }
                    }
                    if (hit) return (IntPtr)(mod.Base.ToInt64() + s.VA + off + i);
                }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>扫可执行节里的补丁特征：E9 rel32 + 16×NOP（入口已被改）。</summary>
    private static IntPtr FindPatchedEntry(IntPtr hp, Mod mod)
    {
        foreach (var s in Sections(hp, mod))
        {
            if (s.Name == ".pmod") continue;
            if ((s.Chars & 0x20000000) == 0) continue;
            int size = (int)Math.Min(s.VSize == 0 ? s.RawSize : s.VSize, s.RawSize == 0 ? s.VSize : s.RawSize);
            if (size < 21 + 0x100) continue;
            const int CHUNK = 8 * 1024 * 1024;
            for (int off = 0; off < size; off += CHUNK - 32)
            {
                int len = Math.Min(CHUNK, size - off);
                if (len < 32) break;
                byte[] buf = ReadSafe(hp, (IntPtr)(mod.Base.ToInt64() + s.VA + off), len);
                if (buf == null) break;
                for (int i = 0; i + 21 <= len; i++)
                {
                    if (buf[i] != 0xE9) continue;
                    bool ok = true;
                    for (int k = 5; k < 21; k++) if (buf[i + k] != 0x90) { ok = false; break; }
                    if (ok) return (IntPtr)(mod.Base.ToInt64() + s.VA + off + i);
                }
            }
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 近地址分配（E9 rel32 只有 ±2GB）
    private static IntPtr AllocNear(IntPtr hp, IntPtr target, int size)
    {
        long lo = target.ToInt64() - 0x70000000L;   // ~1.75GB
        long hi = target.ToInt64() + 0x70000000L;
        for (long a = target.ToInt64(); a >= lo && a <= hi; a += 0x10000)
        {
            IntPtr probe = (IntPtr)a;
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQueryEx(hp, probe, out mbi, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero)
                continue;
            if (mbi.State != 0x10000)   // MEM_FREE
                continue;
            IntPtr mem = VirtualAllocEx(hp, probe, (IntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (mem != IntPtr.Zero) return mem;
        }
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 主流程
    /// <summary>对指定 navicat 进程打运行时补丁。成功返回空串，失败返回原因。</summary>
    public static string Apply(int pid, Action<string> log)
    {
        IntPtr hp = OpenProcess(PROCESS_ALL, false, (uint)pid);
        if (hp == IntPtr.Zero) return "OpenProcess 失败（错误码 " + Marshal.GetLastWin32Error() + "，请以管理员运行）";

        try
        {
            Mod nav = null, lib = null;
            foreach (var m in Modules((uint)pid))
            {
                if (string.Equals(m.Name, "navicat.exe", StringComparison.OrdinalIgnoreCase)) nav = m;
                if (string.Equals(m.Name, "libcf.dll", StringComparison.OrdinalIgnoreCase)) lib = m;
            }
            if (lib == null) return "libcf.dll 尚未加载（请等 Navicat 完全启动后再试）";
            if (nav == null) log("（本进程里没有 navicat.exe 模块，只做 libcf 部分）");

            // ---- 1) 请求码公钥 PEM
            byte[] pem = PData.GetPem();
            IntPtr pPem = nav == null ? IntPtr.Zero : FindPem(hp, nav);
            if (pPem == IntPtr.Zero)
            {
                log("（提示：主模块里没找到 PEM 头，可能已被替换或版本不同）");
            }
            else
            {
                byte[] cur = Read(hp, pPem, (int)PEM_LEN);
                bool same = cur != null;
                if (same)
                    for (int i = 0; i < PEM_LEN; i++) if (cur[i] != pem[i]) { same = false; break; }

                if (same)
                {
                    log("✔ 请求码公钥已经是我们的（无需替换）");
                }
                else
                {
                    uint old;
                    if (!SetProtect(hp, pPem, (int)PEM_LEN, PAGE_READWRITE, out old))
                        return "PEM 改保护失败（错误码 " + Marshal.GetLastWin32Error() + "）";
                    bool ok = Write(hp, pPem, pem);
                    SetProtect(hp, pPem, (int)PEM_LEN, old, out old);
                    if (!ok) return "写 PEM 失败（错误码 " + Marshal.GetLastWin32Error() + "）";
                    log("✔ 已把请求码公钥替换成自己的（0x" + (pPem.ToInt64()).ToString("X") + "）");
                }
            }

            // ---- 2) libcf 验签函数入口
            byte[] pro = PData.GetPrologue();
            IntPtr entry = FindPrologue(hp, lib, pro);
            if (entry == IntPtr.Zero)
                return "在 libcf.dll 的可执行节里没找到函数序言（该版本可能不匹配，或文件级补丁已占用入口）";

            byte[] head = Read(hp, entry, 5);
            if (head != null && head[0] == 0xE9)
            {
                log("✔ libcf 入口已经是跳板（补丁已在位）");
                return "";
            }

            byte[] shell = PData.GetShell();
            IntPtr mem = AllocNear(hp, entry, shell.Length);
            if (mem == IntPtr.Zero) return "目标进程里分配不到 ±2GB 内的可执行内存（放弃，避免野跳转）";

            // 回填 shell 里的 jmp rel32：shell+0x5A 是 E9，0x5B 起是 rel32，目标 = entry+21
            long rel = entry.ToInt64() + 21 - (mem.ToInt64() + 0x5A + 5);
            if (rel < int.MinValue || rel > int.MaxValue)
            {
                VirtualFreeEx(hp, mem, IntPtr.Zero, MEM_RELEASE);
                return "shell 与函数入口距离超出 rel32 范围（放弃）";
            }
            byte[] relBytes = BitConverter.GetBytes((int)rel);
            shell[0x5B] = relBytes[0];
            shell[0x5C] = relBytes[1];
            shell[0x5D] = relBytes[2];
            shell[0x5E] = relBytes[3];

            if (!Write(hp, mem, shell))
            {
                VirtualFreeEx(hp, mem, IntPtr.Zero, MEM_RELEASE);
                return "写 shell 失败（错误码 " + Marshal.GetLastWin32Error() + "）";
            }
            log("✔ shell 已写入目标进程 0x" + (mem.ToInt64()).ToString("X") + "（" + shell.Length + " 字节）");

            // 入口：E9 rel32 + 16×NOP
            byte[] patch = new byte[21];
            patch[0] = 0xE9;
            long d = mem.ToInt64() - (entry.ToInt64() + 5);
            if (d < int.MinValue || d > int.MaxValue)
            {
                VirtualFreeEx(hp, mem, IntPtr.Zero, MEM_RELEASE);
                return "入口跳转超出 rel32 范围（放弃）";
            }
            BitConverter.GetBytes((int)d).CopyTo(patch, 1);
            for (int i = 5; i < 21; i++) patch[i] = 0x90;

            uint oldP;
            if (!SetProtect(hp, entry, 21, PAGE_EXECUTE_READWRITE, out oldP))
            {
                VirtualFreeEx(hp, mem, IntPtr.Zero, MEM_RELEASE);
                return "入口改保护失败（错误码 " + Marshal.GetLastWin32Error() + "）";
            }
            bool pok = Write(hp, entry, patch);
            SetProtect(hp, entry, 21, oldP, out oldP);
            if (!pok)
            {
                VirtualFreeEx(hp, mem, IntPtr.Zero, MEM_RELEASE);
                return "写入入口跳转失败（错误码 " + Marshal.GetLastWin32Error() + "）";
            }
            log("✔ 已给 libcf 入口装跳板（0x" + (entry.ToInt64()).ToString("X") + " → 0x" + (mem.ToInt64()).ToString("X") + "）");
            return "";
        }
        finally
        {
            CloseHandle(hp);
        }
    }

    /// <summary>诊断：列出模块的节表与 PEM 扫描结果。</summary>
    public static string Dump(int pid)
    {
        IntPtr hp = OpenProcess(PROCESS_ALL, false, (uint)pid);
        if (hp == IntPtr.Zero) return "OpenProcess failed";
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var m in Modules((uint)pid))
            {
                if (!string.Equals(m.Name, "navicat.exe", StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine("mod " + m.Name + " base=0x" + m.Base.ToInt64().ToString("X") + " size=0x" + m.Size.ToString("X"));
                var secs = Sections(hp, m);
                sb.AppendLine("sections=" + secs.Count);
                foreach (var sc in secs)
                {
                    int size = (int)Math.Min(sc.VSize == 0 ? sc.RawSize : sc.VSize, sc.RawSize == 0 ? sc.VSize : sc.RawSize);
                    sb.AppendLine("  " + sc.Name + " VA=0x" + sc.VA.ToString("X") + " VSize=0x" + sc.VSize.ToString("X")
                        + " RawSize=0x" + sc.RawSize.ToString("X") + " scanSize=" + size);
                }
                IntPtr p = FindPem(hp, m);
                sb.AppendLine("FindPem -> 0x" + p.ToInt64().ToString("X"));
            }
            return sb.ToString();
        }
        finally { CloseHandle(hp); }
    }

    /// <summary>只检查状态，不改内存。</summary>
    public static string State(int pid)
    {
        IntPtr hp = OpenProcess(0x0410, false, (uint)pid);   // VM_READ | QUERY_INFORMATION
        if (hp == IntPtr.Zero) return "无法打开进程";
        try
        {
            Mod nav = null, lib = null;
            foreach (var m in Modules((uint)pid))
            {
                if (string.Equals(m.Name, "navicat.exe", StringComparison.OrdinalIgnoreCase)) nav = m;
                if (string.Equals(m.Name, "libcf.dll", StringComparison.OrdinalIgnoreCase)) lib = m;
            }
            string s = "";
            if (nav != null) s += "PEM=" + (FindPem(hp, nav) != IntPtr.Zero ? "yes" : "no");
            if (lib != null)
            {
                IntPtr e = FindPrologue(hp, lib, PData.GetPrologue());
                if (e != IntPtr.Zero)
                {
                    byte[] h = Read(hp, e, 1);
                    s += " libcf=" + (h != null && h[0] == 0xE9 ? "patched" : "clean");
                }
                else s += " libcf=" + (FindPatchedEntry(hp, lib) != IntPtr.Zero ? "patched" : "unknown");
            }
            return s;
        }
        finally { CloseHandle(hp); }
    }
}
