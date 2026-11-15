# WinKit 的 winget 清单

这个目录存放提交到 [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) 的三份清单。
走 winget 分发的目的很实在：**winget 解包时不传播「来自 Internet」标记（MOTW）**，
用户 `winget install JourneyHua.WinKit` 装完双击 `WinKit.exe` 不会再弹 SmartScreen 拦截。

这一点是实测出来的，不是推测：

```
winget download --id BurntSushi.ripgrep.MSVC -d <目录>   → 下载到的 zip 带 MOTW
winget install  --id BurntSushi.ripgrep.MSVC --silent    → 解出来的文件全部 MOTW=no
```

## 三份清单

| 文件 | 作用 |
| --- | --- |
| `JourneyHua.WinKit.yaml` | 版本号、默认语言环境、License |
| `JourneyHua.WinKit.installer.yaml` | 安装器类型（zip + portable）、下载地址、SHA256、运行时依赖 |
| `JourneyHua.WinKit.locale.zh-CN.yaml` | 中文名称、描述、发布说明地址 |

`InstallerType: zip` + `NestedInstallerType: portable` 的取舍：不需要写安装器、不需要数字签名
（社区仓库里 MSIX 必须签名，zip+portable 不必），代价是**不会出现在「设置 → 应用和功能」里**，
也没有桌面快捷方式，升级与卸载都只能用 `winget upgrade` / `winget uninstall`。

## 发版后要做的两步

1. 从 Release 下载 `WinKit-v<版本>-framework-dependent.zip`，算出 SHA256：

   ```powershell
   (Get-FileHash .\WinKit-v1.9.0-framework-dependent.zip -Algorithm SHA256).Hash
   ```

2. 用脚本把版本号和哈希写进清单（比手改靠谱，两处必须一致）：

   ```powershell
   .\update.ps1 -Version 1.9.0 -Zip ..\..\dist\WinKit-v1.9.0-framework-dependent.zip
   ```

然后提 PR 到 `microsoft/winget-pkgs`，路径是
`manifests/j/JourneyHua/WinKit/<版本>/`。首次提交必须用真实的 GitHub 账号（`JourneyHua`），
因为清单里的 `Publisher` 要和提交者身份对得上。

## 本地校验

```powershell
winget validate --manifest .
winget show --manifest .
```

注意：`winget install --manifest <目录>` 需要先以管理员身份执行
`winget settings --enable LocalManifestFiles`，普通权限下会报
`This feature needs to be enabled by administrators`。这台机器上没有管理员权限时，
就用 `winget validate` + `winget show` 校验清单结构，再拿真实的社区包（比如上面那个 ripgrep）
验证「winget 解包不传播 MOTW」这条结论。
