@echo off
REM dt - dynamics-xpp CLI (Windows shim).
REM
REM Locates PowerShell and execs dt.ps1 with all arguments. The real
REM implementation lives in dt.ps1 -- this shim exists so 'dt' works from cmd
REM and so ~/.local/bin/dt.cmd has a stable target to forward to.
REM
REM Prefers PowerShell 7+ (pwsh) when present, but dt.ps1 is written to run
REM under Windows PowerShell 5.1 too, which is what every dev box already has.

setlocal
set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%dt.ps1"

if not exist "%PS_SCRIPT%" (
    echo dt: cannot find dt.ps1 at %PS_SCRIPT% 1>&2
    exit /b 1
)

REM Note the goto-based structure rather than parenthesized if-blocks: inside a
REM block, %errorlevel% is expanded when the block is PARSED, not when the
REM command runs, so "exit /b %errorlevel%" would always return the code from
REM before PowerShell ran -- turning every failure into a silent success.

where pwsh >nul 2>nul
if errorlevel 1 goto :use_windows_powershell

pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
exit /b %errorlevel%

:use_windows_powershell
where powershell >nul 2>nul
if errorlevel 1 goto :no_powershell

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
exit /b %errorlevel%

:no_powershell
echo dt: PowerShell was not found on PATH. 1>&2
exit /b 1
