# Navicat 18 离线激活工具（开源版）

一个**不需要登录、不需要联网、不需要服务器**的 Navicat Premium 18 离线激活工具。
全部签名都在本机完成——因为你用的是**你自己生成的密钥对**。

支持两种补丁方式：

| | `libcf.dll`（文件级，默认） | `version.dll`（运行时） |
|---|---|---|
| 原理 | 给 `libcf.dll` 新增 `.pmod` 节 + 改函数入口跳板 | 放一个代理 DLL，在 Navicat 进程内改内存 |
| 是否改 Navicat 文件 | **是**（`libcf.dll` + `navicat.exe` 的公钥） | **否**（磁盘文件一个字节都不动） |
| 官方更新后 | 被覆盖，需重装 | 仍然有效 |
| 额外依赖 | 无 | 需要用 MinGW 编译代理 DLL（仓库不附带成品） |
| 卸载 | 拷回 `.orig` | 删掉 `version.dll` |

> ⚠️ 仅供学习研究、自有软件与已授权环境的兼容性测试使用。
> 请勿用于侵犯他人软件著作权或规避商业授权。

---

## 它做什么

Navicat 的「手动激活」是标准的 **请求码 ↔ 激活码** 模型：

1. 客户端用**内嵌公钥**加密含机器信息的 JSON → **请求码**；
2. 厂商用**私钥**解开、构造许可 JSON、再用私钥签名 → **激活码**；
3. 客户端用**内嵌验签模数**验证激活码，通过后写入许可。

本工具把这两把「厂商的钥匙」换成**你自己的**：

| 位置 | 原本 | 替换成 | 做法 |
|---|---|---|---|
| `navicat.exe` 里的请求码公钥 | 厂商 RSA-2048 SPKI PEM | 你的公钥（同长度 460 字节覆盖） | 文件替换 / 内存替换 |
| `libcf.dll` 里的验签模数 | 厂商模数（运行时才有） | 你的模数（小端 256 字节） | `.pmod` 跳板 / 运行时 shell |

`libcf.dll` 的跳板（核心）：

- 在可执行节里按 **21 字节函数序言** 定位 `decrypt_RSA_NON_STD`（Navicat 18 中 RVA `0xEA850`，仅作参考，实际靠特征匹配）；
- 新增可执行节 `.pmod`（352 字节 shell，**偏移 `0x5F` 处内嵌你的 256 字节模数**）；
- 函数入口前 21 字节改成 `E9 rel32` + 16×`NOP`，跳到 `.pmod`；
- shell 保存现场 → 把调用现场对象里的验签模数换成你的 → 执行原序言 → `jmp` 回 `入口+21`。

于是 Navicat 自己的 RSA 校验会用**你的模数**验**你用自己私钥签出的激活码**——天然通过。

---

## 环境要求

- Windows 10 / 11 x64
- .NET Framework 4.x（系统自带 `csc.exe`，无需 SDK）
- Python 3，以及下面任意一样用于生成密钥：
  - Python 的 `cryptography` 库，**或** `openssl`（Git for Windows 自带）
- （仅 `version.dll` 方式需要）MinGW-w64 的 `gcc`，默认路径 `D:\dev\mingw64\bin\gcc.exe`，可用 `set GCC=...` 覆盖

---

## 构建

```bat
:: 1) 生成你自己的 RSA-2048 密钥对（一次性）→ keys\ + src\pem_local.cs
python tools\gen_keys.py

:: 2) 把 .pmod shell 里的模数换成你的公钥模数（必须做，否则签出来的码验不过）
python tools\gen_payload.py

:: 3) 用私钥生成离线签发实现 → src\offline.cs
python tools\gen_offline.py

:: 4) 编译运行时代理（仅 version.dll 方式需要）→ bin\version.dll
build\build_proxy.cmd

:: 5) 把代理 DLL 转成可内嵌的字节数组 → src\proxy_data.cs
python tools\gen_proxy_cs.py

:: 6) 编译主程序 → bin\Navicat18Tool-Offline.exe
build\build_offline.cmd
```

