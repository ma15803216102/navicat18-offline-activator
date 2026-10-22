@echo off
rem ============================================================
rem  Navicat 18 offline activator - build script
rem
rem  Requires: .NET Framework 4.x (csc.exe ships with Windows) + Python 3
rem  Run from repo root or double-click; paths are %~dp0 based.
rem
rem  The version.dll (proxy DLL) mode is OPTIONAL, and it is built
rem  HERE automatically - you do NOT have to run build_proxy.cmd first:
rem    * src\proxy_data.cs already there -> used as is
rem    * missing (fresh clone)           -> proxy is built now
rem                                         (build\build_proxy.cmd + gen_proxy_cs.py)
rem    * no MinGW gcc / proxy build fail -> falls back to a stub and still
rem                                         builds; that exe only supports
rem                                         the libcf.dll mode
rem ============================================================
setlocal
cd /d "%~dp0.."

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPFDIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF
if not exist "%CSC%" (
  set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
  set WPFDIR=C:\Windows\Microsoft.NET\Framework\v4.0.30319\WPF
)
if not exist "%CSC%" (
  echo [!!] csc.exe not found. Install .NET Framework 4.x first.
  exit /b 1
)

if not exist src\pem_local.cs (
  echo [!!] src\pem_local.cs missing.
  echo      Run:  python tools\gen_keys.py
  exit /b 1
)
if not exist src\offline.cs (
  echo [!!] src\offline.cs missing.
  echo      Run:  python tools\gen_offline.py
  exit /b 1
)
rem ---- the .pmod shell must carry YOUR modulus, otherwise your own codes will not verify ----
python tools\gen_payload.py --check
if errorlevel 1 (
  echo [!!] src\pdata.cs shell modulus does not match keys\mykey_pub.pem
  echo      Run:  python tools\gen_payload.py
  exit /b 1
)

rem ============================================================
rem  proxy DLL embedding (optional - only used by the version.dll mode)
rem
rem  src\proxy_data.cs is a generated file (git-ignored). It gets rebuilt when
rem  it is missing, when it is the empty stub, or when it came from an older
rem  tools\gen_proxy_cs.py (no ProxyData.Available field).
rem ============================================================
set HAVE_PROXY=0
if not exist src\proxy_data.cs goto :proxy_build
findstr /c:"PROXY_STUB_MARKER" src\proxy_data.cs >nul 2>&1
if not errorlevel 1 goto :proxy_build
findstr /c:"internal static readonly bool Available" src\proxy_data.cs >nul 2>&1
if errorlevel 1 goto :proxy_build
set HAVE_PROXY=1
goto :compile

:proxy_build
echo [proxy] building the version.dll proxy ...
call "%~dp0build_proxy.cmd"
if errorlevel 1 goto :proxy_stub
if not exist bin\version.dll goto :proxy_stub
python tools\gen_proxy_cs.py
if errorlevel 1 goto :proxy_stub
if not exist src\proxy_data.cs goto :proxy_stub
set HAVE_PROXY=1
goto :compile

:proxy_stub
echo [proxy] proxy DLL unavailable - building WITHOUT the version.dll mode.
echo [proxy] reason: MinGW gcc missing, or the proxy failed to compile (see above).
echo [proxy] this exe will only support the libcf.dll (file patch) mode.
python tools\gen_proxy_stub.py --out src\proxy_data.cs
if errorlevel 1 (
  echo BUILD FAILED: cannot write the proxy stub.
  exit /b 1
)

:compile
if not exist bin mkdir bin
if "%HAVE_PROXY%"=="1" (
  echo [build] mode support: libcf.dll + version.dll
) else (
  echo [build] mode support: libcf.dll only
)

"%CSC%" /nologo /target:winexe /win32icon:src\app.ico /optimize+ /define:OFFLINE ^
  /r:System.Numerics.dll ^
  /r:"%WPFDIR%\UIAutomationClient.dll" ^
  /r:"%WPFDIR%\UIAutomationTypes.dll" ^
  /out:bin\Navicat18Tool-Offline.exe ^
  src\Navicat18Tool.cs src\lic.cs src\genshin.cs src\form_new.cs src\pdata.cs ^
  src\uia.cs src\autoact.cs src\offline.cs src\manual.cs src\waitform.cs src\pem_local.cs src\proxy_data.cs

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK  ^-^>  bin\Navicat18Tool-Offline.exe
if not "%HAVE_PROXY%"=="1" echo NOTE: this exe has no embedded proxy DLL - version.dll mode is disabled.
endlocal
