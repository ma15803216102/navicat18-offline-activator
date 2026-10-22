@echo off
rem ============================================================
rem  Navicat 18 offline activator - main build
rem
rem  Needs: .NET Framework 4.x (csc.exe ships with Windows) + Python 3
rem  Optional: MinGW-w64 gcc (only for the version.dll proxy mode)
rem
rem  ONE COMMAND IS ENOUGH:
rem      build\build_offline.cmd
rem
rem  The proxy DLL is built (and embedded) automatically:
rem    * src\proxy_data.cs already there  -> used as is
rem    * missing / stale (fresh clone)     -> build\build_proxy.cmd runs now
rem    * no MinGW / proxy build fails      -> stub is generated, exe still
rem                                          builds, but only the libcf.dll
rem                                          mode works (version.dll mode is
rem                                          refused in the UI)
rem
rem  You can pre-set gcc / python for this window if the auto-detection
rem  misses them:
rem      set GCC=C:\mingw64\bin\gcc.exe
rem      set PYTHON=C:\Python313\python.exe
rem ============================================================
setlocal EnableExtensions
set "ORIGCD=%CD%"
cd /d "%~dp0.."

rem ---- 1) csc.exe (ships with .NET Framework 4.x) -------------------
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

rem ---- 2) find python ----------------------------------------------
set "PYRUN="
if not "%PYTHON%"=="" set "PYRUN="%PYTHON%""
if not defined PYRUN (
  for /f "delims=" %%P in ('where python 2^>nul') do if not defined PYRUN set "PYRUN="%%P""
)
if not defined PYRUN (
  where py >nul 2>&1 && set "PYRUN=py -3"
)
if not defined PYRUN (
  for /d %%D in ("%LOCALAPPDATA%\Programs\Python\Python3*") do (
    if not defined PYRUN if exist "%%~fD\python.exe" set "PYRUN="%%~fD\python.exe""
  )
)
if not defined PYRUN if exist "%USERPROFILE%\anaconda3\python.exe" set "PYRUN="%USERPROFILE%\anaconda3\python.exe""
if not defined PYRUN if exist "%ProgramData%\anaconda3\python.exe" set "PYRUN="%ProgramData%\anaconda3\python.exe""
if not defined PYRUN (
  echo [!!] Python 3 not found. Install Python 3, or in this window:
  echo        set PYTHON=C:\path\to\python.exe
  exit /b 2
)
%PYRUN% -c "import sys; sys.exit(0 if sys.version_info[0]==3 else 1)" >nul 2>&1
if errorlevel 1 (
  echo [!!] Python 3 is required, but %PYRUN% is not Python 3.
  exit /b 2
)

rem ---- 3) your own key material must be generated first -------------
if not exist src\pem_local.cs (
  echo [!!] src\pem_local.cs missing.
  echo      Run:  %PYRUN% tools\gen_keys.py
  exit /b 1
)
if not exist src\offline.cs (
  echo [!!] src\offline.cs missing.
  echo      Run:  %PYRUN% tools\gen_offline.py
  exit /b 1
)
rem the .pmod shell must carry YOUR modulus, otherwise your own codes will not verify
%PYRUN% tools\gen_payload.py --check
if errorlevel 1 (
  echo [!!] src\pdata.cs shell modulus does not match keys\mykey_pub.pem
  echo      Run:  %PYRUN% tools\gen_payload.py
  exit /b 1
)

rem ---- 4) proxy DLL: reuse, rebuild, or fall back to a stub ----------
set HAVE_PROXY=0
if not exist src\proxy_data.cs goto :proxy_build
findstr /c:"PROXY_STUB_MARKER" src\proxy_data.cs >nul 2>&1
if not errorlevel 1 goto :proxy_build
findstr /c:"internal static readonly bool Available" src\proxy_data.cs >nul 2>&1
if errorlevel 1 goto :proxy_build
set HAVE_PROXY=1
goto :compile

:proxy_build
echo [proxy] building bin\version.dll (MinGW gcc) ...
call "%~dp0build_proxy.cmd"
if errorlevel 1 goto :proxy_stub
if not exist bin\version.dll goto :proxy_stub
%PYRUN% tools\gen_proxy_cs.py
if errorlevel 1 goto :proxy_stub
if not exist src\proxy_data.cs goto :proxy_stub
set HAVE_PROXY=1
goto :compile

:proxy_stub
echo.
echo [proxy] version.dll could NOT be built - continuing WITHOUT it.
echo [proxy] the resulting exe supports ONLY the libcf.dll (file patch) mode.
echo [proxy] to enable it later: install MinGW-w64 gcc, then re-run this script
echo [proxy]   (auto-detected), or set GCC and run build\build_proxy.cmd first:
echo [proxy]     set GCC=C:\mingw64\bin\gcc.exe
echo [proxy]     build\build_proxy.cmd
echo [proxy]     build\build_offline.cmd
echo.
%PYRUN% tools\gen_proxy_stub.py --out src\proxy_data.cs
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
  echo BUILD FAILED: csc reported errors above.
  exit /b 1
)
echo BUILD OK  ^-^>  bin\Navicat18Tool-Offline.exe
if not "%HAVE_PROXY%"=="1" echo NOTE: no embedded proxy - version.dll mode is disabled in this exe.
endlocal
