<#
.SYNOPSIS
    把发版产物里的版本号与 SHA256 写进 winget 清单。

.DESCRIPTION
    三份清单里有四处要跟版本走：PackageVersion（三份文件各一处）、InstallerUrl 里的版本、
    InstallerSha256。手改容易漏，漏了会被 winget-pkgs 的校验打回，所以用脚本一次改全。

.EXAMPLE
    .\update.ps1 -Version 1.9.0 -Zip ..\..\dist\WinKit-v1.9.0-framework-dependent.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Zip
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号要写成 1.9.0 这样：$Version" }
if (-not (Test-Path -LiteralPath $Zip)) { throw "找不到安装包：$Zip" }

$resolved = (Resolve-Path -LiteralPath $Zip).Path
$hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
Write-Host "安装包：$resolved"
Write-Host "SHA256：$hash"

# 先把原来的版本号读出来，好把 URL 里的旧版本换掉（不能盲目替换所有数字）
$versionFile = Join-Path $here 'JourneyHua.WinKit.yaml'
$oldVersion = (Select-String -LiteralPath $versionFile -Pattern '^PackageVersion:\s*(\S+)').Matches[0].Groups[1].Value
Write-Host "清单原版本：$oldVersion"

foreach ($file in Get-ChildItem -LiteralPath $here -Filter 'JourneyHua.WinKit*.yaml') {
    $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    $text = $text -replace "(?m)^PackageVersion:\s*$([regex]::Escape($oldVersion))\s*$", "PackageVersion: $Version"
    $text = $text -replace "v$([regex]::Escape($oldVersion))/WinKit-v$([regex]::Escape($oldVersion))-framework-dependent\.zip",
                            "v$Version/WinKit-v$Version-framework-dependent.zip"
    $text = $text -replace '(?m)^(\s*InstallerSha256:\s*)[0-9A-Fa-f]+$', "`${1}$hash"
    [IO.File]::WriteAllText($file.FullName, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "已更新：$($file.Name)"
}

Write-Host ''
Write-Host '现在可以校验清单：'
Write-Host '  winget validate --manifest .'
Write-Host '  winget show --manifest .'
