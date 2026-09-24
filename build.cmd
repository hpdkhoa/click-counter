@echo off
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set CSC=%FW%\csc.exe
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:ClickCounter.exe ^
  /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\PresentationCore.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  ClickCounter.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built ClickCounter.exe
