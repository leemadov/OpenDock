@echo off
setlocal
set "ARCH=%~1"
if not defined ARCH set "ARCH=x64"
if /i "%ARCH%"=="arm64" set "VC_ARCH=x64_arm64"
if /i "%ARCH%"=="x64" set "VC_ARCH=x64"
if not defined VC_ARCH (
  echo Native Liquid Glass supports x64 and arm64.
  exit /b 1
)
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS_PATH=%%i"
if not defined VS_PATH (
  echo Native Liquid Glass requires Visual Studio C++ Build Tools and a Windows SDK.
  exit /b 1
)
call "%VS_PATH%\VC\Auxiliary\Build\vcvarsall.bat" %VC_ARCH% >nul || exit /b 1
if not exist "%~dp0..\bin" mkdir "%~dp0..\bin" || exit /b 1
pushd "%~dp0..\bin" || exit /b 1
cl /nologo /std:c++17 /W4 /O2 /fp:precise /MT /LD "%~dp0LiquidGlass.cpp" /link /OUT:GlassDock.LiquidGlass.dll
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
