# WinKit 的 winget 清单

这个目录存放用于提交到 [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) 的三份清单。
本仓清单更新不代表社区源已收录；只有 `winget show --id JourneyHua.WinKit --exact` 能查询到版本后，才可通过 `winget install --id JourneyHua.WinKit --exact` 安装。发布包始终以本仓库 Releases 为准。
此前本机测试观察到 winget 解包没有向内部文件传播「来自 Internet」标记（MOTW）；
这只是该环境的测试结果，SmartScreen 仍受文件声誉、下载标记和本机策略影响，不保证安装后没有安全提示。

此前测试记录：

```
winget download --id BurntSushi.ripgrep.MSVC -d <目录>   → 下载到的 zip 带 MOTW
winget install  --id BurntSushi.ripgrep.MSVC --silent    → 解出来的文件全部 MOTW=no
```

## 三份清单

都在 `manifests/` 子目录里（**不要**把 README 或脚本放在同一目录：`winget validate --manifest <目录>`
会把目录里的文件都当 YAML 解析，遇到 Markdown 直接报
`[YAML:Scanner] ... did not find expected comment or line break`，很难看出是文件名的问题）。

| 文件 | 作用 |
| --- | --- |
| `manifests/JourneyHua.WinKit.yaml` | 版本号、默认语言环境 |
| `manifests/JourneyHua.WinKit.installer.yaml` | 安装器类型（zip + portable）、下载地址、SHA256、运行时依赖 |
| `manifests/JourneyHua.WinKit.locale.zh-CN.yaml` | 中文名称、描述、许可、发布说明地址 |

`InstallerType: zip` + `NestedInstallerType: portable` 的取舍：不需要写安装器、不需要数字签名
（社区仓库里 MSIX 必须签名，zip+portable 不必），代价是**不会出现在「设置 → 应用和功能」里**，
也没有桌面快捷方式，升级与卸载都只能用 `winget upgrade` / `winget uninstall`。

## 发版后更新分发清单

1. 从 Release 下载 `WinKit-v<版本>-framework-dependent.zip`，算出 SHA256：

   ```powershell
   (Get-FileHash .\WinKit-v1.11.0-framework-dependent.zip -Algorithm SHA256).Hash
   ```

2. 用脚本把版本号和哈希写进清单（比手改靠谱，两处必须一致）：

   ```powershell
   .\update.ps1 -Version 1.11.0 -Zip ..\..\dist\WinKit-v1.11.0-framework-dependent.zip
   ```

   脚本按 UTF-8 无 BOM 写回，跑完会把新的 `InstallerSha256` 打印出来。

3. 铺出提 PR 用的目录结构（`manifests/j/JourneyHua/WinKit/<版本>/`）：

   ```powershell
   .\prepare-pr.ps1            # 默认输出到 .\pr-out（已在 .gitignore 里）
   .\prepare-pr.ps1 -Out D:\tmp\wk-pr
   ```

   它会从 version 清单里读 `PackageVersion` 当目录名（不手写，免得版本对不上），
   复制三份 yaml 进去，并打印完整的 fork / 提交 / 开 PR 命令。

提 PR 到 `microsoft/winget-pkgs` 的路径是 `manifests/j/JourneyHua/WinKit/<版本>/`。
首次提交必须用真实的 GitHub 账号（`JourneyHua`），因为清单里的 `Publisher` 要和提交者身份对得上。

## 本地校验

```powershell
winget validate --manifest manifests
winget show --manifest manifests
```

两条命令都跑通过一遍才算清单没写错。踩过的两个坑记在这里：

- `validate` 读整个目录，目录里混了 README 会报 scanner 错误（见上）；
- 依赖不能写在根级 `PackageDependencies:`，那会被判成未知字段
  （`Manifest Warning: Unknown field. [PackageDependencies]`），正确层级是
  `Dependencies:` → `PackageDependencies:` → `- PackageIdentifier`。

注意：`winget install --manifest <目录>` 需要先以管理员身份执行
`winget settings --enable LocalManifestFiles`，普通权限下会报
`This feature needs to be enabled by administrators`。这台机器上没有管理员权限时，
就用 `winget validate` + `winget show` 校验清单结构，再拿真实的社区包（比如上面那个 ripgrep）
验证「winget 解包不传播 MOTW」这条结论。
