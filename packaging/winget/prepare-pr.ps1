# 把 packaging/winget/manifests/ 里的三份清单铺成 winget-pkgs 仓库要求的目录结构，
# 方便直接复制进 fork 里提 PR。
#
# winget-pkgs 的路径规则是：manifests/<标识符第一个字母小写>/<发布者>/<包名>/<版本>/
#   manifests/j/JourneyHua/WinKit/1.10.1/
#       JourneyHua.WinKit.yaml
#       JourneyHua.WinKit.installer.yaml
#       JourneyHua.WinKit.locale.zh-CN.yaml
#
# 用法：pwsh -File prepare-pr.ps1 [-Out .\pr-out]

[CmdletBinding()]
param(
    [string] $Out = (Join-Path $PSScriptRoot 'pr-out')
)

$ErrorActionPreference = 'Stop'

$manifestDir = Join-Path $PSScriptRoot 'manifests'
if (-not (Test-Path -LiteralPath $manifestDir)) {
    throw "找不到清单目录：$manifestDir"
}

# 版本号从 version 清单里读，避免和目录名不一致
$versionFile = Join-Path $manifestDir 'JourneyHua.WinKit.yaml'
$versionLine = Select-String -LiteralPath $versionFile -Pattern '^PackageVersion:\s*(\S+)' | Select-Object -First 1
if ($null -eq $versionLine) { throw "读不到 PackageVersion：$versionFile" }
$version = $versionLine.Matches[0].Groups[1].Value

$target = Join-Path $Out (Join-Path 'manifests\j\JourneyHua\WinKit' $version)
New-Item -ItemType Directory -Path $target -Force | Out-Null

Get-ChildItem -LiteralPath $manifestDir -Filter '*.yaml' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name) -Force
    "已复制：$($_.Name)"
}

""
"目标目录：$target"
"版本：$version"
""
@"
下一步（需要你自己的 GitHub 账号，PR 的 Publisher 必须和提交者身份对得上）：

  # 1. 先本地校验一遍（缺 winget 就在「应用安装程序」里更新一下）
  winget validate --manifest "$target"

  # 2. fork microsoft/winget-pkgs，克隆自己的 fork
  gh repo fork microsoft/winget-pkgs --clone
  cd winget-pkgs

  # 3. 复制清单进去（保持目录层级）
  New-Item -ItemType Directory -Force "manifests\j\JourneyHua\WinKit\$version"
  Copy-Item "$target\*.yaml" "manifests\j\JourneyHua\WinKit\$version\"

  # 4. 提交并开 PR
  git checkout -b JourneyHua.WinKit-$version
  git add "manifests/j/JourneyHua/WinKit/$version"
  git commit -m "New package: JourneyHua.WinKit version $version"
  git push -u origin JourneyHua.WinKit-$version
  gh pr create --repo microsoft/winget-pkgs --title "New package: JourneyHua.WinKit version $version" --body "见 manifests 目录。WinKit 是 Windows 系统管理面板，zip + portable 分发，MIT 许可。"
"@
