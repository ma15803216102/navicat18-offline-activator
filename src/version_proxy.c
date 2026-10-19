/* version.dll 代理 —— Navicat 激活方式二（运行时补丁，不改任何 Navicat 文件）
 *
 * 原理：
 *   1) navicat.exe 通过导入表加载 version.dll（仅 3 个导出），而 Windows 在
 *      SafeDllSearchMode=1 下优先搜索 exe 所在目录 —— 且 version.dll 不在
 *      KnownDLLs 列表里，所以放在安装目录的这个 DLL 会被优先加载。
 *   2) Navicat 官方更新只替换它自己的文件，不会删除我们放进去的 version.dll，
 *      因此补丁在更新后依然生效；同时因为没有修改 navicat.exe / libcf.dll，
 *      也不会被任何自身完整性校验发现。
 *   3) 本 DLL 把 3 个导出转发给系统 version.dll（保证 Navicat 正常工作），
 *      并在后台线程里完成原本"文件级补丁"做的两件事：
 *        a. 把 navicat.exe 内存映像中的请求码公钥 PEM 替换成我们自己的；
 *        b. 按函数序言特征找到 libcf.dll 的 RSA 解密函数，就地改写为跳到
 *           一段运行时分配的 shell（shell 把 vtable+0x38 处的验签模数换成
 *           我们的，再执行原函数序言并跳回原函数）。
 *
 * 编译（MinGW-w64）：
 *   gcc -shared -O2 -o version.dll version_proxy.c -lversion -static-libgcc
 *
 * 安全设计：任何一步失败都只写日志、不影响 Navicat 正常启动（绝不阻断）。
 */
#include <windows.h>
#include <stdio.h>
#include "proxy_data.h"

/* ---- 可配置：日志路径（%TEMP%\nvproxy.log）；设为 0 可关闭日志 ---- */
#define WRITE_LOG 1

static HMODULE g_real = NULL;
static volatile LONG g_pem_done = 0;
static volatile LONG g_libcf_done = 0;

/* ------------------------------------------------------------------ 日志 */

static void nvlog(const char *fmt, ...)
{
#if WRITE_LOG
    char path[MAX_PATH];
    FILE *f;
    va_list ap;
    if (GetTempPathA(MAX_PATH, path) == 0) return;
    lstrcatA(path, "nvproxy.log");
    f = fopen(path, "a");
    if (!f) return;
    va_start(ap, fmt);
    vfprintf(f, fmt, ap);
    va_end(ap);
    fprintf(f, "\n");
    fclose(f);
#else
    (void)fmt;
#endif
}

/* --------------------------------------------------- PE 辅助：遍历可执行节 */

typedef struct {
    unsigned char *begin;   /* 节起始（文件中/内存中）*/
    unsigned char *end;
    char name[9];
} Sect;

/* 从模块基址解析节表，只收集含 EXECUTE 的节。
 * ★ 必须排除 ".pmod"：那是「文件级补丁」新增的节，里面内嵌了一份函数序言副本，
 *   若被扫到会打到副本上而不是真函数（实测踩过）。排除它同时也让两种激活方式互不干扰。 */
