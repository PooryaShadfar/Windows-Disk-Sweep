@echo off
rem Builds bin\DiskSweep.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No Visual Studio or SDK needed.
setlocal
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" (echo .NET Framework 4.x compiler not found at %FW% & exit /b 1)
if not exist bin mkdir bin
"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /out:bin\DiskSweep.exe /win32icon:assets\DiskSweep.ico ^
  /resource:src\MainWindow.xaml,MainWindow.xaml /resource:assets\DiskSweep.ico,app.ico ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" ^
  src\*.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built %~dp0bin\DiskSweep.exe
