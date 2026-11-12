# WinKit 系统管家

Windows 11 上管四件事的小工具：**右键菜单**、**此电脑显示项**、**资源管理器左侧导航栏**、**注册表体检**。
所有改动都先存快照，随时可撤销；不联网、不上传、不装服务。

仓库：https://github.com/JourneyHua/WinKit

## 能力边界（先读这段，避免预期落空）

| 你能做的 | 你做不到的（系统限制，不是 bug） |
|---|---|
| 屏蔽第三方软件塞进 Win11 一级菜单的项 | 删掉一级菜单里的**内置项**（复制/粘贴/重命名/共享/发送到/删除/属性）——微软没有开放接口 |
| 禁用/删除/改名经典菜单里的任意项（8 个位置域） | 给右键菜单拖拽排序 |
| 隐藏此电脑里的用户文件夹、系统节点、任意盘符 | 改盘符字母、装卸驱动 |
| 关闭软件钉在左侧导航栏的节点（iCloud Photos、WPS 云盘、应用宝等） | 把任意 exe 钉成导航栏一级图标（要写自定义 CLSID，非官方） |
| 增删快速访问固定项、调整顺序、开关导航窗格选项 | 给右键菜单拖拽排序 |

部分第三方项即使写入屏蔽列表也**不会生效**（更新版 Win11 用 PackagedCom 保护了这些项）。界面会标注，不会假装成功。

## 运行要求

- Windows 11（已在 26H2 / build 26300 上验证）
- **.NET 10 Desktop Runtime**（框架依赖发布，约 2 MB 本体）
  → https://dotnet.microsoft.com/download/dotnet/10.0
- 运行时需要管理员权限（改 HKLM、屏蔽列表、此电脑节点都要写系统区）

## 快速开始

1. 双击 `dist\WinKit.exe`，接受 UAC 提权
2. 左侧选模块；每条改动都会即时生效，多数情况需要点顶部「重启资源管理器」才看得见效果
3. 改错了点顶部「撤销上一步」；想整体回到本次启动前，去「备份与还原」→「回滚本次会话」

## 三个模块怎么用

### 右键菜单

- **一级菜单（右键直接看到的那层）**：不点「显示更多选项」时弹出的紧凑菜单。
  - **数据源是包清单**（`AppxManifest.xml` 里的 `windows.fileExplorerContextMenus`），不是注册表里的 COM 类清单。后者只说明"这个包注册了哪些 COM 类"，会把背景任务、预览器全混进来——这正是之前"显示中却看不到"的原因。
  - 清单里 `<ItemType Type="*">` / `Directory` / `Directory\Background` / `Drive` 就是作用域，左侧域树据此分组。
  - **驱动器会继承「文件夹」作用域的项**：VS Code、PowerRename 只声明了 `Directory`，却照样出现在驱动器右键里。所以驱动器分组 = 声明 `Drive` 的 + 被继承的文件夹项，后者在「继承」列标注。
  - 读不到清单的包单独归入「作用域未确认」并置灰，不假装它一定在菜单里。
  - 勾掉 = 把该软件所有 `IExplorerCommand` 的 CLSID 写进 `Shell Extensions\Blocked`，整块从一级菜单消失。
  - 行内「卸载」= 调它自己的卸载程序（WinKit 不删任何文件），卸载后每 3 秒轮询、最多 10 分钟，自动复查残留并报告。
  - 内置项（打开、复制、粘贴、重命名、共享、发送到、属性、显示更多选项）由 Windows 硬编码，无可移除接口。
- **经典菜单（显示更多选项）**：8 个位置域——文件、文件夹、文件夹空白处、驱动器、桌面空白处、库、发送到、新建。
  - 勾选框 = 启用/禁用（`LegacyDisable` 或屏蔽列表，完全可逆）
  - 「改名」= 写 `MUIVerb`，只改显示名
  - 「删除」= 真删注册表项，但删前自动快照，可撤销
  - 默认视图是「仅第三方 + 高频四域」，因为你的机器上有 2000+ 条，全平铺没法用
- **发送到**：本质是快捷方式文件，禁用 = 移到 `%APPDATA%\WinKit\SendToBackup`
- **新建**：禁用 = 把 `ShellNew` 键改名，数据保留

### 此电脑

- **用户文件夹**：3D 对象、桌面、下载、文档、图片、音乐、视频（ThisPCPolicy）
- **系统节点**：控制面板、网络、第三方注册的命名空间（WPS 云盘、腾讯应用宝等）。隐藏会删注册表项，能撤销
- **驱动器**：隐藏 = 写 `NoDrives` 位掩码。只影响资源管理器显示，不动盘符和数据

