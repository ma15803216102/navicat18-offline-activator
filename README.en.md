# Navicat 18 Offline Activator

**No account. No network. No server.** Every signature is produced on your own machine — with a key pair that *you* generate.

[![Latest release](https://img.shields.io/github/v/release/ma15803216102/navicat18-offline-activator?label=release)](https://github.com/ma15803216102/navicat18-offline-activator/releases)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0.0-orange)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-blue)](#requirements)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4)](https://dotnet.microsoft.com/)

[简体中文](README.md) ｜ **English**

Repositories: [GitHub](https://github.com/ma15803216102/navicat18-offline-activator) ｜ [Gitee](https://gitee.com/mzy666666/navicat18-offline-activator) (both are kept in sync)

> **License**: PolyForm Noncommercial License 1.0.0 (see [LICENSE](LICENSE)) — source-available with **no commercial use**.
> This is **not** an OSI-approved open-source license: the OSI definition does not allow field-of-use restrictions.
> The accurate description is **source-available, noncommercial**. Commercial use requires a separate license.
>
> **Disclaimer**: intended for study, research, and interoperability testing on software you own or are authorized to test.
> Navicat is commercial software by PremiumSoft. Do not use this project to infringe its copyright or to circumvent
> commercial licensing.

---

## Table of Contents

- [Features](#features)
- [How it works](#how-it-works)
- [Patch modes](#patch-modes)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Usage](#usage)
- [Building from source](#building-from-source)
- [Command-line interface](#command-line-interface)
- [Project layout](#project-layout)
- [FAQ](#faq)
- [Known limitations](#known-limitations)
- [Security notes](#security-notes)
- [License](#license)
- [Sponsor](#sponsor)
- [Acknowledgements](#acknowledgements)

---

## Features

- **Fully offline** — no network, no login, no backend service. Activation completes locally.
- **Two patch modes** — file-level `libcf.dll` (default) and a `version.dll` proxy DLL. Pick either; switching requires an uninstall first.
- **One-command build** — `build\build_offline.cmd` compiles and embeds the proxy DLL automatically. Without MinGW it degrades to "`libcf.dll` only" and **still builds successfully**.
- **Install-path detection that respects you** — with both Navicat 17 and 18 installed, the tool picks **18** by inspecting `navicat.exe`'s file version, and never overwrites a folder you selected by hand.
- **Machine-readable self-checks** — `--modes`, `--dir` and friends write results to `%TEMP%`, so you can script verification and troubleshooting.
- **No leftovers** — no service, no kernel driver, no resident process. The tool exits cleanly; only the patch itself stays in the Navicat folder.

---

## How it works

Navicat's "manual activation" is the classic **request code ↔ activation code** scheme:

1. The client encrypts a machine-info JSON with its **embedded public key** → the **request code**.
2. The vendor decrypts it with the **private key**, builds a license JSON, and signs it with the same private key → the **activation code**.
3. The client verifies the activation code with its **embedded verification modulus**, then persists the license.

This tool replaces both vendor keys with **yours**:

| Location | Original | Replaced with | Mechanism |
|---|---|---|---|
| Request-code public key inside `navicat.exe` | Vendor RSA-2048 SPKI PEM | Your public key (same-length, 460-byte overwrite) | File or in-memory replacement |
| Verification modulus inside `libcf.dll` | Vendor modulus (constructed at runtime) | Your modulus (256 bytes, little-endian) | `.pmod` trampoline / runtime shell |

The `libcf.dll` **inline hook** (an entry-point hook / detour) in four steps:

1. Locate `decrypt_RSA_NON_STD` in the executable section by its **21-byte function prologue** (RVA `0xEA850` in Navicat 18 — reference only; the tool matches bytes, not addresses).
2. Append an executable section `.pmod` (352-byte shell, with **your 256-byte modulus embedded at offset `0x5F`**).
3. Overwrite the first 21 bytes of the function with `E9 rel32` (jmp) + 16 × `NOP`, branching into `.pmod`.
4. The shell saves the context, swaps the verification modulus inside the caller's object for yours, runs the original prologue, then `jmp`s back to `entry + 21`.

Navicat's own RSA check therefore verifies **an activation code signed by your private key** against **your modulus** — it passes by construction.

Deeper details (field layout, block-length conditions, version byte) are in [docs/原理.md](docs/原理.md) (Chinese).

---

## Patch modes

| | `libcf.dll` (file-level, default) | `version.dll` (proxy DLL) |
|---|---|---|
| Mechanism | Append a `.pmod` section to `libcf.dll` and inline-hook the function entry | Drop a proxy DLL next to the executable; the loader loads it at startup and patches in-process |
| Modifies Navicat files | **Yes** (`libcf.dll` + the public key in `navicat.exe`) | **No** (only adds `version.dll`) |
| After an official update | Overwritten — reinstall the patch | Still works |
| Extra dependency | None | Needs MinGW to compile the proxy DLL (no prebuilt binary in the repo; it is embedded into the exe at build time) |
| On-disk footprint | Patched DLL + `.orig` backup | One resident `version.dll` (deleted on uninstall) |
| Uninstall | Restore `.orig` | Delete `version.dll` |

The two modes are **mutually exclusive**: once the file-level patch has turned the function entry into an `E9`
trampoline, the proxy DLL can no longer find the original prologue and reports "function prologue not found".
Use **Uninstall patch** before switching.

---

## Requirements

**Running the release build**

- Windows 10 / 11 x64
- Administrator rights (writing into `Program Files`)

**Building**

- .NET Framework 4.x — ship with Windows, `csc.exe` is enough (no SDK required)
- Python 3 with `cryptography`, or `openssl` (bundled with Git for Windows)
  - The scripts auto-detect `python` (PATH → `py -3` → common install dirs). Override with `set PYTHON=C:\Python313\python.exe`.
- *(Optional — only for the `version.dll` mode)* MinGW-w64 `gcc`
  - `winget install -e --id BrechtSanders.WinLibs.POSIX.UCRT`, or `set GCC=C:\mingw64\bin\gcc.exe`
  - You do **not** need to add MinGW to the system PATH — the build script prepends the compiler's directory itself and self-checks it.

---

## Quick start

1. Grab the latest `Navicat18Tool-Offline-v*.zip` from [Releases](https://github.com/ma15803216102/navicat18-offline-activator/releases) (or [Gitee releases](https://gitee.com/mzy666666/navicat18-offline-activator/releases)).
2. Unzip and verify `Navicat18Tool-Offline.exe` against `SHA256SUMS.txt`.
3. Right-click `Navicat18Tool-Offline.exe` → **Run as administrator**.
4. Follow [Usage](#usage).

> Prefer building it yourself? See [Building from source](#building-from-source).

---

## Usage

> For the full user manual (screenshots, failure recovery, uninstall) see **[docs/使用说明.md](docs/使用说明.md)** (Chinese). Quick version below.

1. Right-click **Run as administrator** on `Navicat18Tool-Offline.exe`.
2. Choose a patch mode:
   - **`libcf.dll` (default)** — click *Install patch*; `libcf.dll` and `navicat.exe` are backed up and rewritten.
   - **`version.dll` (proxy DLL)** — click *Install patch*; the tool closes Navicat if it is running, then drops `version.dll` into the install folder. It stays resident and patches every future Navicat start, so no reinstall is needed.
3. Start Navicat → **Help → Registration**.
4. Hold **Ctrl+Shift** and click **Activate** → the *Manual activation* dialog appears; copy the **request code**.
5. Back in the tool: paste the request code, fill in e-mail/company → generate the **activation code**.
6. Paste the activation code into Navicat → done.

Uninstall: the tool's *Uninstall patch* restores `.orig` files or deletes the proxy DLL.

> **Navicat 17 and 18 both installed?** Both leave registry entries behind; the tool ranks candidates by the
> `navicat.exe` file version and picks **18**. Once you have selected or edited the folder yourself,
> *Install patch* / *Start activation* will **not** overwrite it. Inspect the decision with
> `Navicat18Tool-Offline.exe --dir` → `%TEMP%\nvdir.txt`.

---

## Building from source

```bat
:: 1) Generate your own RSA-2048 key pair (one-time) -> keys\ + src\pem_local.cs
python tools\gen_keys.py

:: 2) Embed your public modulus into the .pmod shell (mandatory, otherwise signed codes won't verify)
python tools\gen_payload.py

:: 3) Generate the offline signing implementation from your private key -> src\offline.cs
python tools\gen_offline.py

:: 4) Compile the tool -> bin\Navicat18Tool-Offline.exe
build\build_offline.cmd
```

Step 4 **also handles the `version.dll` proxy**: it compiles it and converts it into an embedded byte array.
You therefore do **not** need to run `build\build_proxy.cmd` or `python tools\gen_proxy_cs.py` by hand
(both are kept for debugging the proxy in isolation).

The script detects three machine states:

| Machine state | Result |
|---|---|
| MinGW gcc present, `src\proxy_data.cs` missing | Compiles the proxy and embeds it → both modes available |
| Already embedded (`src\proxy_data.cs` valid) | Reused, no recompilation |
| **No MinGW gcc** (or proxy build failed) | Falls back to a stub implementation and **still builds**; that exe supports `libcf.dll` only, and switching to `version.dll` in the UI is refused with an explanation |

Verify what a given exe supports:

```bat
bin\Navicat18Tool-Offline.exe --modes
:: writes %TEMP%\nvmode.txt
::   PROXY|available|len=<n>|sha256=<hash>   -> both modes available
::   PROXY|unavailable|len=0|...             -> libcf.dll only
```

### Build error reference

| Error | Cause / fix |
|---|---|
| `'python' is not recognized` | Python not on PATH. `set PYTHON=C:\Python313\python.exe` and rerun (the script also tries `py -3` and common install dirs). |
| `FAILED: tools\gen_proxy_h.py` | Missing `cryptography`: `python -m pip install cryptography` |
| `[!!] gcc.exe not found` | MinGW missing or not on PATH. `set GCC=C:\mingw64\bin\gcc.exe` and rerun. |
| `[proxy] version.dll could NOT be built` | Consequence of the previous line: the exe still builds, but supports `libcf.dll` only. Install gcc and rerun. |
| `BUILD FAILED: gcc could not compile ...` | gcc runs but fails; the script prints gcc's own stderr. |
| `[!!] src\pdata.cs shell modulus does not match ...` | Key changed without rerunning `python tools\gen_payload.py`. |

To build the proxy DLL alone with full output: run `build\build_proxy.cmd` directly (accepts `set GCC=` with absolute or
relative paths, quoted or unquoted).

### Why gcc needs a correct PATH

`build_proxy.cmd` supports `set GCC=C:\path\to\gcc.exe`, but that variable alone used to be insufficient:
`gcc.exe` loads `libisl-*.dll`, `libmpc-*.dll` and `libwinpthread-1.dll` from its own directory, and Windows resolves
those DLLs through **PATH**, not through the executable's directory. With MinGW's `bin` missing from PATH, gcc
**exits with code 1 and prints nothing** — which looks like a mysterious build failure.

The script now prepends the compiler directory to PATH, runs `gcc --version` as a self-check, and surfaces gcc's
stderr on failure. Manual verification:

```bat
set GCC=C:\path\to\mingw64\bin\gcc.exe
build\build_proxy.cmd
:: expected last line: BUILD OK  ->  bin\version.dll
```

### Generated files

`src\offline.cs`, `src\pem_local.cs` and `src\proxy_data.cs` are **machine-local artifacts** (already in `.gitignore`):

- `offline.cs` — your private key;
- `pem_local.cs` — your public key;
- `proxy_data.cs` — the `version.dll` **compiled against your public key** (committing it would only ship a DLL locked to your key).

No private key and no prebuilt proxy DLL are ever committed.

> `src\pdata.cs` keeps the generation-time public modulus (verification only — safe to publish). After changing keys you
> must rerun `gen_payload.py`; `build_offline.cmd` runs a `--check` first and aborts on mismatch, so it cannot produce an
> exe that fails to verify its own codes.

---

## Command-line interface

A set of headless switches mirrors the GUI; results go to `%TEMP%` for scripting.

| Switch | Purpose | Output |
|---|---|---|
| `--modes` | Which patch modes this exe supports | `nvmode.txt`: `PROXY\|available\|len=<n>\|sha256=<hash>` or `PROXY\|unavailable\|...` |
| `--dir` | Detected Navicat folder candidates (score, `navicat.exe` version, final pick) | `nvdir.txt`, last line `PICKED: <path>` |
| `--offline <request-code-file> [email] [company]` | Generate an activation code without the GUI | `nvoff.txt`: `OFF_OK\|<code>` or `OFF_FAIL\|<reason>` |
| `--devid` | Machine device ID | `nvdev.txt` |
| `--lic` | Local license-file state | `nvlic.txt`: `LOAD_OK\|<code>\|<device>\|<expire>\|<remain>\|siglen=<n>`, or `LOAD_NULL` / `LOAD_ERR ...` |
| `--vfyfile <file>` | Verify a `<code>\|<device>\|<expire>\|<sig>` tuple | `nvfy.txt`: `VERIFY_OK` / `VERIFY_FAIL` |

> Other switches (`--uishot`, `--winscan`, `--dlgtest`, `--mediumtest`) are internal diagnostics used by the UI/flow
> test suite and normally don't need attention.

---

## Project layout

```
navicat18-offline-activator/
├─ src/
│  ├─ Navicat18Tool.cs     main program (patch install/uninstall, key replacement, CLI entry)
│  ├─ autoact.cs           activation flow
│  ├─ form_new.cs          main window (incl. install-path detection)
│  ├─ manual.cs            manual-activation dialog
│  ├─ genshin.cs / uia.cs / waitform.cs   custom-drawn UI and window automation
│  ├─ pdata.cs             embedded payload (.pmod shell, prologue signature)
│  ├─ lic.cs               offline license stub (no login, no network)
│  ├─ version_proxy.c      version.dll proxy source
│  └─ NavicatPatch.cs      command-line patcher (optional)
├─ tools/
│  ├─ gen_keys.py          generate key pair -> keys/ + src/pem_local.cs
│  ├─ gen_payload.py       embed your modulus into the shell
│  ├─ gen_offline.py       generate src/offline.cs from the private key
│  ├─ gen_proxy_h.py       generate proxy_data.h for the proxy build
│  ├─ gen_proxy_cs.py      convert version.dll into src/proxy_data.cs
│  ├─ gen_proxy_stub.py    minimal stub for src/proxy_data.cs (no MinGW)
│  └─ check_release.py     pre-release checks (private key leakage, leftover server URLs)
├─ build/
│  ├─ build_proxy.cmd      build bin/version.dll
│  └─ build_offline.cmd    build bin/Navicat18Tool-Offline.exe
├─ docs/
│  ├─ 原理.md              internals (request/activation model, patch mechanics, proxy DLL)
│  ├─ 使用说明.md          user manual (6-step flow, mode comparison, FAQ, status checks)
│  └─ images/              documentation images
├─ keys/                   (git-ignored — your private key)
├─ bin/                    (git-ignored — build output)
└─ dist/                   (git-ignored — release packages)
```

---

## FAQ

**Both Navicat 17 and 18 are installed and the folder keeps snapping back to 17.**
Fixed in v1.0.6. Detection now collects every registry candidate and ranks them by `navicat.exe`'s file version, so 18 wins; a folder you selected or typed yourself is never overwritten. Inspect the decision with `--dir` (`nvdir.txt`).

**Antivirus flags the tool or kills it.**
The `version.dll` mode looks like DLL hijacking to signature-based engines — a **false positive**. It makes no network calls, modifies no system files and touches no Navicat file on disk; add an exclusion. The `libcf.dll` mode does not trigger this.

**Activation reports success in the UI but Navicat is unlicensed after a restart.**
The patch did not apply, or the public key/modulus pair does not match: confirm the patch went into the install folder you are actually launching, and that `gen_payload.py` was rerun after changing keys. See the troubleshooting section of [docs/使用说明.md](docs/使用说明.md).

**An official Navicat update broke the patch.**
The file-level patch is overwritten by updates — reinstall it. The `version.dll` proxy survives updates (though it may itself break across major versions).

**"Function prologue not found".**
Either the other mode's patch is installed (uninstall first, then switch), or the Navicat version is not 18 (only 18 is supported).

**There is a `version.dll` / `~nvp_*.tmp` in the folder — can I delete it?**
`version.dll` is the proxy patch itself; remove it via *Uninstall patch*. `~nvp_*.tmp` is a renamed leftover that could not be deleted while loaded; it can be removed after a reboot.

**"Registration window not found".**
Open it first: Navicat → **Help → Registration**, then click *Start activation* in the tool — the flow is driven through window automation.

---

## Known limitations

- **Navicat 18 only** — the target prologue is version-specific; a new version needs new signature bytes (`_pro` in `src/pdata.cs`).
- **Changing keys requires rerunning `gen_payload.py`** — the shell's modulus is baked in at build time.
- **No prebuilt `version.dll` ships in this repo** — `build\build_offline.cmd` compiles it with your local MinGW. Without MinGW that mode is disabled (`--modes` reports `PROXY|unavailable`); the `libcf.dll` mode is unaffected.
- Release packages contain only `Navicat18Tool-Offline.exe`, `README.txt` and `SHA256SUMS.txt` (no standalone `version.dll`): the proxy is embedded in the exe as OTP ciphertext and dropped into the Navicat folder at install time.
- The `version.dll` mode leaves one **resident `version.dll`** in the install folder; it is deleted on uninstall.
- The two modes are **mutually exclusive** — an installed `E9` trampoline makes the proxy report "prologue not found". Uninstall before switching.
- A loaded `version.dll` cannot be deleted by Windows (`DeleteFile` fails mid-session); it is renamed to `~nvp_*.tmp` and scheduled for deletion on reboot.
- Official updates overwrite `libcf.dll` / `navicat.exe` (file-level mode) — reinstall the patch afterwards.
- **The private key lives on your machine**: anyone with `src/offline.cs` or `bin/Navicat18Tool-Offline.exe` can sign activation codes for themselves.

---

## Security notes

- The key pair is generated locally by `tools\gen_keys.py`; the private key never traverses the network.
- Release executables **embed your private key** — that is the cost of offline signing. Sharing such a build hands over signing capability. Distribute only builds you compiled yourself, or adopt an online-signing architecture.
- The proxy DLL is embedded as OTP ciphertext; no compiled DLL is committed.
- Download releases only from this repository and verify `SHA256SUMS.txt`. Builds of unknown origin may contain additional code.

---

## License

[PolyForm Noncommercial License 1.0.0](LICENSE).

- **Permitted**: personal study, research, experiments, internal use by non-profit organizations, plus modification and redistribution (with the license text and the Required Notice).
- **Not permitted**: any commercial use — including internal business operations, production environments, hosted services, or paid delivery.
- Commercial use requires a separate license.

This project is **not** OSI-approved open source: the OSI definition forbids field-of-use restrictions. The accurate
label is **source-available, noncommercial**.

---

## Sponsor

The tool is free and fully functional. If it saved you the price of a Navicat license, you can buy the author a bowl of
hulatang (spicy soup) — **entirely optional; every feature works either way**.

| Alipay | WeChat Pay |
|---|---|
| <img src="docs/images/sponsor-alipay.jpg" width="280" alt="Alipay QR code"> | <img src="docs/images/sponsor-wechat.png" width="280" alt="WeChat Pay QR code"> |

> A tip is not a purchase, carries no service commitment, and **does not change the noncommercial license** above —
> commercial use still requires a separate license.

---

## Acknowledgements

- Reverse engineering, implementation, documentation and release automation were all done inside the
  **DSH (DeepSeek Harness) + IG5 (Infinite Generation 5)** workbench.
- Thanks to everyone who filed issues — the build fixes in v1.0.5 and the install-path fix in v1.0.6 both came from
  user-provided reproduction steps.
