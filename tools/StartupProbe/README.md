# 普通启动验收

[Invoke-StartupProbe.ps1](Invoke-StartupProbe.ps1) 直接启动待交付的 WinKit EXE，观察真实主窗口和系统概览导航，再正常关闭本次创建的进程。它使用 Windows PowerShell 5.1 兼容语法。PowerShell 7 动态编译使用该宿主 `ref` 目录中的配套引用程序集，避免运行时 TPA 转发到被编译器排除的 CoreLib；UI Automation 与 WindowsBase 使用实际加载的程序集，并排除同名引用冲突。

在仓库根目录、可交互的 Windows 桌面中执行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/StartupProbe/Invoke-StartupProbe.ps1 -ExecutablePath "dist/WinKit-v1.14.2-win-x64.exe" -ExpectedVersion 1.14.2 -RequireMediumIntegrity -ReportPath "dist/startup-probe/release-default.json"
~~~

将路径替换为真正准备交付的 EXE。不要用另一次构建、框架依赖 DLL 或已有的 WinKit 窗口替代。未指定报告位置时，脚本在仓库的 `dist/startup-probe/<unique-id>/report.json` 写入报告；具体路径会以 `STARTUP_PROBE_REPORT=...` 输出。帮助命令为 `-Help`。

## 验收内容

- 以 `UseShellExecute=false`、空应用参数启动指定 EXE，工作目录为该 EXE 所在目录。脚本保留继承的环境，不设置 `TEMP`、`TMP`、`DOTNET_BUNDLE_EXTRACT_BASE_DIR`、`DOTNET_ROOT` 或隐藏原生启动错误的变量。
- 并行读取标准输出和标准错误，保留原生 host 在 C# 入口执行前写出的错误。每个流最多保存 1,048,576 个字符；截断或读取异常都会导致验收失败。
- 核对启动 PID、映像绝对路径、进程创建时间及 token 的用户 SID 和精确的预期完整性级别。默认 `-ExpectedProcessIntegrity Observer` 保持原有同级要求；发布入口显式使用 `-ExpectedProcessIntegrity Medium`，只接受同一用户、RID 8192 的应用，观察器本身限 Medium 或 High；该模式还要求观察器和应用均为已知的非 AppContainer、非受限 token，RestrictedSids 为空。报告分别记录期望 RID、观察器与应用 token，并保留 RestrictedSids、IsRestricted、ElevationType 和 IsAppContainer。`-RequireMediumIntegrity` 额外要求观察器为 Medium（RID 8192）；脚本不修改任何 token。
- 最多等待 90 秒（可用 `-TimeoutSeconds 5..90` 缩短），每 100 毫秒检查进程退出和窗口身份。窗口必须属于本次 PID，标题为“WinKit 系统管家”，可见、启用，并通过 UI Automation 的可交互检查。UI Automation 在后台线程执行，调用停滞也不会绕过等待截止时间。
- 找到 AutomationId 为 `overview` 的系统概览按钮并实际调用 InvokePattern，核对其 ItemStatus 为“当前页面”。必要时会先展开“体检与安全”组。这是普通界面导航，可能按应用既有行为保存选中页面和导航组展开状态；不会执行优化、清理或权限修复。
- 同一窗口连续通过检查至少 3 秒后，发送 `CloseMainWindow`，等待最多 20 秒并要求进程以 0 正常退出。最后重新核对 EXE 的 SHA-256，避免验收期间文件被替换。

退出码 0 表示以上验收全部通过；其他情况均以 1 退出，并尽量保存失败报告。失败清理只处理该脚本创建且 PID、创建时间、映像路径、SID 和完整性级别仍匹配的进程，优先正常关闭，必要时终止这个 PID。脚本不按进程名清理，不终止进程树，也不关闭其他 WinKit 实例。

## 报告与版本

JSON 包含 SHA-256、ProductVersion、FileVersion、继承的相关环境变量、观察器/子进程 token、窗口与导航检查结果、稳定时间、原生输出、十进制/十六进制退出码、正常关闭与失败清理结果。进程若过早退出而无法查询 token，对应字段可能为空；原生输出和退出码仍会保留。

指定 `-ExpectedVersion 1.14.2` 时，ProductVersion 必须恰为 `1.14.2`，FileVersion 必须恰为 `1.14.2.0`。版本参数只接受规范的稳定版 `major.minor.patch`。未指定时只记录版本，不检查版本相等。

本地普通用户验收应在未提升的 Medium 桌面运行，并使用 `-RequireMediumIntegrity`。High CI 观察器启动已标记 Medium 的交付文件时，应用会正常成为 Medium；发布脚本显式指定 `-ExpectedProcessIntegrity Medium` 验证这一结果，而不是把它误判为同级启动失败。这份报告会同时保留 High 观察器与 Medium 应用的事实，不能替代本地未提升观察器的验收。Low、System 等观察器不能使用这个发布模式；Low、High、其他用户的应用也不能通过其精确身份检查。

## 完整性标签造成的原生启动失败

本仓库曾出现交付 EXE 继承工作区 Low Mandatory Label 的情况：Medium PowerShell 启动后，原生 host 的 token 实际为 Low（RID 4096），用户 SID 相同且 RestrictedSids 为空。默认用户临时目录为 Medium，host 因而无法创建单文件解包目录，报告 Errorcode 5，且尚未进入应用 C# 启动逻辑。

把 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 改到可写的 Low 工作区，可以让该 EXE 启动，却掩盖了默认临时目录下的交付缺陷。发布前应核对最终 EXE 的完整性标签，并使用未覆写临时目录/解包路径的环境对同一个文件重新验收；构建与发布门槛应保证交付物具有预期的 Medium 标签。不要通过放宽用户临时目录权限来替代修正交付物。

本脚本只观察，不修改 EXE 的完整性标签、DACL、临时目录权限、环境变量或用户 token。若调用者本身已设置解包路径或临时目录变量，脚本会如实继承并记录；这种结果应注明为诊断环境，不能作为默认环境普通启动通过的证据。

`Set-ArtifactIntegrity.ps1` 只调整明确指定的输出 EXE 的标签，并核对 SHA-256、所有者、组、原始 DACL 规则字节与控制标志。Windows 设置标签时可能将 `DiscretionaryAclAutoInherited` 从未设置归一化为已设置；检查仅忽略这一状态位，访问规则、顺序、继承保护等实质变化仍视为失败。

标签校验回归使用隔离文件夹具，可在 Windows PowerShell 5.1 或 PowerShell 7 下运行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/StartupProbe/Test-ArtifactIntegrity.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/StartupProbe/Test-StartupIdentity.ps1
~~~

身份策略回归只加载实际的纯策略函数，覆盖普通同级、High 观察器与 Medium 应用、错误 SID、错误 RID 和不支持的观察器；不会启动应用或更改 token。
