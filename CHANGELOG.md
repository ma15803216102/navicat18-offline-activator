# 更新日志

本项目的所有重要变更都记录在这里。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 `主.次.修订`。

> 说明：本仓库本地只有 v1.0.0 / v1.0.2 / v1.0.3 / v1.0.5 / v1.0.6 的 tag；v1.0.1 未发布，v1.0.4 只在远端
> 打过 tag（对应移除内存补丁的那次提交）。以 GitHub / Gitee 的 Releases 页面为准。

## [1.0.6] - 2026-10-22

修「同时装了 Navicat 17 和 18 时，目录被改回 17」。

### 修复

- 自动探测按「注册表里第一个碰到的 Navicat 记录」返回，17/18 各留一条记录时经常拿到 17；
  现在收集全部候选（`App Paths` + `Uninstall`，HKLM/HKCU、32/64 位、`InstallLocation` 与 `UninstallString`），
  按 `navicat.exe` 的**文件版本**排序（18 优先、17 减分），路径文本中的 18/17 仅作次要判据。
- 点【安装补丁】/【开始激活】时会无条件重新探测并覆盖目录框，导致用户手填的 18 路径被冲掉；
  现在只在「目录为空或无效，且用户从未手动改过」时才自动填。打字、粘贴、点【选择目录】都算「已手动指定」。
- `InstallLocation` 常见的结尾反斜杠现在会统一去掉。

### 新增

- 日志打印实际目标与版本（如 `目标： ...\navicat.exe  版本 18.0.4.0`），识别成 17 时明确提示。
- 无界面自检 `--dir`：把每个候选目录、得分、`navicat.exe` 版本与最终选中项写入 `%TEMP%\nvdir.txt`。

## [1.0.5] - 2026-10-22

修「按 README 在干净机器上构建会失败」。

### 修复

- 主构建脚本此前要求先手工执行 `build_proxy.cmd` 才能成功；现在 `build_offline.cmd` 自己构建并内嵌代理 DLL，
  缺 MinGW 时降级为占位实现，**构建依然成功**（该 exe 仅支持 `libcf.dll` 方式）。
- `set GCC=<路径>` 单独设置时 gcc 仍会失败：Windows 按 PATH 解析 gcc 依赖的 `libisl` / `libmpc` /
  `libwinpthread`，缺 PATH 时 gcc 退出码 1 且不打印任何信息。现在脚本把编译器目录前置到 PATH、编译前跑
  `gcc --version` 自检，并在失败时原样输出 gcc 的 stderr。
- `set GCC=` 支持带引号与相对路径（相对路径按调用目录解析）。

### 变更

- Python 自动探测：`%PYTHON%` → `where python` → `py -3` → 常见安装目录 → Anaconda。
- README 增加「构建报错对照」表（缺 Python / 缺 cryptography / 缺 gcc / 代理构建失败 / 模数不匹配）。

## [1.0.4] - 2026-10-19

### 变更

- 移除内存补丁（runtime patch），回到 `version.dll` 代理 DLL 方案。
- 代理 DLL 在安装目录常驻，不再在加载后改名。

## [1.0.3] - 2026-10-19

### 变更

- 发布包不再单独附带 `version.dll`：代理 DLL 以 OTP 密文内嵌进 `Navicat18Tool-Offline.exe`，
  安装补丁时由工具释放到 Navicat 安装目录。

## [1.0.2] - 2026-10-19

### 新增

- 内存补丁（runtime patch）：不改磁盘文件、不落 DLL，作为默认的非持久方式。

> 该方式已在 1.0.4 移除：与文件级补丁互斥，且每次重启 Navicat 都要重打。

## [1.0.0] - 2026-10-19

### 新增

- 首个版本：Navicat Premium 18 离线激活。
  - `libcf.dll` 文件级补丁：新增 `.pmod` 节 + 函数入口 inline hook，替换验签模数。
  - `version.dll` 代理 DLL：进程启动时在内存中替换验签模数。
  - 请求码公钥替换（`navicat.exe` 内 460 字节 SPKI PEM）。
  - 编译期校验 `.pmod` shell 里的模数与当前密钥是否一致。
