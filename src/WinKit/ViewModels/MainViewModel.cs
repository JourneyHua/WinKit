using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public interface IModule
{
    void Refresh();
}

public sealed class NavItem
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Group { get; init; } = "";
    /// <summary>Segoe Fluent Icons 字形；字体缺失时界面回落到色点。</summary>
    public string Glyph { get; init; } = "";
    public System.Windows.Media.Brush AccentBrush { get; init; } = System.Windows.Media.Brushes.Gray;
    public Func<object> Factory { get; init; } = () => new object();
    public object? Instance { get; set; }
    public bool WarmedUp { get; set; }

    public object ViewModel => Instance ??= Factory();
}

/// <summary>导航分组：默认只展开当前模块所在的组，避免 21 项把侧栏撑出滚动。</summary>
public sealed class NavGroup : ObservableObject
{
    private bool _isExpanded;

    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public ObservableCollection<NavItem> Items { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value)) Toggled?.Invoke(this);
        }
    }

    public Action<NavGroup>? Toggled { get; set; }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ChangeJournal _journal = new();
    private readonly UiSettings _settings;
    private NavItem? _selectedNav;
    private object? _currentViewModel;
    private string _statusText = "就绪";
    private int _busyCount;

    public MainViewModel() : this(UiSettingsService.Load())
    {
    }

    public MainViewModel(UiSettings settings)
    {
        _settings = settings;
        NavItems = new ObservableCollection<NavItem>
        {
            new()
            {
                Key = "overview",
                Title = "系统概览",
                Group = "体检与安全",
                Glyph = "&#xE9D9;",
                Subtitle = "一屏看清系统版本、硬件、磁盘占用、待重启与安全状态（只读）",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x6F, 0xEB)),
                Factory = () => new OverviewViewModel(SetStatus)
            },
            new()
            {
                Key = "menu",
                Title = "右键菜单",
                Group = "资源管理器",
                Glyph = "&#xE700;",
                Subtitle = "管理 Win11 一级菜单与展开后的完整菜单",
                AccentBrush = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Accent"],
                Factory = () => new ContextMenuViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "thispc",
                Title = "此电脑",
                Group = "资源管理器",
                Glyph = "&#xE8B7;",
                Subtitle = "管理设备、软件、驱动器在此电脑中的显示",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x6E, 0x56)),
                Factory = () => new ThisPcViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "nav",
                Title = "导航栏",
                Group = "资源管理器",
                Glyph = "&#xE8A9;",
                Subtitle = "管理左侧固定的软件节点与快速访问项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBA, 0x75, 0x17)),
                Factory = () => new NavPaneViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "apps",
                Title = "应用管理",
                Group = "应用与驱动",
                Glyph = "&#xE71D;",
                Subtitle = "卸载桌面与商店应用、winget 检测更新、常用运行库体检与常用系统工具库",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x64, 0xAB)),
                Factory = () => new AppManagerViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "drivers",
                Title = "驱动管理",
                Group = "应用与驱动",
                Glyph = "&#xE90F;",
                Subtitle = "管理本机第三方驱动包、自动备份与卸载、检测安装官方与本地驱动更新",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x72, 0x85)),
                Factory = () => new DriverManagerViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "startup",
                Title = "启动与服务",
                Group = "系统设置",
                Glyph = "&#xE7B8;",
                Subtitle = "管理开机启动项（原生无损启用/禁用）、第三方后台服务与系统计划任务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x48, 0x0F)),
                Factory = () => new StartupServiceViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "clean",
                Title = "系统清理",
                Group = "系统设置",
                Glyph = "&#xE74D;",
                Subtitle = "安全清理临时文件、系统更新缓存、崩溃转储、缩略图缓存及扫描大文件",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x8A, 0x3E)),
                Factory = () => new SystemCleanViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "tweaks",
                Title = "系统调优与百宝箱",
                Group = "系统设置",
                Glyph = "&#xE713;",
                Subtitle = "Win11 体验与隐私开关、TCP/UDP 端口占用查询与网络修复、16 项深层管理面板直达",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0x3E, 0xC9)),
                Factory = () => new SystemTweakViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "filelock",
                Title = "文件占用解锁",
                Group = "文件与磁盘",
                Glyph = "&#xE72E;",
                Subtitle = "查出是谁锁着这个文件，可直接结束占用进程（Restart Manager）",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9D, 0x5B, 0x0E)),
                Factory = () => new FileLockViewModel(SetStatus)
            },
            new()
            {
                Key = "events",
                Title = "事件日志诊断",
                Group = "体检与安全",
                Glyph = "&#xE7BA;",
                Subtitle = "近 7/30 天错误与严重事件按来源归并，蓝屏、崩溃、更新失败一眼定位",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x2A, 0x37)),
                Factory = () => new EventLogViewModel(SetStatus)
            },
            new()
            {
                Key = "wupdate",
                Title = "Windows 更新",
                Group = "体检与安全",
                Glyph = "&#xE777;",
                Subtitle = "查看已装补丁、暂停/恢复更新、清理下载缓存、重启更新服务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x6B, 0x5C)),
                Factory = () => new WindowsUpdateViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "network",
                Title = "网络诊断",
                Group = "网络与诊断",
                Glyph = "&#xE968;",
                Subtitle = "网卡/DNS/网关、延迟与丢包、DNS 解析耗时、Wi-Fi 信号与系统代理",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x5F, 0x9E)),
                Factory = () => new NetworkViewModel(SetStatus)
            },
            new()
            {
                Key = "diff",
                Title = "文件与文件夹对比",
                Group = "文件与磁盘",
                Glyph = "&#xE8C8;",
                Subtitle = "按大小/时间/哈希对比两个目录，列出仅左侧、仅右侧、内容不同与更新方向",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x5C, 0x1E)),
                Factory = () => new DiffViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "duplicates",
                Title = "重复文件检测",
                Group = "文件与磁盘",
                Glyph = "&#xE8EF;",
                Subtitle = "大小 → 头部 64KB → 全文件 SHA-256 三级校验，清理一律进回收站",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x3F, 0x8A)),
                Factory = () => new DuplicateViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "assoc",
                Title = "默认打开方式",
                Group = "应用与驱动",
                Glyph = "&#xE8A5;",
                Subtitle = "查看每个扩展名关联到哪个程序，一键调起系统设置或「打开方式」，清理失效关联",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x6B, 0x52)),
                Factory = () => new FileAssocViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "report",
                Title = "体检与维护",
                Group = "体检与安全",
                Glyph = "&#xE9D9;",
                Subtitle = "一键汇总各模块检查结果，导出 Markdown/JSON；可注册每周自动维护任务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x7C, 0x86)),
                Factory = () => new ReportViewModel(SetStatus, SetMaintenanceStatus, SetBusy)
            },
            new()
            {
                Key = "diskhealth",
                Title = "磁盘健康",
                Group = "体检与安全",
                Glyph = "&#xEDA2;",
                Subtitle = "SMART 预测失败、SSD 磨损度、温度、通电时长、读写错误与各卷占用",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x3F, 0x8F)),
                Factory = () => new DiskHealthViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "env",
                Title = "环境变量",
                Group = "系统设置",
                Glyph = "&#xE756;",
                Subtitle = "查看/编辑用户与系统环境变量，可视化调整 PATH 顺序并标记失效目录",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x6D, 0x1F)),
                Factory = () => new EnvironmentViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "registry",
                Title = "注册表管理",
                Group = "系统设置",
                Glyph = "&#xE71D;",
                Subtitle = "按分类体检，只清理可证明无效的项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x4A, 0xB7)),
                Factory = () => new RegistryAuditViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "backup",
                Title = "备份与还原",
                Group = "系统设置",
                Glyph = "&#xE81C;",
                Subtitle = "查看自动快照，随时回滚",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x6B)),
                Factory = () => new BackupViewModel(_journal, SetStatus)
            }
        };

        _journal.Changed += SetStatus;
        UndoCommand = new RelayCommand(Undo);
        RestartExplorerCommand = new RelayCommand(RestartExplorer);
        ElevateCommand = new RelayCommand(Elevate);
        ToggleGroupCommand = new RelayCommand(p => ToggleGroup(p as NavGroup));
        ShowSettingsCommand = new RelayCommand(() => ShowSettingsRequested?.Invoke());

        BuildGroups();
    }

    /// <summary>由 MainWindow 订阅，弹出主题/密度浮层。</summary>
    public event Action? ShowSettingsRequested;

    public ObservableCollection<NavItem> NavItems { get; }

    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    public RelayCommand ToggleGroupCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    /// <summary>按 Group 字段把 NavItems 归并成 5 个分组，顺序按首次出现。</summary>
    private void BuildGroups()
    {
        var order = new List<string>();

        foreach (var item in NavItems)
        {
            if (!order.Contains(item.Group)) order.Add(item.Group);
        }

        foreach (var name in order)
        {
            var group = new NavGroup
            {
                Name = name,
                Glyph = NavItems.First(i => i.Group == name).Glyph
            };

            foreach (var item in NavItems.Where(i => i.Group == name))
                group.Items.Add(item);

            group.Toggled += g =>
            {
                if (_suppressGroupEvents) return;

                if (g.IsExpanded)
                {
                    if (!_settings.ExpandedGroups.Contains(g.Name)) _settings.ExpandedGroups.Add(g.Name);
                    _settings.CollapsedGroups.Remove(g.Name);
                }
                else
                {
                    if (!_settings.CollapsedGroups.Contains(g.Name)) _settings.CollapsedGroups.Add(g.Name);
                    _settings.ExpandedGroups.Remove(g.Name);
                }

                UiSettingsService.Save(_settings);
            };

            NavGroups.Add(group);
        }
    }

    private bool _suppressGroupEvents;

    /// <summary>恢复分组折叠状态：默认只展开当前模块所在的组。</summary>
    private void RestoreGroupState(NavItem? current)
    {
        _suppressGroupEvents = true;

        try
        {
            foreach (var group in NavGroups)
            {
                var containsCurrent = current is not null && group.Items.Contains(current);

                group.IsExpanded = containsCurrent
                                   || _settings.ExpandedGroups.Contains(group.Name)
                                   || (!_settings.CollapsedGroups.Contains(group.Name) && NavGroups.Count == 1);
            }
        }
        finally
        {
            _suppressGroupEvents = false;
        }
    }

    private void ToggleGroup(NavGroup? group)
    {
        if (group is null) return;
        group.IsExpanded = !group.IsExpanded;
    }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value) || value is null) return;
            CurrentViewModel = value.ViewModel;
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentSubtitle));

            // 记住上次停留的模块
            _settings.LastModuleKey = value.Key;
            UiSettingsService.Save(_settings);

            // 切到别的组里的模块时，展开那个组
            var owner = NavGroups.FirstOrDefault(g => g.Items.Contains(value));
            if (owner is not null && !owner.IsExpanded) owner.IsExpanded = true;

            // 首次进入模块时才加载数据，避免启动时把所有模块都扫一遍
            if (!value.WarmedUp)
            {
                value.WarmedUp = true;
                if (value.ViewModel is IModule module) module.Refresh();
            }
        }
    }

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        private set => Set(ref _currentViewModel, value);
    }

    public string CurrentTitle => SelectedNav?.Title ?? "";

    public string CurrentSubtitle => SelectedNav?.Subtitle ?? "";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string BackupHint => ChangeJournal.BackupDirectory;

    public string ElevationText => Native.IsElevated() ? "已以管理员身份运行" : "未提权（部分功能不可用）";

    public bool ShowElevateButton => !Native.IsElevated();

    public bool IsGlobalBusy => _busyCount > 0;

    public string VersionText
    {
        get
        {
            var v = typeof(MainViewModel).Assembly.GetName().Version;
            var verText = v is null ? "1.2.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            return $"v{verText} · .NET 10";
        }
    }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RestartExplorerCommand { get; }

    public RelayCommand ElevateCommand { get; }

    public void Initialize()
    {
        // 恢复上次停留的模块（找不到就回第一个）
        var target = NavItems.FirstOrDefault(i => i.Key == _settings.LastModuleKey) ?? NavItems[0];
        RestoreGroupState(target);

        if (SelectedNav is null) SelectedNav = target;
        else if (CurrentViewModel is IModule module) module.Refresh();

        // 无人值守的定期维护失败时，用户下次打开就能看到，而不是静默烂在日志里
        var lastOutcome = MaintenanceService.LastOutcomeText();
        if (lastOutcome.Contains("失败", StringComparison.Ordinal))
            StatusText = $"上次定期维护未成功（{lastOutcome}），详见「体检与维护」页";
    }

    /// <summary>供窗口关闭时写回窗口位置与大小。</summary>
    public void SaveWindowState(double width, double height, double left, double top, bool maximized)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowMaximized = maximized;
        UiSettingsService.Save(_settings);
    }

    public UiSettings Settings => _settings;

    private void SetStatus(string message) => StatusText = message;

    /// <summary>体检/维护模块专用：把结论推到状态栏，异常时同时提示。</summary>
    private void SetMaintenanceStatus(string message) => StatusText = message;

    private void SetBusy(bool busy)
    {
        _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
        OnPropertyChanged(nameof(IsGlobalBusy));
    }

    private static void Elevate()
    {
        if (Native.RestartAsAdmin())
            Application.Current.Shutdown();
    }

    private void Undo()
    {
        if (!_journal.CanUndo)
        {
            MessageBox.Show("本次运行还没有可撤销的改动。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var title = _journal.UndoLast();
        StatusText = $"已撤销：{title}";
        if (CurrentViewModel is IModule module) module.Refresh();
    }

    private void RestartExplorer()
    {
        var result = MessageBox.Show(
            "将关闭并重新启动资源管理器。正在进行的文件复制或重命名会被中断，确定继续吗？",
            "重启资源管理器",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        var killed = Native.RestartExplorer();
        SetStatus($"已重启资源管理器（结束 {killed} 个进程）");
    }
}
