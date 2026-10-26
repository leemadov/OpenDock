@echo off
rem Builds bin\OpenDock.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WPF=%FW%\WPF
if not exist bin mkdir bin
set REFS=/r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:System.Xaml.dll /r:System.Drawing.dll /r:System.Data.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:Microsoft.VisualBasic.dll /r:"%WPF%\UIAutomationClient.dll" /r:"%WPF%\UIAutomationTypes.dll" /r:System.Management.dll /r:System.Web.Extensions.dll
if /i "%1"=="check" (
  "%FW%\csc.exe" /nologo /win32manifest:app.manifest /target:exe /main:OpenDock.Checks /out:bin\checks.exe %REFS% src\*.cs tests\*.cs || exit /b 1
  bin\checks.exe %2
  exit /b
)
"%FW%\csc.exe" /nologo /win32manifest:app.manifest /target:winexe /optimize+ /out:bin\OpenDock.exe %REFS% src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\OpenDock.exe
