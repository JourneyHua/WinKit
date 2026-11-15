@echo off
chcp 65001 >nul
setlocal
echo.
echo    WinKit 解除锁定
echo    ----------------
echo    从浏览器下载的压缩包解压后，文件会带一个「来自 Internet」的标记，
echo    Windows 会因此认为它「不安全」，于是每次启动都弹 SmartScreen 拦截窗口。
echo    这个脚本把本目录（含子目录）所有文件的标记去掉，之后双击 WinKit.exe 就不会再弹了。
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d = Split-Path -Parent -LiteralPath '%~f0'; $n = 0; Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { if ($_.FullName -ne '%~f0') { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue; $n++ } }; Write-Host ('已处理 ' + $n + ' 个文件。现在可以直接双击 WinKit.exe。')"
echo.
pause
