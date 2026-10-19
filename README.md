# Navicat 18 离线激活工具（开源版）

一个**不需要登录、不需要联网、不需要服务器**的 Navicat Premium 18 离线激活工具。
全部签名都在本机完成——因为你用的是**你自己生成的密钥对**。

> ⚠️ 仅供学习研究、自有软件与已授权环境的兼容性测试使用。
> 请勿用于侵犯他人软件著作权或规避商业授权。

---

## 它做什么

Navicat 的「手动激活」是一个标准的 **请求码 ↔ 激活码** 模型：

1. 客户端用**内嵌公钥**加密一段含机器信息的 JSON，得到 **请求码**；
2. 你把请求码发给厂商，厂商用**私钥**解开、构造许可 JSON、再用私钥签名，得到 **激活码**；
3. 客户端用**内嵌的验签模数**验证激活码，通过后写入本地许可。

本工具把上面两把「厂商的钥匙」换成**你自己的**：

| 位置 | 原本 | 替换成 | 做法 |
|---|---|---|---|
| `navicat.exe` 里的请求码公钥 | 厂商 RSA-2048 SPKI PEM | 你的公钥（同长度 460 字节覆盖） | 文件替换 |
| `libcf.dll` 里的验签模数 | 厂商模数（不在文件里，运行时才有） | 你的模数 | 新增 `.pmod` 节 + 入口跳板 |

`libcf.dll` 的改法（核心）：

- 在可执行节里按 **21 字节函数序言** 定位 `decrypt_RSA_NON_STD`（Navicat 18 中 RVA `0xEA850`，只作参考，实际靠特征匹配）；
- 新增一个可执行节 `.pmod`（352 字节跳板 + 内嵌你的 2048 位模数）；
- 把函数入口前 21 字节改成 `E9 rel32` + 16×`NOP`，跳到 `.pmod`；
- 跳板在调用现场把验签对象里的模数换成你的，然后执行被覆盖掉的原序言，再 `jmp` 回 `入口+21` 继续执行原函数。

于是 Navicat 自己的 RSA 校验会用**你的模数**去验**你用自己私钥签出的激活码**——天然通过，且不改动任何许可判断逻辑。

---

## 环境要求

- Windows 10 / 11 x64
- .NET Framework 4.x（系统自带 `csc.exe`，无需装 SDK）
- Python 3（脚本用），以及下面任意一样用于生成密钥：
  - Python 的 `cryptography` 库，**或**
  - `openssl`（Git for Windows 自带：`D:\software\Git\usr\bin\openssl.exe`）

---

## 三步构建

```bat
:: 1) 生成你自己的 RSA-2048 密钥对（一次性；产物在 keys\ 与 src\pem_local.cs）
python tools\gen_keys.py

:: 2) 用私钥生成离线签发实现（产物 src\offline.cs）
python tools\gen_offline.py

:: 3) 编译离线版（产物 bin\Navicat18Tool-Offline.exe）
build\build_offline.cmd
```

三条纪律：

- `keys\mykey_priv.pem` 与 `src\offline.cs` 是**私钥的两种形式**，已在 `.gitignore` 里，**永远不要提交或外传**；
- 换一台机器重新构建 = 重新生成一对密钥（旧激活码不再通用，这是预期行为）；
- 仓库本身不含任何人的私钥/公钥，每个人用自己的。

---

## 使用

1. 右键 **以管理员身份运行** `bin\Navicat18Tool-Offline.exe`（写 `Program Files` 需要管理员）；
2. 点【安装补丁】。它会：
   - 结束正在运行的 Navicat（否则文件被占用）；
   - 首次安装时把 `libcf.dll` / `navicat.exe` 备份成 `.orig`；
   - 给 `libcf.dll` 加 `.pmod` 节、替换 `navicat.exe` 里的公钥；
3. 启动 Navicat → 菜单【帮助】→【注册】；
4. 按住 **Ctrl+Shift** 点【激活】→ 进入「手动激活」窗口，复制**请求码**；
5. 回到工具：粘贴请求码、填邮箱/公司 → 生成**激活码**；
6. 把激活码粘回 Navicat → 激活完成。

卸载/还原：工具里的【卸载补丁】会把 `.orig` 拷回，删除 `.pmod` 补丁。

---

## 目录结构

```
navicat18-offline-activator/
├─ src/
│  ├─ Navicat18Tool.cs     主程序（自检入口 / 补丁安装 / 公钥替换）
│  ├─ autoact.cs           激活核心流程（补丁检查→手动激活→请求码→激活码）
│  ├─ form_new.cs          主界面
│  ├─ manual.cs            手动激活窗口（请求码/激活码）
│  ├─ genshin.cs           自绘 UI 基类与控件
│  ├─ uia.cs               UI Automation 封装（识别 Navicat 窗口/控件）
│  ├─ waitform.cs          等待窗口
│  ├─ pdata.cs             内嵌载荷（.pmod 跳板字节、函数序言特征）
│  ├─ lic.cs               离线版授权桩（离线版不做登录/联网校验）
│  ├─ NavicatPatch.cs      独立的命令行补丁工具（可选）
│  └─ app.ico
├─ tools/
│  ├─ gen_keys.py          生成 RSA-2048 密钥对 → keys/ 与 src/pem_local.cs
│  └─ gen_offline.py       用私钥生成 src/offline.cs
├─ build/
│  └─ build_offline.cmd    编译离线版
├─ docs/
│  └─ 原理.md              更细的技术说明
├─ keys/                   （git-ignored）私钥/公钥
└─ bin/                    （git-ignored）编译产物
```

---

## 已知限制

- **只支持 Navicat 18**：目标函数序言是版本相关的，换版本需要重新定位特征字节。
- **不包含「运行时 version.dll 代理」方式**：那条路线需要一个单独编译的代理 DLL，本仓库不附带编译好的二进制。
- 官方更新会覆盖 `libcf.dll` / `navicat.exe`，升级后需要重新安装补丁。
- 私钥在你本机：任何拿到 `src/offline.cs` 或 `bin\*.exe` 的人都能给自己签发激活码。

---

## 免责声明

本项目仅用于**学习、研究与自有/已授权环境**的兼容性测试。使用者应自行确保其行为符合当地法律与软件许可协议。
作者不对任何滥用行为负责。
