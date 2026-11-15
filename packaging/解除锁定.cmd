@echo off
setlocal
rem ---------------------------------------------------------------------------
rem  Removes the "downloaded from the Internet" mark (MOTW / Zone.Identifier)
rem  from every file in this folder, so Windows SmartScreen stops showing
rem  "Windows protected your PC" every time WinKit.exe starts.
rem
rem  This .cmd file is intentionally ASCII-only and does no chcp: cmd re-reads
rem  batch files by byte offset, so switching the console code page mid-file
rem  makes it split multi-byte lines and report the tail as an unknown command
rem  ("'...' is not recognized as an internal or external command").
rem  All Chinese text lives in unblock.ps1, which Windows PowerShell reads as
rem  UTF-8 because the file carries a BOM.
rem ---------------------------------------------------------------------------
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0unblock.ps1"
set "wk_rc=%ERRORLEVEL%"
echo.
pause
exit /b %wk_rc%
