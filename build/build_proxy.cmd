@echo off
rem ============================================================
rem  Build bin\version.dll  (the proxy DLL for the version.dll mode)
rem
rem  You normally do NOT need to run this by hand: build_offline.cmd
rem  calls it automatically when the proxy is missing or stale.
rem
rem  Needs: Python 3 (+ cryptography)  and  MinGW-w64 gcc
rem
rem  gcc lookup order:
rem    1. %GCC%          example:  set GCC=C:\mingw64\bin\gcc.exe
rem    2. gcc.exe on PATH
rem    3. D:\dev\mingw64\bin\gcc.exe
rem
rem  Why the script touches PATH: gcc.exe loads its own DLLs
rem  (libisl-*.dll / libmpc-*.dll / libwinpthread-1.dll ...) through
rem  PATH, NOT from gcc's own directory. If that directory is not on
rem  PATH, gcc exits with code 1 and prints NOTHING - which looks like
rem  a random build failure. So we prepend the gcc folder ourselves.
rem
rem  Output: bin\version.dll   (build_offline.cmd then embeds it)
rem ============================================================
setlocal EnableExtensions
rem keep the caller's directory: "set GCC=mingw64\bin\gcc.exe" is relative to it,
rem and we are about to switch to the repo root
set "ORIGCD=%CD%"
cd /d "%~dp0.."

rem ---- 1) find python -----------------------------------------------
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
  echo [!!] Python 3 not found.
  echo      Install Python 3, or point at it in this window:
  echo        set PYTHON=C:\path\to\python.exe
  exit /b 2
)
%PYRUN% -c "import sys; sys.exit(0 if sys.version_info[0]==3 else 1)" >nul 2>&1
if errorlevel 1 (
  echo [!!] Python 3 is required, but %PYRUN% is not Python 3.
  exit /b 2
)

rem ---- 2) find gcc -------------------------------------------------
if "%GCC%"=="" (
  where gcc >nul 2>&1 && for /f "delims=" %%G in ('where gcc') do if not defined GCC set "GCC=%%G"
)
if "%GCC%"=="" if exist "D:\dev\mingw64\bin\gcc.exe" set "GCC=D:\dev\mingw64\bin\gcc.exe"
if "%GCC%"=="" (
  echo [!!] gcc.exe not found.
  echo      Install MinGW-w64, then either put its bin folder on PATH
  echo      or point at the exe for this window:
  echo        set GCC=C:\mingw64\bin\gcc.exe
  echo.
  echo      Quick options:
  echo        winget install -e --id BrechtSanders.WinLibs.POSIX.UCRT
  echo        or MSYS2 ^(pacman -S mingw-w64-ucrt-x86_64-gcc^)
  exit /b 2
)

rem strip optional quotes the user may have typed
set "GCC=%GCC:"=%"
rem a relative GCC was typed relative to the caller's directory, not the repo root
if not "%GCC:~1,1%"==":" if not "%GCC:~0,2%"=="\\" set "GCC=%ORIGCD%\%GCC%"
if not exist "%GCC%" (
  echo [!!] gcc not found at: %GCC%
  echo      Point at the real path, e.g.:
  echo        set GCC=C:\mingw64\bin\gcc.exe
  exit /b 2
)

rem ---- 3) make gcc's own folder absolute and put it on PATH ---------
for %%I in ("%GCC%") do set "GCCDIR=%%~dpI"
if "%GCCDIR:~-1%"=="\" set "GCCDIR=%GCCDIR:~0,-1%"
pushd "%GCCDIR%" 2>nul || (echo [!!] cannot enter %GCCDIR% & exit /b 2)
set "GCCDIR=%CD%"
popd
set "PATH=%GCCDIR%;%PATH%"

rem ---- 4) gcc must actually run ------------------------------------
"%GCC%" --version >nul 2>&1
if errorlevel 1 (
  echo [!!] gcc exists but cannot run: %GCC%
  echo      Its own DLLs were not loaded. Add this folder to PATH manually:
  echo        set PATH=%GCCDIR%;%%PATH%%
  exit /b 2
)

if not exist bin mkdir bin

echo [1/2] generating bin\proxy_data.h ...
%PYRUN% tools\gen_proxy_h.py --out bin\proxy_data.h
if errorlevel 1 (
  echo FAILED: tools\gen_proxy_h.py ^(is the "cryptography" package installed?^)
  echo        %PYRUN% -m pip install cryptography
  exit /b 1
)

echo [2/2] compiling bin\version.dll ...
copy /y src\version_proxy.c bin\version_proxy.c >nul
"%GCC%" -shared -O2 -o bin\version.dll bin\version_proxy.c -Ibin -lversion -static-libgcc 2> bin\gcc.err
if errorlevel 1 (
  echo BUILD FAILED: gcc could not compile bin\version_proxy.c
  if exist bin\gcc.err type bin\gcc.err
  echo.
  echo      gcc: %GCC%
  echo      if the output above is empty, gcc died before printing anything -
  echo      check that %GCCDIR% is on PATH and that "%%GCC% --version" works.
  exit /b 1
)
if exist bin\gcc.err del /q bin\gcc.err >nul 2>&1

echo BUILD OK  ^-^>  bin\version.dll
echo next: build_offline.cmd ^(it embeds the DLL^)
endlocal