static int collect_exec_sections(unsigned char *base, Sect *out, int maxn)
{
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS *nt;
    IMAGE_SECTION_HEADER *sec;
    int i, n = 0;

    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;

    sec = IMAGE_FIRST_SECTION(nt);
    for (i = 0; i < nt->FileHeader.NumberOfSections && n < maxn; i++) {
        if (!(sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;
        if (memcmp(sec[i].Name, ".pmod", 5) == 0) {
            nvlog("[libcf] note: '.pmod' section present -> file-level patch is installed");
            continue;
        }
        out[n].begin = base + sec[i].VirtualAddress;
        out[n].end = out[n].begin + sec[i].Misc.VirtualSize;
        memcpy(out[n].name, sec[i].Name, 8);
        out[n].name[8] = 0;
        n++;
    }
    return n;
}

/* ------------------------------------------- 任务 a：替换请求码公钥 PEM */

static const char PEM_HEAD[] = "-----BEGIN PUBLIC KEY-----";

static int patch_pem_once(void)
{
    unsigned char *base = (unsigned char *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS *nt;
    unsigned char *scan;
    size_t size, i;
    int found = 0;

    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return 0;
    size = nt->OptionalHeader.SizeOfImage;

    /* 全映像扫描 PEM 头（公钥在只读数据节，需要临时放开写权限） */
    for (scan = base, i = 0; i + sizeof(PEM_HEAD) <= size; ) {
        if (scan[i] == '-' && memcmp(scan + i, PEM_HEAD, sizeof(PEM_HEAD) - 1) == 0) {
            DWORD old = 0;
            /* 已经是我们的公钥？幂等处理 */
            if (memcmp(scan + i, OUR_PEM, OUR_PEM_LEN) == 0) {
                nvlog("[pem] already patched at %p", (void *)(scan + i));
                return 1;
            }
            if (VirtualProtect(scan + i, OUR_PEM_LEN, PAGE_READWRITE, &old)) {
                memcpy(scan + i, OUR_PEM, OUR_PEM_LEN);
                VirtualProtect(scan + i, OUR_PEM_LEN, old, &old);
                nvlog("[pem] REPLACED at %p (module base %p)", (void *)(scan + i), (void *)base);
                found = 1;
                break;
            } else {
                nvlog("[pem] VirtualProtect failed at %p", (void *)(scan + i));
            }
        }
        i++;
    }
    if (!found) nvlog("[pem] PEM header not found in image (size=0x%zX)", size);
    return found;
}

/* ------------------------- 就近分配：E9 rel32 只能跳 ±2GB
 *
 * ★ 这里踩过一个真实的坑（2026-10-13 用户实测崩溃 c0000005）：
 *   直接用 VirtualAlloc(NULL,...) 分配 shell 时，系统可能给到与 libcf.dll
 *   相距几十 TB 的地址（实测 fn=0x7FF9D13DA850 / shell=0x24684150000，差约 138TB），
 *   而入口改写用的是 `E9 rel32`，跳转范围只有 ±2GB —— 相对位移被 int32
 *   截断成野地址，宿主进程一调用该函数就 c0000005。
 *   文件级补丁没这个问题，因为它的 shell 就放在 libcf 自己的 .pmod 节里。
 *   所以运行时必须把 shell 分配在目标函数附近。
 */
static unsigned char *alloc_near(void *anchor, size_t size)
{
    SYSTEM_INFO si;
    uintptr_t gran, base, off;
    void *p;

    GetSystemInfo(&si);
    gran = si.dwAllocationGranularity ? si.dwAllocationGranularity : 0x10000;
    base = (uintptr_t)anchor & ~((uintptr_t)gran - 1);

    /* 从近到远双向探测；上限 1.75GB，保证 rel32 一定装得下 */
    for (off = gran; off < 0x70000000ULL; off += gran) {
        if (base > off + size) {
            p = VirtualAlloc((void *)(base - off), size, MEM_COMMIT | MEM_RESERVE,
                             PAGE_EXECUTE_READWRITE);
            if (p) return (unsigned char *)p;
        }
        p = VirtualAlloc((void *)(base + off), size, MEM_COMMIT | MEM_RESERVE,
                         PAGE_EXECUTE_READWRITE);
        if (p) return (unsigned char *)p;
    }
    return NULL;
}

/* ------------------------------ 任务 b：给 libcf.dll 的 RSA 解密函数打补丁 */

static int patch_one_function(unsigned char *fn)
{
    /* 原函数入口前 21 字节会被改成：E9 rel32 + 16 x NOP */
    DWORD old = 0;
    unsigned char *sh;
    long long back, jmp_pos;
    int rel, rel2, i;

    /* 已经是补丁态（首字节 E9 且后继在我们的 shell 之外）——跳过 */
    if (fn[0] == 0xE9) {
        nvlog("[libcf] function at %p already looks patched, skip", (void *)fn);
        return 1;
    }

    /* ★ 必须就近分配（见 alloc_near 的说明） */
    sh = alloc_near(fn, SHELL_LEN);
    if (!sh) {
        nvlog("[libcf] alloc_near failed (err=%lu) -- 放弃本次补丁", GetLastError());
        return 0;
    }
    memcpy(sh, SHELL_CODE, SHELL_LEN);

    /* 回填 shell 内尾部 jmp：shell+0x5A 是 E9，0x5B 起是 rel32；目标是"原函数序言之后" */
    back = (long long)fn + PRO_SIG_LEN;
    jmp_pos = (long long)sh + 0x5A;
    rel = (int)(back - (jmp_pos + 5));

    /* 改原函数入口：E9 rel32 -> shell */
    rel2 = (int)((long long)sh - ((long long)fn + 5));

    /* ★ 范围兜底：任一 rel32 超范围就彻底放弃。
     *   宁可补丁不生效（Navicat 只是未激活），也绝不能把宿主进程打崩。 */
    if (rel < -(1 << 30) || rel >(1 << 30) || rel2 < -(1 << 30) || rel2 >(1 << 30)) {
        nvlog("[libcf] ABORT: rel32 out of range (rel=%d rel2=%d) -- 放弃本次补丁", rel, rel2);
        VirtualFree(sh, 0, MEM_RELEASE);
        return 0;
    }
    memcpy(sh + 0x5B, &rel, 4);

    if (!VirtualProtect(fn, PRO_SIG_LEN, PAGE_EXECUTE_READWRITE, &old)) {
        nvlog("[libcf] VirtualProtect(entry) failed (err=%lu)", GetLastError());
        VirtualFree(sh, 0, MEM_RELEASE);
        return 0;
    }
    fn[0] = 0xE9;
    memcpy(fn + 1, &rel2, 4);
    for (i = 5; i < PRO_SIG_LEN; i++) fn[i] = 0x90;
    VirtualProtect(fn, PRO_SIG_LEN, old, &old);
    FlushInstructionCache(GetCurrentProcess(), fn, PRO_SIG_LEN);

    nvlog("[libcf] PATCHED fn=%p shell=%p dist=%lld rel=%d rel2=%d",
          (void *)fn, (void *)sh, (long long)sh - (long long)fn, rel, rel2);
    return 1;
}

static int patch_libcf_once(void)
{
    HMODULE mod = GetModuleHandleW(L"libcf.dll");
    Sect secs[32];
    int nsec, i, j, hits = 0;

    if (!mod) return 0;
    nsec = collect_exec_sections((unsigned char *)mod, secs, 32);
    if (nsec <= 0) {
        nvlog("[libcf] module at %p has no executable section", (void *)mod);
        return 0;
    }
    for (i = 0; i < nsec; i++) {
        unsigned char *p = secs[i].begin;
        /* 留出一个序言长度的余量，避免越界比较 */
        while (p + PRO_SIG_LEN <= secs[i].end) {
            if (p[0] == PRO_SIG[0] && memcmp(p, PRO_SIG, PRO_SIG_LEN) == 0) {
                nvlog("[libcf] prologue hit at %p (section %s)", (void *)p, secs[i].name);
                patch_one_function(p);
                hits++;
                p += PRO_SIG_LEN;
                continue;
            }
            p++;
        }
    }
    if (hits == 0) {
        nvlog("[libcf] VERSION MISMATCH: prologue not found in %d exec sections "
              "(module %p) -- needs re-adaptation", nsec, (void *)mod);
        /* 返回 1 表示"已完成判定"，不再重试，避免刷日志 */
        return 1;
    }
    nvlog("[libcf] done, %d hit(s)", hits);
    return 1;
}

/* ------------------------------------------------------------ 后台工作线程 */

static DWORD WINAPI worker(LPVOID param)
{
    int i;
    (void)param;

    nvlog("[proxy] worker start (pid=%lu)", (unsigned long)GetCurrentProcessId());

    /* PEM 在主模块映像里，立即可做；失败则重试几次（有些发行版延迟解密） */
    for (i = 0; i < 20; i++) {
        if (patch_pem_once()) { InterlockedExchange(&g_pem_done, 1); break; }
        Sleep(150);
    }
    if (!g_pem_done) nvlog("[pem] giving up after retries");

    /* libcf.dll 可能晚于 version.dll 加载：轮询等待（最多 ~90 秒） */
    for (i = 0; i < 900; i++) {
        if (GetModuleHandleW(L"libcf.dll")) {
            if (patch_libcf_once()) { InterlockedExchange(&g_libcf_done, 1); break; }
        }
        Sleep(100);
    }
    if (!g_libcf_done) nvlog("[libcf] giving up after retries");

    nvlog("[proxy] worker exit (pem=%ld libcf=%ld)", g_pem_done, g_libcf_done);
    return 0;
}

/* -------------------------------------------------- 导出的 3 个转发函数 */

static void *real_proc(const char *name)
{
    if (!g_real) return NULL;
    return (void *)GetProcAddress(g_real, name);
}

__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR f, LPDWORD h)
{
    typedef DWORD(WINAPI * P)(LPCWSTR, LPDWORD);
    P p = (P)real_proc("GetFileVersionInfoSizeW");
    return p ? p(f, h) : 0;
}

__declspec(dllexport) BOOL WINAPI GetFileVersionInfoW(LPCWSTR f, DWORD h, DWORD len, LPVOID data)
{
    typedef BOOL(WINAPI * P)(LPCWSTR, DWORD, DWORD, LPVOID);
    P p = (P)real_proc("GetFileVersionInfoW");
    return p ? p(f, h, len, data) : FALSE;
}

__declspec(dllexport) BOOL WINAPI VerQueryValueW(LPCVOID blk, LPCWSTR sub, LPVOID *out, PUINT len)
{
    typedef BOOL(WINAPI * P)(LPCVOID, LPCWSTR, LPVOID *, PUINT);
    P p = (P)real_proc("VerQueryValueW");
    return p ? p(blk, sub, out, len) : FALSE;
}

/* 供外部（诊断/自检）确认代理是否已就位并完成补丁 */
__declspec(dllexport) int NvProxyState(void)
{
    return (g_pem_done ? 1 : 0) | (g_libcf_done ? 2 : 0);
}

/* ------------------------------------------------------------------ 入口 */

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID reserved)
{
    wchar_t sysdir[MAX_PATH];
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(self);

        GetSystemDirectoryW(sysdir, MAX_PATH);
        lstrcatW(sysdir, L"\\version.dll");
        g_real = LoadLibraryW(sysdir);
        nvlog("[proxy] loaded, real version.dll = %p (%s)",
              (void *)g_real, g_real ? "ok" : "FAILED");

        /* DllMain 内只创建线程，不在这里做任何重活 */
        if (CreateThread(NULL, 0, worker, NULL, 0, NULL) == NULL) {
            nvlog("[proxy] CreateThread failed (err=%lu)", GetLastError());
        }
    }
    return TRUE;
}
