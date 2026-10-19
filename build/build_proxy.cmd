@echo off
rem ============================================================
rem  Build the version.dll proxy (runtime patch mode)
rem  Requires MinGW-w64 gcc (default: D:\dev\mingw64\bin\gcc.exe,
rem  override with:  set GCC=C:\path\to\gcc.exe)
rem  Output: bin\version.dll   then run: python tools\gen_proxy_cs.py
rem ============================================================
setlocal
cd /d "%~dp0.."

if "%GCC%"=="" set GCC=D:\dev\mingw64\bin\gcc.exe
if not exist "%GCC%" (
  for %%I in (gcc.exe) do if not "%%~$PATH:I"=="" set GCC=%%~$PATH:I
)
if not exist "%GCC%" (
  echo [!!] gcc.exe not found. Install MinGW-w64 or:  set GCC=C:\path\to\gcc.exe
  exit /b 1
)
if not exist bin mkdir bin

echo [1/2] generating bin\proxy_data.h ...
python tools\gen_proxy_h.py --out bin\proxy_data.h
if errorlevel 1 ( echo FAILED: proxy_data.h & exit /b 1 )

echo [2/2] compiling bin\version.dll ...
copy /y src\version_proxy.c bin\version_proxy.c >nul
"%GCC%" -shared -O2 -o bin\version.dll bin\version_proxy.c -Ibin -lversion -static-libgcc
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )

echo BUILD OK  ^-^>  bin\version.dll
echo next: python tools\gen_proxy_cs.py
endlocal
