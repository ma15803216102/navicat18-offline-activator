@echo off
rem ============================================================
rem  Navicat 18 offline activator - build script
rem  Requires: .NET Framework 4.x (csc.exe ships with Windows)
rem  Run from repo root or double-click; paths are %~dp0 based.
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

if not exist src\proxy_data.cs (
  echo [!!] src\proxy_data.cs missing.
  echo      Run:  build\build_proxy.cmd   then   python tools\gen_proxy_cs.py
  exit /b 1
)
if not exist bin mkdir bin

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
endlocal
