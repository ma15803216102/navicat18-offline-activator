@echo off
rem ============================================================
rem  Build the version.dll proxy (runtime patch mode)
rem
rem  Requires MinGW-w64 gcc. Default: D:\dev\mingw64\bin\gcc.exe
rem  Override with:  set GCC=C:\path\to\gcc.exe
rem
rem  IMPORTANT - why "set GCC=..." alone is not enough:
rem    gcc.exe itself depends on DLLs shipped next to it
rem    (libisl-*.dll / libmpc-*.dll / libwinpthread-1.dll / ...).
rem    Windows resolves those through PATH, NOT through the directory
rem    of the executable being started. So if the MinGW bin directory is
rem    not on PATH, gcc exits with code 1 and prints absolutely nothing
rem    (no error text at all) - which looks like a random build failure.
rem    This script adds the gcc directory to PATH by itself.
rem
rem  Output: bin\version.dll   then run: python tools\gen_proxy_cs.py
rem ============================================================
setlocal
cd /d "%~dp0.."

rem ---- 1) locate gcc -----------------------------------------------
if "%GCC%"=="" set GCC=D:\dev\mingw64\bin\gcc.exe
if not exist "%GCC%" (
  for %%I in (gcc.exe) do if not "%%~$PATH:I"=="" set GCC=%%~$PATH:I
)
if not exist "%GCC%" (
  echo [!!] gcc.exe not found. Install MinGW-w64, or point at it:
  echo        set GCC=C:\path\to\gcc.exe
  exit /b 1
)

rem ---- 2) put gcc's own directory on PATH (needed for its DLLs) ----
for %%I in ("%GCC%") do set "GCCDIR=%%~dpI"
if "%GCCDIR:~-1%"=="\" set "GCCDIR=%GCCDIR:~0,-1%"
set "PATH=%GCCDIR%;%PATH%"

rem ---- 3) sanity check: gcc must actually run ----------------------
"%GCC%" --version >nul 2>&1
if errorlevel 1 (
  echo [!!] gcc.exe found but cannot run:
  echo        %GCC%
  echo      Its own DLLs were not found. Add that directory to PATH manually:
  echo        set PATH=C:\path\to\mingw64\bin;^<the rest of PATH^>
  exit /b 1
)

if not exist bin mkdir bin

echo [1/2] generating bin\proxy_data.h ...
python tools\gen_proxy_h.py --out bin\proxy_data.h
if errorlevel 1 ( echo FAILED: proxy_data.h & exit /b 1 )

echo [2/2] compiling bin\version.dll ...
copy /y src\version_proxy.c bin\version_proxy.c >nul
"%GCC%" -shared -O2 -o bin\version.dll bin\version_proxy.c -Ibin -lversion -static-libgcc 2> bin\gcc.err
if errorlevel 1 (
  echo BUILD FAILED
  if exist bin\gcc.err type bin\gcc.err
  exit /b 1
)
if exist bin\gcc.err del /q bin\gcc.err >nul 2>&1

echo BUILD OK  ^-^>  bin\version.dll
echo next: python tools\gen_proxy_cs.py
endlocal