`src\offline.cs`、`src\pem_local.cs`、`src\proxy_data.cs` 都是**本机生成物**（已在 `.gitignore`）：

- `offline.cs` = 你的私钥；
- `pem_local.cs` = 你的公钥；
- `proxy_data.cs` = **用你的公钥编译出来的** `version.dll`（若提交到仓库，别人拿到的会是绑死你公钥的 DLL，对他们无用）。

所以仓库里不含任何人的**私钥**，也不含编译好的代理 DLL。

> `src/pdata.cs` 里保留了生成时的公钥模数（仅用于验签，公开无妨）。换密钥后必须重跑 `gen_payload.py`；`build_offline.cmd` 会先做 `--check`，不匹配直接终止，避免编译出「验不过自己的码」的 exe。

---

## 使用

1. 右键 **以管理员身份运行** `bin\Navicat18Tool-Offline.exe`（写 `Program Files` 需要管理员）；
2. 选择激活方式：
   - **libcf.dll（默认）**：点【安装补丁】→ 备份并改写 `libcf.dll` / `navicat.exe`；
   - **version.dll**：点【安装补丁】→ 只往安装目录放一个 `version.dll`（需先完成构建第 4、5 步）；
3. 启动 Navicat → 【帮助】→【注册】；
4. 按住 **Ctrl+Shift** 点【激活】→「手动激活」窗口，复制**请求码**；
5. 回工具：粘贴请求码、填邮箱/公司 → 生成**激活码**；
6. 把激活码粘回 Navicat → 完成。

卸载：工具里【卸载补丁】会把 `.orig` 拷回 / 删除代理 DLL。

---

## 目录结构

```
navicat18-offline-activator/
├─ src/
│  ├─ Navicat18Tool.cs     主程序（补丁安装/卸载、公钥替换、自检入口）
│  ├─ autoact.cs           激活核心流程
│  ├─ form_new.cs          主界面
│  ├─ manual.cs            手动激活窗口
│  ├─ genshin.cs / uia.cs / waitform.cs   自绘 UI 与窗口自动化
│  ├─ pdata.cs             内嵌载荷（.pmod shell、函数序言特征）
│  ├─ lic.cs               离线版授权桩（不做登录/联网校验）
│  ├─ version_proxy.c      version.dll 代理源码（运行时补丁）
│  └─ NavicatPatch.cs      命令行补丁工具（可选）
├─ tools/
│  ├─ gen_keys.py          生成密钥对 → keys/ + src/pem_local.cs
│  ├─ gen_payload.py       把 shell 内嵌模数换成你的公钥模数
│  ├─ gen_offline.py       用私钥生成 src/offline.cs
│  ├─ gen_proxy_h.py       生成 proxy_data.h（编译代理用）
│  ├─ gen_proxy_cs.py      把 version.dll 转成 src/proxy_data.cs
│  └─ check_release.py     发布前自检（私钥是否泄露 / 服务端地址残留）
├─ build/
│  ├─ build_proxy.cmd      编译 bin/version.dll
│  └─ build_offline.cmd    编译 bin/Navicat18Tool-Offline.exe
├─ docs/原理.md
├─ keys/                   （git-ignored）
└─ bin/                    （git-ignored）
```

---

## 已知限制

- **只支持 Navicat 18**：目标函数序言是版本相关的，换版本需要重新定位特征字节（改 `src/pdata.cs` 的 `_pro`）。
- **换密钥必须重跑 `gen_payload.py`**：shell 里的模数是编译期写死的，工具不会自动同步。
- `version.dll` 方式需要自己编译代理 DLL（本仓库不附带成品，原因见上）。
- 官方更新会覆盖 `libcf.dll` / `navicat.exe`（文件级方式），升级后需重装补丁。
- 私钥在你本机：任何拿到 `src/offline.cs` 或 `bin/Navicat18Tool-Offline.exe` 的人都能给自己签发激活码。

---

## 免责声明

本项目仅用于**学习、研究与自有/已授权环境**的兼容性测试。使用者应自行确保其行为符合当地法律与软件许可协议。
作者不对任何滥用行为负责。
