using System.Diagnostics;
using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class TweakSwitchItem : ObservableObject
{
    private bool _isOn;

    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public HiveKind Hive { get; init; } = HiveKind.CurrentUser;
    public string SubKey { get; init; } = "";
    public string ValueName { get; init; } = "";
    public int OnValue { get; init; }
    public int OffValue { get; init; }
    public bool DefaultIsOn { get; init; }
    public bool RequiresExplorerRestart { get; init; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            _isOn = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<TweakSwitchItem, bool>? ToggleRequested { get; set; }

    public string StatusText => IsOn ? "已开启" : "已关闭";
    public string RegistryPath => RegPath.Of(Hive, SubKey).FullPath;
}

public sealed class PortProcessItem : ObservableObject
{
    public string Protocol { get; init; } = "";
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public string State { get; init; } = "";
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";

    public bool HasExecutable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
    public bool CanKill => Pid > 4;

    public string SearchBlob => $"{Protocol} {LocalAddress} {LocalPort} {RemoteAddress} {State} {Pid} {ProcessName} {ExecutablePath}".ToLowerInvariant();
}

public sealed class AdminPanelShortcut
{
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string CommandLabel { get; init; } = "";
    public string Description { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Arguments { get; init; } = "";
}

public sealed class SystemTweakService
{
    private readonly ChangeJournal _journal;

    public SystemTweakService(ChangeJournal journal) => _journal = journal;

    public List<TweakSwitchItem> LoadTweaks()
    {
        const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string cab = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState";
        const string search = @"Software\Microsoft\Windows\CurrentVersion\Search";
        const string desktopIcons = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string advInfo = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo";
        const string engage = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement";

        var specs = new List<TweakSwitchItem>
        {
            // 1. 资源管理器体验
            new()
            {
                Id = "show_file_ext",
                Category = "资源管理器体验",
                Title = "显示已知文件类型的扩展名 (.exe / .txt / .docx)",
                Description = "防止伪装成文档图标的可执行程序欺骗（对应 HideFileExt = 0）",
                SubKey = adv,
                ValueName = "HideFileExt",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "show_hidden_files",
                Category = "资源管理器体验",
                Title = "显示隐藏的文件和文件夹 (AppData 等)",
                Description = "在资源管理器中直接显示隐藏属性的文件与目录（对应 Hidden = 1）",
                SubKey = adv,
                ValueName = "Hidden",
                OnValue = 1,
                OffValue = 2,
                DefaultIsOn = false
            },
            new()
            {
                Id = "launch_to_this_pc",
                Category = "资源管理器体验",
                Title = "打开资源管理器时默认进入「此电脑」而非「主页」",
                Description = "按 Win+E 打开资源管理器时直接展示磁盘分区（对应 LaunchTo = 1）",
                SubKey = adv,
                ValueName = "LaunchTo",
                OnValue = 1,
                OffValue = 2,
                DefaultIsOn = false
            },
            new()
            {
                Id = "full_path_title",
                Category = "资源管理器体验",
                Title = "在窗口标题栏显示完整文件夹路径",
                Description = "方便直接识别当前所处的完整目录层级（对应 FullPath = 1）",
                SubKey = cab,
                ValueName = "FullPath",
                OnValue = 1,
                OffValue = 0,
                DefaultIsOn = false
            },
            new()
            {
                Id = "auto_check_select",
                Category = "资源管理器体验",
                Title = "使用复选框选择文件项",
                Description = "鼠标悬停文件时显示多选复选框（对应 AutoCheckSelect = 1）",
                SubKey = adv,
                ValueName = "AutoCheckSelect",
                OnValue = 1,
                OffValue = 0,
                DefaultIsOn = false
            },

            // 2. 任务栏与桌面体验
            new()
            {
                Id = "taskbar_left",
                Category = "任务栏与桌面体验",
                Title = "任务栏图标靠左对齐（关闭则为 Win11 默认居中）",
                Description = "将开始菜单与任务栏图标恢复到经典左下角位置（对应 TaskbarAl = 0）",
                SubKey = adv,
                ValueName = "TaskbarAl",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "hide_taskbar_search",
                Category = "任务栏与桌面体验",
                Title = "隐藏任务栏上的「搜索框」按钮",
                Description = "释放任务栏空间，仍可按 Win+S 唤出搜索（对应 SearchboxTaskbarMode = 0）",
                SubKey = search,
                ValueName = "SearchboxTaskbarMode",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "hide_taskbar_widgets",
                Category = "任务栏与桌面体验",
                Title = "隐藏任务栏上的「小组件 (Widgets)」按钮",
                Description = "关闭任务栏天气与资讯信息流入口（对应 TaskbarDa = 0）",
                SubKey = adv,
                ValueName = "TaskbarDa",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "hide_task_view",
                Category = "任务栏与桌面体验",
                Title = "隐藏任务栏上的「任务视图」按钮",
                Description = "隐藏虚拟桌面切换图标，仍可按 Win+Tab 使用（对应 ShowTaskViewButton = 0）",
                SubKey = adv,
                ValueName = "ShowTaskViewButton",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "desktop_icon_thispc",
                Category = "任务栏与桌面体验",
                Title = "在桌面上显示「此电脑」图标",
                Description = "控制桌面「此电脑」系统图标可见性",
                SubKey = desktopIcons,
                ValueName = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "desktop_icon_controlpanel",
                Category = "任务栏与桌面体验",
                Title = "在桌面上显示「控制面板」图标",
                Description = "控制桌面经典「控制面板」系统图标可见性",
                SubKey = desktopIcons,
                ValueName = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },

            // 3. 系统隐私与去广告
            new()
            {
                Id = "disable_start_ads",
                Category = "系统隐私与去广告",
                Title = "关闭开始菜单中的应用推广与推荐建议",
                Description = "禁止在开始菜单推荐栏插入商店推广应用（对应 SubscribedContent-338388Enabled = 0）",
                SubKey = cdm,
                ValueName = "SubscribedContent-338388Enabled",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "disable_lockscreen_tips",
                Category = "系统隐私与去广告",
                Title = "关闭锁屏界面上的推广提示与花絮",
                Description = "保持锁屏画面干净无广告文字（对应 RotatingLockScreenOverlayEnabled = 0）",
                SubKey = cdm,
                ValueName = "RotatingLockScreenOverlayEnabled",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "disable_advertising_id",
                Category = "系统隐私与去广告",
                Title = "禁用系统个性化广告追踪 ID",
                Description = "阻止应用使用广告 ID 进行跨应用兴趣追踪（对应 AdvertisingInfo\\Enabled = 0）",
                SubKey = advInfo,
                ValueName = "Enabled",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            },
            new()
            {
                Id = "disable_scoobe_nag",
                Category = "系统隐私与去广告",
                Title = "关闭系统更新后「完成设备设置」全屏弹窗提醒",
                Description = "防止更新重启后弹出全屏 Microsoft 365 / OneDrive 引导界面（对应 ScoobeSystemSettingEnabled = 0）",
                SubKey = engage,
                ValueName = "ScoobeSystemSettingEnabled",
                OnValue = 0,
                OffValue = 1,
                DefaultIsOn = false
            }
        };

        foreach (var item in specs)
        {
            var raw = Reg.GetInt(RegPath.Of(item.Hive, item.SubKey), item.ValueName);
            item.IsOn = raw.HasValue ? raw.Value == item.OnValue : item.DefaultIsOn;
        }

        return specs;
    }

    public void SetTweak(TweakSwitchItem item, bool isOn)
    {
        var targetVal = isOn ? item.OnValue : item.OffValue;
        _journal.Record(
            $"{(isOn ? "开启" : "关闭")}系统调优：{item.Title}",
            new[] { (item.Hive, item.SubKey) },
            () =>
            {
                Reg.SetDword(RegPath.Of(item.Hive, item.SubKey), item.ValueName, targetVal);
            });
        Native.NotifyAssociationsChanged();
    }

    public async Task<List<PortProcessItem>> LoadPortTableAsync()
    {
        var (_, stdout, _) = await AppManagerService.RunProcessAsync("netstat.exe", "-ano", TimeSpan.FromSeconds(15));
        return await Task.Run(() => ParseNetstatOutput(stdout));
    }

    public static List<PortProcessItem> ParseNetstatOutput(string rawOutput)
    {
        var result = new List<PortProcessItem>();
        if (string.IsNullOrWhiteSpace(rawOutput)) return result;

        var procMap = new Dictionary<int, (string Name, string Exe)>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var exe = "";
                    try { exe = p.MainModule?.FileName ?? ""; } catch (Exception) { }
                    procMap[p.Id] = (p.ProcessName, exe);
                }
                catch (Exception) { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception) { }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in rawOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("UDP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;

            var proto = tokens[0].ToUpperInvariant();
            var local = tokens[1];
            var remote = tokens[2];
            string state;
            string pidToken;

            if (proto == "TCP")
            {
                if (tokens.Length < 5) continue;
                state = tokens[3];
                pidToken = tokens[4];
            }
            else
            {
                state = "UDP";
                pidToken = tokens[3];
            }

            if (!int.TryParse(pidToken, out var pid)) continue;

            var lastColon = local.LastIndexOf(':');
            if (lastColon <= 0) continue;
            var addrPart = local[..lastColon];
            var portPart = local[(lastColon + 1)..];
            if (!int.TryParse(portPart, out var port)) continue;

            var dedupKey = $"{proto}:{addrPart}:{port}:{state}:{pid}";
            if (!seen.Add(dedupKey)) continue;

            var procName = pid == 0 ? "Idle" : pid == 4 ? "System" : $"PID:{pid}";
            var exePath = "";
            if (procMap.TryGetValue(pid, out var pInfo))
            {
                procName = pInfo.Name;
                exePath = pInfo.Exe;
            }

            result.Add(new PortProcessItem
            {
                Protocol = proto,
                LocalAddress = addrPart,
                LocalPort = port,
                RemoteAddress = remote,
                State = state,
                Pid = pid,
                ProcessName = procName,
                ExecutablePath = exePath
            });
        }

        return result
            .OrderBy(p => p.State == "LISTENING" ? 0 : 1)
            .ThenBy(p => p.LocalPort)
            .ToList();
    }

    public async Task<(bool Success, string Message)> RunNetworkRepairCommandAsync(string title, string fileName, string args)
    {
        var (code, output, err) = await AppManagerService.RunProcessAsync(fileName, args, TimeSpan.FromSeconds(20));
        if (code == 0)
            return (true, $"{title} 执行成功");

        var msg = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
        return (false, $"{title} 未完成：{msg}");
    }

    public List<AdminPanelShortcut> GetAdminShortcuts() =>
        new()
        {
            new() { Category = "服务与后台", Title = "系统服务管理", CommandLabel = "services.msc", Description = "查看与管理所有 Windows 后台服务与依存关系", FileName = "services.msc" },
            new() { Category = "服务与后台", Title = "任务计划程序", CommandLabel = "taskschd.msc", Description = "管理 Windows 定时触发任务与后台计划", FileName = "taskschd.msc" },
            new() { Category = "策略与注册表", Title = "本地组策略编辑器", CommandLabel = "gpedit.msc", Description = "配置系统计算机策略与用户组策略模板", FileName = "gpedit.msc" },
            new() { Category = "策略与注册表", Title = "注册表编辑器", CommandLabel = "regedit.exe", Description = "直接打开系统注册表树编辑器", FileName = "regedit.exe" },
            new() { Category = "硬件与存储", Title = "磁盘管理", CommandLabel = "diskmgmt.msc", Description = "分区格式化、扩展卷、压缩卷与修改盘符字母", FileName = "diskmgmt.msc" },
            new() { Category = "硬件与存储", Title = "设备管理器", CommandLabel = "devmgmt.msc", Description = "查看硬件设备状态、驱动签名与冲突属性", FileName = "devmgmt.msc" },
            new() { Category = "系统与环境", Title = "高级系统属性 / 环境变量", CommandLabel = "sysdm.cpl", Description = "配置系统环境变量 (PATH)、虚拟内存页面文件与远程桌面", FileName = "sysdm.cpl" },
            new() { Category = "系统与环境", Title = "Windows 可选功能开关", CommandLabel = "optionalfeatures.exe", Description = "启用或关闭 Hyper-V、WSL、Telnet、沙盒等系统内置组件", FileName = "optionalfeatures.exe" },
            new() { Category = "网络与电源", Title = "网络连接适配器", CommandLabel = "ncpa.cpl", Description = "经典网卡适配器面板：直接改 IPv4、DNS、VLAN 与桥接", FileName = "ncpa.cpl" },
            new() { Category = "网络与电源", Title = "经典电源计划选项", CommandLabel = "powercfg.cpl", Description = "切换平衡/高性能/卓越性能电源计划与盒盖睡眠行为", FileName = "powercfg.cpl" },
            new() { Category = "软件与安全", Title = "经典程序和功能", CommandLabel = "appwiz.cpl", Description = "传统控制面板卸载程序列表与已安装更新查看", FileName = "appwiz.cpl" },
            new() { Category = "软件与安全", Title = "证书管理器", CommandLabel = "certmgr.msc", Description = "管理当前用户与受信任根证书颁发机构证书", FileName = "certmgr.msc" },
            new() { Category = "性能与诊断", Title = "资源监视器", CommandLabel = "resmon.exe", Description = "实时监控 CPU、内存、磁盘 I/O、网络流量及文件句柄占用", FileName = "resmon.exe" },
            new() { Category = "性能与诊断", Title = "性能监视器", CommandLabel = "perfmon.exe", Description = "查看系统性能计数器与诊断数据收集器集", FileName = "perfmon.exe" },
            new() { Category = "性能与诊断", Title = "系统可靠性监视器", CommandLabel = "perfmon /rel", Description = "按时间轴查看近期软件崩溃、蓝屏与驱动异常历史记录", FileName = "perfmon.exe", Arguments = "/rel" },
            new() { Category = "性能与诊断", Title = "DirectX 诊断工具", CommandLabel = "dxdiag.exe", Description = "查看显卡 DirectX 特性级别、显示输出与声卡驱动诊断报告", FileName = "dxdiag.exe" }
        };
}
