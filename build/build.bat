@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Windows built-in C# compiler (no install needed)
set "CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. Enable .NET Framework 4.x in Windows Features.
  pause
  exit /b 1
)

tasklist /FI "IMAGENAME eq AutoLink.exe" 2>nul | find /I "AutoLink.exe" >nul
if not errorlevel 1 (
  echo AutoLink.exe is running. Close it first, then run this again.
  pause
  exit /b 1
)

echo Building AutoLink.exe ...
"%CSC%" /nologo /target:exe /optimize+ /platform:anycpu /codepage:65001 /win32manifest:AutoLink.manifest /win32icon:AutoLink.ico /out:AutoLink.exe Program.cs Outage.cs State.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED
  pause
  exit /b 1
)

echo.
echo BUILD OK: %~dp0AutoLink.exe
echo AutoLink.exe asks for administrator permission (UAC) every time it starts.
pause
