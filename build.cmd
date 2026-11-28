@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=%USERPROFILE%\dotnet-sdk\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo [1/3] 编译...
"%DOTNET%" build src\WinKit\WinKit.csproj -c Release --nologo || goto :fail

echo [2/3] 发布 win-x64 自包含单 EXE...
"%DOTNET%" publish src\WinKit\WinKit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:PublishTrimmed=false -p:PublishAot=false -o dist\.publish-win-x64 --nologo || goto :fail

echo [3/3] 校验单 EXE 并生成更新元数据...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; [xml]$project=Get-Content -LiteralPath 'src\WinKit\WinKit.csproj' -Raw; $version=$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText; if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid release version' }; $publishDir=Join-Path $PWD 'dist\.publish-win-x64'; $files=@(Get-ChildItem -LiteralPath $publishDir -Recurse -File); if ($files.Count -ne 1 -or $files[0].Name -cne 'WinKit.exe') { throw 'Publish output must contain only WinKit.exe' }; $fileName='WinKit-v'+$version+'-win-x64.exe'; $asset=Join-Path $PWD ('dist\'+$fileName); Copy-Item -LiteralPath $files[0].FullName -Destination $asset -Force; & 'tools\StartupProbe\Set-ArtifactIntegrity.ps1' -ExecutablePath $asset; & 'tools\StartupProbe\Invoke-StartupProbe.ps1' -ExecutablePath $asset -ExpectedVersion $version -ReportPath (Join-Path $PWD ('dist\WinKit-v'+$version+'-startup.json')); if ($LASTEXITCODE -ne 0) { throw 'Ordinary startup failed' }; $file=Get-Item -LiteralPath $asset; $metadata=[ordered]@{schemaVersion=1;version=$version;rid='win-x64';fileName=$fileName;sha256=(Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant();size=[long]$file.Length}; $metadataPath=Join-Path $PWD ('dist\WinKit-v'+$version+'-win-x64.update.json'); [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json), (New-Object Text.UTF8Encoding($false))); Remove-Item -LiteralPath $publishDir -Recurse -Force; Write-Host $asset; Write-Host $metadataPath" || goto :fail
echo 完成。EXE 已包含 .NET 运行时，可直接复制运行。
goto :eof

:fail
echo 构建失败，请检查上方错误。
exit /b 1