### 导航栏

- **导航栏固定项（软件节点）**：iCloud Photos、WPS 云盘、腾讯应用宝文件管理、小米云盘、OneDrive 这类软件自己钉在"此电脑"下面的节点。
  原理是 CLSID 上的 `System.IsPinnedToNameSpaceTree`，关掉 = 把它改成 0，**不删注册表项、不卸载软件**，勾回来即恢复。改完需要重启资源管理器看效果。
- **顽固节点（会自动重建）**：有些软件（典型是 WPS 云盘）每次启动都换一个新 GUID 重新钉上去，所以按单个 ID 关永远关不完。
  这里按显示名把同族 GUID 归组，一次性删掉全部注册项（此电脑 + 导航栏），并能定位/结束重建它的后台进程（如 `wpscloudsvr.exe`）。
  注意：结束进程只影响该软件的后台组件，别去结束你自己正在用的主程序。
- **快速访问固定项**：可增（选文件夹）、删（取消固定）、调顺序（「应用新顺序」会先全部取消再按列表重钉，过程几秒）
- 「最近使用」的条目只能读，取消固定请用资源管理器右键
- 四个导航窗格开关直接写 `Explorer\Advanced`

### 注册表管理

按分类定向扫描，不做全表遍历（单类 3000 条上限，超限会标注结果可能不完整）：

| 分类 | 内容 | 可清理 |
|---|---|---|
| 启动项 | Run / RunOnce / Winlogon | 指向的文件不存在时 |
| 右键菜单 | 各域条目汇总，管理请到右键菜单页 | 否 |
| 文件关联 | `.ext → ProgID → 打开命令` | 否（只报告） |
| 卸载信息 | 卸载程序已失效的记录 | 是 |
| 失效残留 | CLSID 的 InprocServer32 指向的 DLL 不存在 | 是 |
| 策略与限制 | Policies 树，只展示 | 否 |
| 环境变量 / PATH | PATH 里已不存在的目录 | 是（只从 PATH 移除该条） |

**只有「可证明无效」的项才有清理按钮**——证据就是它指向的文件或目录确实不存在。其余一律只读，避免把还能用的配置删掉。删除同样走快照，可撤销。

## 安全机制

- 每次写入前，把**受影响的注册表键**（含子键、值、类型）存成 JSON 快照：`%LOCALAPPDATA%\WinKit\Backups`
- 只存改动涉及的部分，几十 KB 一份，保留最近 50 份
- 「撤销上一步」逐条精确还原；「回滚本次会话」按相反顺序全撤
- 写操作抛异常时**自动回滚**，不会留半截状态
- 全程无网络请求

## 开发

SDK 装在用户目录（避免 UAC）：`%USERPROFILE%\dotnet-sdk\dotnet.exe`，或直接用你机器上任意 `dotnet`（.NET 10 SDK）。

```bash
# 编译
dotnet build WinKit.slnx

# 发布（框架依赖）
dotnet publish src/WinKit/WinKit.csproj -c Release -o dist

# 发布（自包含单目录，免装运行时，约 160 MB）
dotnet publish src/WinKit/WinKit.csproj -c Release -r win-x64 --self-contained true -o dist-standalone

# 界面离屏自检：逐模块渲染 PNG，不弹窗口
dist\WinKit.exe --smoke C:\temp\smoke

# 只读探测：枚举全部菜单项/节点/驱动器/快速访问，落一份报告
dotnet run --project tools/Probe -- report.txt

# 写入自测：只在 HKCU 建临时键，验证快照与撤销
dotnet run --project tools/Probe -- --selftest
```

免提权调试界面：加 `-p:ApplicationManifest=app.preview.manifest` 编译（`app.preview.manifest` 把 `requireAdministrator` 换成 `asInvoker`）。

### 目录

```
src/WinKit/
  Core/        注册表封装、快照引擎、Win32 调用、MVVM 基建
  Services/    右键菜单、新菜单、此电脑、导航栏四个业务层
  ViewModels/  四个模块 + 壳
  Views/       四个 UserControl
  Themes/      颜色与控件样式
tools/Probe/   只读探测 + 写入自测
```

## 已知限制

1. 一级菜单的 PackagedCom 项，阻断可能无效——写 `Shell Extensions\Blocked` 是唯一落地手段，覆盖率取决于系统版本
2. 快速访问排序靠「全部取消后重钉」，期间列表会短暂清空
3. `NoDrives` 是策略键，个别系统需要注销才完全生效
4. 驱动器隐藏只作用于当前用户
