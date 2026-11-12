@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=%USERPROFILE%\dotnet-sdk\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo [1/3] 编译...
"%DOTNET%" build src\WinKit\WinKit.csproj -c Release --nologo || goto :fail

echo [2/3] 发布框架依赖版本到 dist\ ...
"%DOTNET%" publish src\WinKit\WinKit.csproj -c Release -o dist --nologo || goto :fail

echo [3/3] 完成。可执行文件：dist\WinKit.exe
echo 需要 .NET 10 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/10.0
goto :eof

:fail
echo 构建失败，请检查上方错误。
exit /b 1
