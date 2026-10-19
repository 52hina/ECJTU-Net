@echo off
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4 compiler not found.
  pause
  exit /b 1
)
if not exist dist mkdir dist
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ ^
  /out:dist\ECJTU-Net.exe /win32icon:src\ECJTU-Net-small.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Security.dll src\Program.cs
if errorlevel 1 (
  echo [ERROR] Build failed.
  pause
  exit /b 1
)
echo [OK] Build complete: dist\ECJTU-Net.exe
