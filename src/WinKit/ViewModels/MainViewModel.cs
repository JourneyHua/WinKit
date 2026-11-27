using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public interface IModule
{
    void Refresh();
}

public sealed class NavItem : ObservableObject
{
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; internal set => Set(ref _isSelected, value); }

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

    /// <summary>
    /// 这个模块是否包含需要管理员权限的操作。
    /// 浏览页面不触发提权；由页面提示和操作时的权限检查说明限制。
    /// 用户级设置仍可在普通权限下修改。
    /// </summary>
    public bool RequiresElevation => ElevationKeys.Contains(Key);

    private static readonly HashSet<string> ElevationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "menu", "thispc", "nav", "apps", "drivers", "startup", "clean", "tweaks",
        "wupdate", "network", "assoc", "restore", "env", "registry", "backup"
    };
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
    private readonly ChangeJournal _journal;
    private readonly UiSettings _settings;
    private readonly bool _saveSettings;
    private bool _isClosePending;
    private string _navSearchText = "";
    private IReadOnlyList<NavItem> _navSearchResults = [];
    private NavItem? _selectedNav;
    private object? _currentViewModel;
    private string _statusText = "就绪";
    private readonly Dictionary<string, string> _moduleStatuses = new(StringComparer.Ordinal);
    private int _busyCount;
    private string _feedbackMessage = "";
    private bool _feedbackIsError;

    public MainViewModel() : this(UiSettingsService.Load())
    {
    }

    public MainViewModel(UiSettings settings) : this(settings, new ChangeJournal())
    {
    }

    internal MainViewModel(UiSettings settings, ChangeJournal journal, bool saveSettings = true)
    {
        _settings = settings;
        _journal = journal;
        _saveSettings = saveSettings;
        NavItems = new ObservableCollection<NavItem>
        {
            new()
            {
                Key = "overview",
                Title = "系统概览",
                Group = "体检与安全",
                Glyph = "\uE9D9",
                Subtitle = "一屏看清系统版本、硬件、磁盘占用、待重启与安全状态（只读）",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x6F, 0xEB)),
                Factory = () => new OverviewViewModel(SetStatus)
            },
            new()
            {
                Key = "menu",
                Title = "右键菜单",
                Group = "资源管理器",
                Glyph = "\uE700",
                Subtitle = "管理 Win11 一级菜单与展开后的完整菜单",
                AccentBrush = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Accent"],
                Factory = () => new ContextMenuViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "thispc",
                Title = "此电脑",
                Group = "资源管理器",
                Glyph = "\uE8B7",
                Subtitle = "管理设备、软件、驱动器在此电脑中的显示",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x6E, 0x56)),
                Factory = () => new ThisPcViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "nav",
                Title = "导航栏",
                Group = "资源管理器",
                Glyph = "\uE8A9",
                Subtitle = "管理左侧固定的软件节点与快速访问项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBA, 0x75, 0x17)),
                Factory = () => new NavPaneViewModel(_journal, SetStatusFor("nav"), SetBusy)
            },
            new()
            {
                Key = "apps",
                Title = "应用管理",
                Group = "应用与驱动",
                Glyph = "\uE71D",
                Subtitle = "卸载桌面与商店应用、winget 检测更新、常用运行库体检与常用系统工具库",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x64, 0xAB)),
                Factory = () => new AppManagerViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "drivers",
                Title = "驱动管理",
                Group = "应用与驱动",
                Glyph = "\uE90F",
                Subtitle = "管理本机第三方驱动包、自动备份与卸载、检测安装官方与本地驱动更新",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x72, 0x85)),
                Factory = () => new DriverManagerViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "startup",
                Title = "启动与服务",
                Group = "系统设置",
                Glyph = "\uE7B8",
                Subtitle = "管理开机启动项（原生无损启用/禁用）、第三方后台服务与系统计划任务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x48, 0x0F)),
                Factory = () => new StartupServiceViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "clean",
                Title = "系统清理",
                Group = "系统设置",
                Glyph = "\uE74D",
                Subtitle = "安全清理临时文件、系统更新缓存、崩溃转储、缩略图缓存及扫描大文件",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x8A, 0x3E)),
                Factory = () => new SystemCleanViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "tweaks",
                Title = "系统调优与百宝箱",
                Group = "系统设置",
                Glyph = "\uE713",
                Subtitle = "Win11 体验与隐私开关、TCP/UDP 端口占用查询与网络修复、16 项深层管理面板直达",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0x3E, 0xC9)),
                Factory = () => new SystemTweakViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "filelock",
                Title = "文件占用解锁",
                Group = "文件与磁盘",
                Glyph = "\uE72E",
                Subtitle = "查出是谁锁着这个文件，可直接结束占用进程（Restart Manager）",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9D, 0x5B, 0x0E)),
                Factory = () => new FileLockViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "events",
                Title = "事件日志诊断",
                Group = "体检与安全",
                Glyph = "\uE7BA",
                Subtitle = "近 7/30 天错误与严重事件按来源归并，蓝屏、崩溃、更新失败一眼定位",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x2A, 0x37)),
                Factory = () => new EventLogViewModel(SetStatusFor("events"))
            },
            new()
            {
                Key = "wupdate",
                Title = "Windows 更新",
                Group = "体检与安全",
                Glyph = "\uE777",
                Subtitle = "查看已装补丁、暂停/恢复更新、清理下载缓存、重启更新服务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x6B, 0x5C)),
                Factory = () => new WindowsUpdateViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "network",
                Title = "网络诊断",
                Group = "网络与诊断",
                Glyph = "\uE968",
                Subtitle = "网卡/DNS/网关、延迟与丢包、DNS 解析耗时、Wi-Fi 信号与系统代理",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x5F, 0x9E)),
                Factory = () => new NetworkViewModel(SetStatusFor("network"))
            },
            new()
            {
                Key = "diff",
                Title = "文件与文件夹对比",
                Group = "文件与磁盘",
                Glyph = "\uE8C8",
                Subtitle = "按大小/时间/哈希对比两个目录，列出仅左侧、仅右侧、内容不同与更新方向",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x5C, 0x1E)),
                Factory = () => new DiffViewModel(SetStatusFor("diff"))
            },
            new()
            {
                Key = "duplicates",
                Title = "重复文件检测",
                Group = "文件与磁盘",
                Glyph = "\uE8EF",
                Subtitle = "大小 → 头部 64KB → 全文件 SHA-256 三级校验，清理一律进回收站",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x3F, 0x8A)),
                Factory = () => new DuplicateViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "assoc",
                Title = "默认打开方式",
                Group = "应用与驱动",
                Glyph = "\uE8A5",
                Subtitle = "查看每个扩展名关联到哪个程序，一键调起系统设置或「打开方式」，清理失效关联",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x6B, 0x52)),
                Factory = () => new FileAssocViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "report",
                Title = "体检与维护",
                Group = "体检与安全",
                Glyph = "\uE9D5",
                Subtitle = "一键汇总各模块检查结果，导出 Markdown/JSON；可注册每周自动维护任务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x7C, 0x86)),
                Factory = () => new ReportViewModel(SetStatusFor("report"), SetBusy)
            },
            new()
            {
                Key = "diskhealth",
                Title = "磁盘健康",
                Group = "体检与安全",
                Glyph = "\uEDA2",
                Subtitle = "SMART 预测失败、SSD 磨损度、温度、通电时长、读写错误与各卷占用",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x3F, 0x8F)),
                Factory = () => new DiskHealthViewModel(SetStatusFor("diskhealth"))
            },
            new()
            {
                Key = "restore",
                Title = "系统还原点",
                Group = "体检与安全",
                Glyph = "\uE7A7",
                Subtitle = "创建、删除还原点，或把系统回退到某个还原点（不动个人文件）",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x57, 0x9A)),
                Factory = () => new SystemRestoreViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "processes",
                Title = "进程管理",
                Group = "体检与安全",
                Glyph = "\uE9D9",
                Subtitle = "按内存排序看全部进程，可按名字/路径/PID 筛选，结束进程或整棵进程树",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x6B, 0x5C)),
                Factory = () => new ProcessManagerViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "smart",
                Title = "SMART 属性",
                Group = "文件与磁盘",
                Glyph = "\uE9D2",
                Subtitle = "逐条看硬盘的 SMART 原始属性（重映射扇区、待映射、温度、通电时长），可导出 CSV",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7A, 0x3E, 0x1D)),
                Factory = () => new SmartAttributeViewModel(SetStatusFor("smart"))
            },
            new()
            {
                Key = "env",
                Title = "环境变量",
                Group = "系统设置",
                Glyph = "\uE756",
                Subtitle = "查看/编辑用户与系统环境变量，可视化调整 PATH 顺序并标记失效目录",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x6D, 0x1F)),
                Factory = CreateEnvironmentViewModel
            },
            new()
            {
                Key = "registry",
                Title = "注册表管理",
                Group = "系统设置",
                Glyph = "\uE74C",
                Subtitle = "按分类体检，只清理可证明无效的项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x4A, 0xB7)),
                Factory = () => new RegistryAuditViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "backup",
                Title = "备份与还原",
                Group = "系统设置",
                Glyph = "\uE81C",
                Subtitle = "查看自动快照，随时回滚",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x6B)),
                Factory = () => new BackupViewModel(_journal, SetStatus, SetBusy, () => IsGlobalBusy)
            }
        };

        _journal.Changed += OnJournalChanged;
        _journal.OperationStateChanged += () => OnUi(UpdateOperationState);
        _journal.ResultReported += result => OnUi(() =>
        {
            FeedbackIsError = !result.Succeeded;
            FeedbackMessage = result.Message;
            if (result.HasChanges)
            {
                // 还原也可能只完成一部分；所有已打开模块都需重新读取真实状态。
                // 只刷新当前页，其余保留实例与筛选条件，等下次进入时再扫描。
                // 备份页自身监听 Changed 并合并列表刷新，不再安排第二次扫描。
                foreach (var item in NavItems.Where(item => item.Instance is IModule and not BackupViewModel))
                    item.WarmedUp = false;
                RefreshCurrentModuleIfNeeded();
            }
            UpdateUndoState();
        });
        UndoCommand = new RelayCommand(Undo, () => _journal.CanUndo && IsNavigationEnabled);
        DismissFeedbackCommand = new RelayCommand(() => FeedbackMessage = "");
        RestartExplorerCommand = new RelayCommand(() => _ = RestartExplorerAsync(), () => IsNavigationEnabled && ShowRestartExplorer);
        ElevateCommand = new RelayCommand(Elevate, () => IsNavigationEnabled);
        CancelCloseCommand = new RelayCommand(() => IsClosePending = false);
        ToggleGroupCommand = new RelayCommand(p => ToggleGroup(p as NavGroup));
        ClearNavSearchCommand = new RelayCommand(() => NavSearchText = "", () => HasNavSearchText);
        ShowSettingsCommand = new RelayCommand(() => ShowSettingsRequested?.Invoke());

        Updater = new AppUpdateViewModel(
            new AppUpdateService(_saveSettings ? null : System.IO.Path.Combine(_journal.StorageDirectory, "updates")),
            () => IsGlobalBusy,
            () => HasPendingPathDrafts,
            PrepareUpdateRestartAsync, () => CloseRequested?.Invoke(), reviewDrafts: ReviewPendingPathDrafts);
        Updater.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppUpdateViewModel.IsPreparing)) UpdateOperationState();
        };
        BuildGroups();
    }

    public AppUpdateViewModel Updater { get; }
    private AppUpdateRestartSession? _updateRestartSession;
    public bool IsMainContentEnabled => !IsPreparingUpdate;
    private bool IsPreparingUpdate => Updater is { IsPreparing: true };

    private EnvironmentViewModel CreateEnvironmentViewModel()
    {
        var environment = new EnvironmentViewModel(_journal, SetStatus, SetBusy);
        environment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentViewModel.HasPendingPathDrafts)) Updater.NotifyHostStateChanged();
        };
        return environment;
    }

    private NavItem? EnvironmentNav => NavItems.FirstOrDefault(item => item.Key == "env");
    public bool HasPendingPathDrafts => EnvironmentNav?.Instance is EnvironmentViewModel { HasPendingPathDrafts: true };

    public void ReviewPendingPathDrafts()
    {
        if (IsNavigationEnabled && EnvironmentNav is { Instance: EnvironmentViewModel environment } nav)
        {
            NavSearchText = "";
            SelectedNav = nav;
            environment.ReviewPendingPathDrafts();
        }
    }

    private async Task<bool> PrepareUpdateRestartAsync(AppUpdateDownload download, CancellationToken cancellationToken)
    {
        if (IsGlobalBusy || HasPendingPathDrafts) return false;
        var session = await new AppUpdateInstaller().PrepareRestartAsync(download, cancellationToken);
        if (!session.Ready || IsGlobalBusy)
        {
            await session.CancelAsync();
            await session.DisposeAsync();
            return false;
        }
        try
        {
            await session.AllowExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _updateRestartSession = session;
            return true;
        }
        catch
        {
            try { await session.CancelAsync(); } catch { }
            try { await session.DisposeAsync(); } catch { }
            throw;
        }
    }

    public void OnWindowClosed()
    {
        Updater.Dispose();
        if (_updateRestartSession is not null) _ = DisposeUpdateSessionAsync(_updateRestartSession);
    }

    private static async Task DisposeUpdateSessionAsync(AppUpdateRestartSession session)
    {
        try { await session.DisposeAsync(); } catch { /* 关闭进程时只释放握手资源。 */ }
    }

    /// <summary>由 MainWindow 订阅，弹出主题/密度浮层。</summary>
    public event Action? ShowSettingsRequested;

    public ObservableCollection<NavItem> NavItems { get; }

    public ObservableCollection<NavGroup> NavGroups { get; } = new();

    public RelayCommand ToggleGroupCommand { get; }

    public RelayCommand ClearNavSearchCommand { get; }

    public string NavSearchText
    {
        get => _navSearchText;
        set
        {
            if (!Set(ref _navSearchText, value)) return;
            var terms = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            _navSearchResults = terms.Length == 0 ? [] : NavItems.Where(item => terms.All(term =>
                item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Subtitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Group.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            OnPropertyChanged(nameof(NavSearchResults));
            OnPropertyChanged(nameof(HasNavSearchText));
            OnPropertyChanged(nameof(IsNavSearchActive));
            OnPropertyChanged(nameof(ShowNoNavResults));
            ClearNavSearchCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<NavItem> NavSearchResults => _navSearchResults;
    public bool HasNavSearchText => NavSearchText.Length > 0;
    public bool IsNavSearchActive => !string.IsNullOrWhiteSpace(NavSearchText);
    public bool ShowNoNavResults => IsNavSearchActive && NavSearchResults.Count == 0;

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

                SaveSettings();
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
            if (!ReferenceEquals(value, _selectedNav) && !IsNavigationEnabled)
            {
                OnPropertyChanged(nameof(SelectedNav));
                return;
            }
            var previous = _selectedNav;
            if (!Set(ref _selectedNav, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is null) return;
            value.IsSelected = true;
            StatusText = _moduleStatuses.GetValueOrDefault(value.Key, "就绪");
            CurrentViewModel = value.ViewModel;
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentSubtitle));
            OnPropertyChanged(nameof(ElevationBannerText));
            OnPropertyChanged(nameof(ShowElevationBanner));
            OnPropertyChanged(nameof(ShowRestartExplorer));

            // 记住上次停留的模块
            _settings.LastModuleKey = value.Key;
            SaveSettings();

            // 切到别的组里的模块时，展开那个组
            var owner = NavGroups.FirstOrDefault(g => g.Items.Contains(value));
            if (owner is not null && !owner.IsExpanded) owner.IsExpanded = true;

            RefreshCurrentModuleIfNeeded();
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

    public string SessionHint => _journal.CanUndo ? $"本次运行可撤销 {_journal.SessionCount} 次改动" : "";

    public string UndoHint => IsGlobalBusy ? "请等待当前操作完成后撤销"
        : _journal.CanUndo ? $"撤销：{_journal.LastTitle}" : "本次运行还没有可撤销的改动";

    public bool HasUndo => _journal.CanUndo;

    public string FeedbackMessage
    {
        get => _feedbackMessage;
        private set
        {
            if (Set(ref _feedbackMessage, value)) OnPropertyChanged(nameof(HasFeedback));
        }
    }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(FeedbackMessage);

    public bool FeedbackIsError
    {
        get => _feedbackIsError;
        private set => Set(ref _feedbackIsError, value);
    }

    public bool ShowRestartExplorer => SelectedNav?.Key is "menu" or "thispc" or "nav" or "taskbar" or "tweaks";

    /// <summary>
    /// 当前进程是否已提权。主清单是 asInvoker，所以「没提权」是常态而不是异常。
    /// </summary>
    public bool IsElevated => Native.IsElevated();

    public string ElevationText => IsElevated ? "已以管理员身份运行" : "普通权限运行";

    public bool ShowElevateButton => !IsElevated;

    /// <summary>只在包含受限操作的页面说明权限，不打断浏览。</summary>
    public bool ShowElevationBanner => !IsElevated && SelectedNav is { RequiresElevation: true };

    public string ElevationBannerText => ShowElevationBanner
        ? $"「{SelectedNav!.Title}」的部分系统操作需要管理员权限。可先浏览；仅在需要时以管理员身份重启。"
        : "";

    public bool IsGlobalBusy => _busyCount > 0 || _journal.IsOperationActive;

    public bool IsNavigationEnabled => !IsGlobalBusy && !IsPreparingUpdate;

    public string NavigationHint => !IsNavigationEnabled ? "正在完成当前操作，完成后可切换模块" : "打开此模块";

    public string VersionText
    {
        get
        {
            var v = typeof(MainViewModel).Assembly.GetName().Version;
            var verText = v is null ? "1.2.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            return $"v{verText}";
        }
    }

    public RelayCommand UndoCommand { get; }

    public RelayCommand DismissFeedbackCommand { get; }

    public RelayCommand RestartExplorerCommand { get; }

    public RelayCommand ElevateCommand { get; }

    /// <summary>图标字体可用性：缺失时隐藏图标列，避免显示成方框。</summary>
    public bool ShowNavIcons => IconFont.IsAvailable;

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
        if (_saveSettings) Updater.StartAutomaticChecks();
    }

    /// <summary>供窗口关闭时写回窗口位置与大小。</summary>
    public void SaveWindowState(double width, double height, double left, double top, bool maximized)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        _settings.WindowMaximized = maximized;
        SaveSettings();
    }

    public UiSettings Settings => _settings;

    private void SaveSettings()
    {
        if (_saveSettings) UiSettingsService.Save(_settings);
    }

    public bool IsClosePending
    {
        get => _isClosePending;
        private set => Set(ref _isClosePending, value);
    }

    public RelayCommand CancelCloseCommand { get; }

    public event Action? CloseRequested;

    public void RequestCloseWhenIdle()
    {
        if (IsGlobalBusy) IsClosePending = true;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void SetStatus(string message) => OnUi(() => StatusText = message);

    private Action<string> SetStatusFor(string moduleKey) => message => OnUi(() =>
    {
        _moduleStatuses[moduleKey] = message;
        if (SelectedNav?.Key == moduleKey) StatusText = message;
    });

    private void UpdateUndoState()
    {
        OnPropertyChanged(nameof(HasUndo));
        OnPropertyChanged(nameof(SessionHint));
        OnPropertyChanged(nameof(UndoHint));
        UndoCommand.RaiseCanExecuteChanged();
    }

    private void OnJournalChanged(string message) => OnUi(() =>
    {
        StatusText = message;
        FeedbackIsError = false;
        FeedbackMessage = message;
        UpdateUndoState();
    });

    private void SetBusy(bool busy) => OnUi(() =>
    {
        _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
        UpdateOperationState();
    });

    private void UpdateOperationState()
    {
        OnPropertyChanged(nameof(IsGlobalBusy));
        OnPropertyChanged(nameof(IsNavigationEnabled));
        OnPropertyChanged(nameof(NavigationHint));
        OnPropertyChanged(nameof(IsMainContentEnabled));
        Updater.NotifyHostStateChanged();
        UpdateUndoState();
        RestartExplorerCommand.RaiseCanExecuteChanged();
        ElevateCommand.RaiseCanExecuteChanged();
        if (!IsGlobalBusy && IsClosePending)
        {
            IsClosePending = false;
            CloseRequested?.Invoke();
            return;
        }
        foreach (var backup in NavItems.Select(item => item.Instance).OfType<BackupViewModel>())
            backup.NotifyGlobalStateChanged();
        RefreshCurrentModuleIfNeeded();
    }

    private void RefreshCurrentModuleIfNeeded()
    {
        if (IsGlobalBusy || SelectedNav is not { WarmedUp: false, Instance: IModule module } item) return;
        item.WarmedUp = true;
        module.Refresh();
    }

    private void Elevate()
    {
        if (HasPendingPathDrafts)
        {
            ReviewPendingPathDrafts();
            FeedbackIsError = false;
            FeedbackMessage = "PATH 有未应用的更改。请先处理更改，再以管理员身份重新启动。";
            return;
        }

        if (Native.RestartAsAdmin()) Application.Current.Shutdown();
        else
        {
            FeedbackIsError = true;
            FeedbackMessage = "未以管理员身份重新启动。当前窗口保持打开，可稍后重试。";
        }
    }

    private async void Undo()
    {
        if (!UndoCommand.CanExecute(null)) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(_journal.UndoLastResult);
            StatusText = result.Message;
            FeedbackIsError = !result.Succeeded;
            FeedbackMessage = result.Message;
        }
        catch (Exception ex)
        {
            FeedbackIsError = true;
            FeedbackMessage = $"撤销未完成：{ex.Message}。记录已保留，可稍后重试或到「备份与还原」查看。";
            StatusText = FeedbackMessage;
        }
        finally
        {
            SetBusy(false);
            UpdateUndoState();
        }
    }

    private async Task RestartExplorerAsync()
    {
        if (!RestartExplorerCommand.CanExecute(null)) return;
        var result = MessageBox.Show(Application.Current.MainWindow,
            "任务栏和桌面会暂时消失，资源管理器里的文件操作可能中断。请先等这些操作结束，再重启资源管理器。",
            "重启资源管理器？", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (result != MessageBoxResult.OK || !RestartExplorerCommand.CanExecute(null)) return;
        SetBusy(true);
        FeedbackIsError = false;
        FeedbackMessage = "正在重启资源管理器…";
        try
        {
            await Native.RestartExplorerAsync();
            FeedbackMessage = "资源管理器已重新启动。";
        }
        catch (Exception ex)
        {
            FeedbackIsError = true;
            FeedbackMessage = $"无法确认资源管理器已重新启动：{ex.Message}";
        }
        finally
        {
            SetStatus(FeedbackMessage);
            SetBusy(false);
        }
    }
}
