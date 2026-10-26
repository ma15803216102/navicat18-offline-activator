# Navicat 18 离线激活工具

**不需要登录、不需要联网、不需要服务器。** 全部签名在本机完成 —— 用的是你自己生成的密钥对。

[![Latest release](https://img.shields.io/github/v/release/ma15803216102/navicat18-offline-activator?label=release)](https://github.com/ma15803216102/navicat18-offline-activator/releases)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-orange)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-blue)](#环境要求)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4)](https://dotnet.microsoft.com/)

**简体中文** ｜ [English](README.en.md)

仓库：[Gitee](https://gitee.com/mzy666666/navicat18-offline-activator) ｜ [GitHub](https://github.com/ma15803216102/navicat18-offline-activator)（两边同步发布）

> **许可**：PolyForm Noncommercial License 1.0.0（见 [LICENSE](LICENSE)）—— 源码可见 + **禁止商业使用**。
> 这**不是** OSI 认可的开源许可（OSI 的开源定义不允许限制使用领域），准确说法是
> **source-available, noncommercial**。任何商业使用（含内部办公、生产环境、对外服务、付费交付）需单独授权。
>
> **免责**：仅用于学习、研究、自有软件与已授权环境的兼容性测试。
> Navicat 是 PremiumSoft 的商业软件，请勿用本项目侵犯其著作权或规避商业授权。

---

## 目录

- [功能特性](#功能特性)
- [工作原理](#工作原理)
- [两种补丁方式](#两种补丁方式)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [使用步骤](#使用步骤)
- [从源码构建](#从源码构建)
- [命令行接口](#命令行接口)
- [项目结构](#项目结构)
- [常见问题](#常见问题)
- [已知限制](#已知限制)
- [安全说明](#安全说明)
- [许可](#许可)
- [赞助](#赞助)
- [致谢](#致谢)

---

## 功能特性

- **完全离线**：不联网、不登录、不需要任何服务端，激活全程在本机完成。
- **两种补丁方式**：`libcf.dll` 文件级（默认）与 `version.dll` 代理 DLL，按需选择、可互相切换（先卸载）。
- **一条命令构建**：`build\build_offline.cmd` 自动编译并内嵌代理 DLL；机器上没有 MinGW 时自动降级为「仅 `libcf.dll`」，构建照样成功。
- **目录自动识别**：同时装了 Navicat 17 和 18 时按 `navicat.exe` 文件版本选 **18**；你手动选过目录之后不再被覆盖。
- **机器可读自检**：`--modes`、`--dir` 等开关把结果写到 `%TEMP%`，便于脚本化验证与排障。
- **无残留**：不装服务、不装驱动、不留常驻进程；工具退出即结束（补丁本身按所选方式留在 Navicat 目录）。

---

## 工作原理

Navicat 的「手动激活」是标准的 **请求码 ↔ 激活码** 模型：

1. 客户端用**内嵌公钥**加密含机器信息的 JSON → **请求码**；
2. 厂商用**私钥**解开、构造许可 JSON、再用私钥签名 → **激活码**；
3. 客户端用**内嵌验签模数**验证激活码，通过后写入许可。

本工具把这两把「厂商的钥匙」换成**你自己的**：

| 位置 | 原本 | 替换成 | 做法 |
|---|---|---|---|
| `navicat.exe` 里的请求码公钥 | 厂商 RSA-2048 SPKI PEM | 你的公钥（同长度 460 字节覆盖） | 文件替换 / 内存替换 |
| `libcf.dll` 里的验签模数 | 厂商模数（运行时才构造） | 你的模数（小端 256 字节） | `.pmod` 跳板 / 运行时 shell |

`libcf.dll` 的 **inline hook**（入口 hook / detour，核心机制）：

1. 在可执行节里按 **21 字节函数序言** 定位 `decrypt_RSA_NON_STD`（Navicat 18 中 RVA `0xEA850`，仅作参考，实际靠特征匹配）；
2. 新增可执行节 `.pmod`（352 字节 shell，**偏移 `0x5F` 处内嵌你的 256 字节模数**）；
3. 函数入口前 21 字节改成 `E9 rel32`（jmp）+ 16 × `NOP`，跳到 `.pmod`；
4. shell 保存现场 → 把调用现场对象里的验签模数换成你的 → 执行原序言 → `jmp` 回 `入口 + 21`。

于是 Navicat 自己的 RSA 校验会用**你的模数**验**你用自己私钥签出的激活码** —— 天然通过。

更细的推导（字段格式、块长度条件、版本字节）见 [docs/原理.md](docs/原理.md)。

---

## 两种补丁方式

| | `libcf.dll`（文件级，默认） | `version.dll`（代理 DLL） |
|---|---|---|
| 原理 | 给 `libcf.dll` 新增 `.pmod` 节 + 改函数入口做 **inline hook** | 在安装目录放一个代理 DLL，由加载器在启动时读入、在进程内打补丁 |
| 是否修改 Navicat 文件 | **是**（`libcf.dll` + `navicat.exe` 的公钥） | **否**（只多一个 `version.dll`） |
| 官方更新后 | 被覆盖，需要重装 | 仍然有效 |
| 额外依赖 | 无 | 需要用 MinGW 编译代理 DLL（仓库不附带成品，构建时自动内嵌进 exe） |
| 磁盘残留 | 补丁后的 DLL + `.orig` 备份 | 一个 `version.dll`（常驻，卸载时删除） |
| 卸载 | 拷回 `.orig` | 删掉 `version.dll` |

两种方式**互斥**：文件级补丁把函数入口改成 `E9` 跳板后，代理 DLL 找不到原序言，会明确报「没找到函数序言」。要切换方式，先用工具里的【卸载补丁】。

---

## 环境要求

**运行（仅使用发布包）**

- Windows 10 / 11 x64
- 管理员权限（写 `Program Files` 需要）

**构建**

- .NET Framework 4.x（系统自带 `csc.exe`，无需安装 SDK）
- Python 3（需要 `cryptography` 库，或 `openssl` —— Git for Windows 自带）
  - 脚本会自动找 `python`（PATH → `py -3` → 常见安装目录）；找不到时用 `set PYTHON=C:\Python313\python.exe` 指定
- （可选，仅 `version.dll` 方式）MinGW-w64 的 `gcc`
  - 装好即可：`winget install -e --id BrechtSanders.WinLibs.POSIX.UCRT`
  - 或用 `set GCC=C:\mingw64\bin\gcc.exe` 指定
  - **不需要**手动把 MinGW 加进系统 PATH —— 脚本会自己把 gcc 所在目录前置到 PATH 并做自检

---

## 快速开始

1. 打开 [Releases](https://github.com/ma15803216102/navicat18-offline-activator/releases)（或 [Gitee 发行版](https://gitee.com/mzy666666/navicat18-offline-activator/releases)），下载最新 `Navicat18Tool-Offline-v*.zip`；
2. 解压，用 `SHA256SUMS.txt` 核对 `Navicat18Tool-Offline.exe` 的哈希；
3. 右键 **以管理员身份运行** `Navicat18Tool-Offline.exe`；
4. 按[使用步骤](#使用步骤)走完 6 步。

> 也可以从源码构建，见[从源码构建](#从源码构建)。

---

## 使用步骤

> 面向用户的完整手册（界面示意、失败排查、卸载还原）见 **[docs/使用说明.md](docs/使用说明.md)**；下面是速查版。

1. 右键 **以管理员身份运行** `Navicat18Tool-Offline.exe`（写 `Program Files` 需要管理员）；
2. 选择激活方式：
   - **libcf.dll（默认）**：点【安装补丁】→ 备份并改写 `libcf.dll` / `navicat.exe`；
   - **version.dll（代理 DLL）**：点【安装补丁】→ 若 Navicat 正在运行会先把它关掉，然后在安装目录释放 `version.dll`；它常驻在那里，**以后每次启动 Navicat 都会自动加载**并完成补丁，不需要重复安装。
3. 启动 Navicat → 【帮助】→【注册】；
4. 按住 **Ctrl+Shift** 点【激活】→ 弹出「手动激活」窗口，复制**请求码**；
5. 回工具：粘贴请求码、填邮箱/公司 → 生成**激活码**；
6. 把激活码粘回 Navicat → 完成。

卸载：工具里【卸载补丁】会把 `.orig` 拷回 / 删除代理 DLL。

> **同时装了 17 和 18？** 注册表里两代记录都在，工具会按 `navicat.exe` 的文件版本自动挑 **18**；
> 你手动改过目录之后，点【安装补丁】/【开始激活】**不会再覆盖回去**。核对自动识别结果：
> `Navicat18Tool-Offline.exe --dir` → `%TEMP%\nvdir.txt`。

---

## 从源码构建

```bat
:: 1) 生成你自己的 RSA-2048 密钥对（一次性）→ keys\ + src\pem_local.cs
python tools\gen_keys.py

:: 2) 把 .pmod shell 里的模数换成你的公钥模数（必须做，否则签出来的码验不过）
python tools\gen_payload.py

:: 3) 用私钥生成离线签发实现 → src\offline.cs
python tools\gen_offline.py

:: 4) 编译主程序 → bin\Navicat18Tool-Offline.exe
build\build_offline.cmd
```

第 4 步会**自动处理 `version.dll` 方式的代理 DLL**（编译 → 转成内嵌字节数组），所以**不需要**先手工执行 `build\build_proxy.cmd`，也**不需要**手工跑 `python tools\gen_proxy_cs.py`（这两个脚本保留着，想单独调试代理时可以直接跑）。

三种机器状态它自己会判断：

| 机器状态 | 结果 |
|---|---|
| 有 MinGW gcc、`src\proxy_data.cs` 不存在 | 自动编译代理并内嵌 → 两种方式都可用 |
| 已内嵌过（`src\proxy_data.cs` 正常） | 直接使用，不重复编译 |
| **没有 MinGW gcc**（或代理编译失败） | 退回「无代理」占位实现，**照样构建成功**；该 exe 只能用 `libcf.dll` 方式，界面上切换 `version.dll` 会被明确拒绝并说明原因 |

构建后可以自查这个 exe 支持哪些方式：

```bat
bin\Navicat18Tool-Offline.exe --modes
:: 输出 %TEMP%\nvmode.txt
::   PROXY|available|len=<n>|sha256=<hash>   → 两种方式都可用
::   PROXY|unavailable|len=0|...             → 只能用 libcf.dll
```

### 构建报错对照

| 报错 | 原因 / 处理 |
|---|---|
| `'python' is not recognized` | Python 没进 PATH。`set PYTHON=C:\Python313\python.exe` 后重跑（脚本也会试 `py -3` 与常见安装目录） |
| `FAILED: tools\gen_proxy_h.py` | 缺 `cryptography`：`python -m pip install cryptography` |
| `[!!] gcc.exe not found` | 没装 MinGW，或装了但不在 PATH。`set GCC=C:\mingw64\bin\gcc.exe` 后重跑 |
| `[proxy] version.dll could NOT be built` | 上一条的后果：exe 照样构建成功，但只有 `libcf.dll` 一种方式；装好 gcc 后重跑即可 |
| `BUILD FAILED: gcc could not compile ...` | gcc 能跑但编译失败，脚本会把 gcc 原始报错打出来 |
| `[!!] src\pdata.cs shell modulus does not match ...` | 换过密钥但没重跑 `python tools\gen_payload.py` |

只想单独构建代理 DLL 看详细输出：直接跑 `build\build_proxy.cmd`（支持 `set GCC=` 的绝对/相对路径、带不带引号都行）。

### 关于 gcc 的 PATH

`build_proxy.cmd` 支持 `set GCC=C:\path\to\gcc.exe`，但**只设这个变量以前是不够的**：
`gcc.exe` 自己要加载同目录下的 `libisl-*.dll` / `libmpc-*.dll` / `libwinpthread-1.dll`，而 Windows 是按 **PATH** 找这些 DLL 的（不是按 exe 所在目录）。MinGW 的 `bin` 不在 PATH 里时，gcc 会**以退出码 1 静默失败、一个字都不打印**，看起来像「构建莫名失败」。

现在脚本会自己把 gcc 所在目录前置到 PATH，并在编译前先跑一次 `gcc --version` 做自检；真的缺东西时会把 gcc 的报错原样打出来。手工验证：

```bat
set GCC=C:\path\to\mingw64\bin\gcc.exe
build\build_proxy.cmd
:: 期望最后一行： BUILD OK  ->  bin\version.dll
```

### 本机生成物

`src\offline.cs`、`src\pem_local.cs`、`src\proxy_data.cs` 都是**本机生成物**（已在 `.gitignore`）：

- `offline.cs` = 你的私钥；
- `pem_local.cs` = 你的公钥；
- `proxy_data.cs` = **用你的公钥编译出来的** `version.dll`（若提交到仓库，别人拿到的是绑死你公钥的 DLL，对他们无用）。

所以仓库里不含任何人的**私钥**，也不含编译好的代理 DLL。

> `src\pdata.cs` 里保留了生成时的公钥模数（仅用于验签，公开无妨）。换密钥后必须重跑 `gen_payload.py`；
> `build_offline.cmd` 会先做 `--check`，不匹配直接终止，避免编译出「验不过自己的码」的 exe。

---

## 命令行接口

GUI 之外还有一组无界面开关，结果写到 `%TEMP%`，便于脚本化验证。

| 开关 | 作用 | 输出文件 |
|---|---|---|
| `--modes` | 这个 exe 支持哪些补丁方式 | `nvmode.txt`：`PROXY\|available\|len=<n>\|sha256=<hash>` 或 `PROXY\|unavailable\|...` |
| `--dir` | 探测到的 Navicat 目录候选（含得分、`navicat.exe` 版本与最终选中项） | `nvdir.txt`，末行 `PICKED: <path>` |
| `--offline <请求码文件> [email] [company]` | 无界面生成激活码 | `nvoff.txt`：`OFF_OK\|<激活码>` 或 `OFF_FAIL\|<原因>` |
| `--devid` | 本机设备码 | `nvdev.txt` |
| `--lic` | 本地许可文件状态 | `nvlic.txt`：`LOAD_OK\|<code>\|<device>\|<expire>\|<remain>\|siglen=<n>` 或 `LOAD_NULL` / `LOAD_ERR ...` |
| `--vfyfile <文件>` | 校验许可四元组 `<code>\|<device>\|<expire>\|<sig>` | `nvfy.txt`：`VERIFY_OK` / `VERIFY_FAIL` |

> 其余开关（`--uishot`、`--winscan`、`--dlgtest`、`--mediumtest`）是界面/流程自动化测试用的内部诊断入口，一般无需关心。

---

## 项目结构

```
navicat18-offline-activator/
├─ src/
│  ├─ Navicat18Tool.cs     主程序（补丁安装/卸载、公钥替换、命令行入口）
│  ├─ autoact.cs           激活核心流程
│  ├─ form_new.cs          主界面（含目录自动识别）
│  ├─ manual.cs            手动激活窗口
│  ├─ genshin.cs / uia.cs / waitform.cs   自绘 UI 与窗口自动化
│  ├─ pdata.cs             内嵌载荷（.pmod shell、函数序言特征）
│  ├─ lic.cs               离线版授权桩（不做登录/联网校验）
│  ├─ version_proxy.c      version.dll 代理源码
│  └─ NavicatPatch.cs      命令行补丁工具（可选）
├─ tools/
│  ├─ gen_keys.py          生成密钥对 → keys/ + src/pem_local.cs
│  ├─ gen_payload.py       把 shell 内嵌模数换成你的公钥模数
│  ├─ gen_offline.py       用私钥生成 src/offline.cs
│  ├─ gen_proxy_h.py       生成 proxy_data.h（编译代理用）
│  ├─ gen_proxy_cs.py      把 version.dll 转成 src/proxy_data.cs
│  ├─ gen_proxy_stub.py    src/proxy_data.cs 的最小占位实现（没有 MinGW 时用）
│  └─ check_release.py     发布前自检（私钥是否泄露 / 服务端地址残留）
├─ build/
│  ├─ build_proxy.cmd      编译 bin/version.dll
│  └─ build_offline.cmd    编译 bin/Navicat18Tool-Offline.exe
├─ docs/
│  ├─ 原理.md              原理（请求码/激活码模型、补丁机制、代理 DLL）
│  ├─ 使用说明.md          用户手册（6 步流程、两种方式对比、常见问题、状态自检）
│  └─ images/              文档配图（赞助二维码等）
├─ keys/                   （git-ignored，你的私钥）
├─ bin/                    （git-ignored，构建产物）
└─ dist/                   （git-ignored，发布包）
```

---

## 常见问题

**装了 17 又装了 18，目录总被改成 17 的路径？**
v1.0.6 起修好了。自动探测会把注册表里所有候选收集起来、按 `navicat.exe` 文件版本优先选 18；你手动选过或改过目录之后，点【安装补丁】/【开始激活】不会再覆盖。核对方式：`--dir` 看 `nvdir.txt`。

**杀毒软件报毒 / 工具被杀掉？**
`version.dll` 方式的原理与「DLL 劫持」相似，杀软只认特征、分不清意图，属于**误报**。它不联网、不改系统文件、不改 Navicat 磁盘文件；加信任区即可。`libcf.dll` 方式不会有这个问题。

**界面显示激活成功，重启 Navicat 又变回未激活？**
说明补丁没生效或公钥/模数不匹配：确认补丁装到了**当前正在用的那个**安装目录，且构建时跑过 `gen_payload.py`。详见 [docs/使用说明.md](docs/使用说明.md) 的排查章节。

**官方更新 Navicat 之后补丁失效了？**
文件级补丁会被更新覆盖，重装补丁即可；`version.dll` 代理方式不受影响（但它自身也可能随大版本失效）。

**提示「没找到函数序言」？**
两种原因：① 当前装的是另一种方式的补丁（先【卸载补丁】再切）；② Navicat 版本不是 18（本工具只支持 18）。

**目录里有个 `version.dll` / `~nvp_*.tmp`，能删吗？**
`version.dll` 是代理方式的补丁本体，要停用就用【卸载补丁】。`~nvp_*.tmp` 是被加载后无法删除、改名等待重启清理的残留，重启后可删。

**提示「没有检测到「注册」窗口」？**
先在 Navicat 里点【帮助】→【注册】把窗口打开，再回工具点【开始激活】；工具是通过窗口自动化驱动的。

---

## 已知限制

- **只支持 Navicat 18**：目标函数序言是版本相关的，换版本需要重新定位特征字节（改 `src/pdata.cs` 的 `_pro`）。
- **换密钥必须重跑 `gen_payload.py`**：shell 里的模数是编译期写死的，工具不会自动同步。
- 仓库**不附带成品 `version.dll`**：`build\build_offline.cmd` 会用本机 MinGW 现场编译并内嵌；机器上没有 MinGW 时该方式自动停用（`--modes` 报 `PROXY|unavailable`），`libcf.dll` 方式不受影响。
- 发布包只含 `Navicat18Tool-Offline.exe` + `README.txt` + `SHA256SUMS.txt`（不再单独附带 `version.dll`）：代理 DLL 以 OTP 密文内嵌在 exe 里，安装补丁时由工具释放到 Navicat 目录。
- `version.dll` 方式会在安装目录**常驻一个 `version.dll`**，由 Navicat 启动时加载；卸载补丁时删除。
- 两种方式**互斥**：文件级补丁把函数入口改成 `E9` 跳板后，代理 DLL 找不到原序言会报「没找到函数序言」。切换前必须卸载。
- 兜底的 `version.dll` 在被 Navicat 加载后无法删除（Windows 拒绝 `DeleteFile`），只能改名成 `~nvp_*.tmp` 并登记重启后删除。
- 官方更新会覆盖 `libcf.dll` / `navicat.exe`（文件级方式），升级后需重装补丁。
- **私钥在你本机**：任何拿到 `src/offline.cs` 或 `bin/Navicat18Tool-Offline.exe` 的人都能给自己签发激活码。

---

## 安全说明

- 密钥对由 `tools\gen_keys.py` **在本机生成**，私钥不经过网络、不上传任何地方。
- 发布包里的 exe **内嵌你的私钥**（离线签发的代价）。分享给他人的构建等于把签发能力一并给出——请只分发你自己编译的可信包，或改用在线签发架构。
- 代理 DLL 以 OTP 密文内嵌在 exe 中，构建时生成，仓库内不含成品 DLL。
- 请只从本仓库的 Releases 下载发布包，并核对 `SHA256SUMS.txt`；来源不明的构建可能被植入其他代码。

---

## 许可

[PolyForm Noncommercial License 1.0.0](LICENSE)。

- **可以**：个人学习、研究、实验、非营利组织内部使用，以及修改与再分发（需附带许可原文与 `Required Notice`）。
- **不可以**：任何商业使用 —— 含公司内部办公、生产环境、对外服务、付费交付。
- 商业使用需单独授权。

本项目**不是** OSI 认可的开源项目：OSI 的开源定义不允许限制使用领域。准确表述为 **source-available, noncommercial**。

---

## 赞助

这个工具免费、无任何功能限制。如果它帮你省下了买 Navicat 的钱，可以随手打赏一杯胡辣汤 ——
**完全自愿，不打赏也照样能用全部功能**。

| 支付宝 | 微信支付 |
|---|---|
| <img src="docs/images/sponsor-alipay.jpg" width="280" alt="支付宝收款码"> | <img src="docs/images/sponsor-wechat.png" width="280" alt="微信支付收款码"> |

> 打赏不是购买、不构成任何服务承诺，也**不改变上面的非商业许可** —— 商业使用仍然需要单独授权。

---

## 致谢

- 逆向分析与实现、文档与发布流程，都在 **DSH（DeepSeek Harness）+ IG5（Infinite Generation 5）** 工作台内完成。
- 感谢所有提交 issue、反馈「构建步骤有问题」和「目录被改成 17」的用户 —— v1.0.5 / v1.0.6 的修复都来自你们的复现步骤。
