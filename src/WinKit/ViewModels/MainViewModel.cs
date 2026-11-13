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
    public System.Windows.Media.Brush AccentBrush { get; init; } = System.Windows.Media.Brushes.Gray;
    public Func<object> Factory { get; init; } = () => new object();
    public object? Instance { get; set; }
    public bool WarmedUp { get; set; }

    public object ViewModel => Instance ??= Factory();
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ChangeJournal _journal = new();
    private NavItem? _selectedNav;
    private object? _currentViewModel;
    private string _statusText = "就绪";
    private int _busyCount;

    public MainViewModel()
    {
        NavItems = new ObservableCollection<NavItem>
        {
            new()
            {
                Key = "menu",
                Title = "右键菜单",
                Subtitle = "管理 Win11 一级菜单与展开后的完整菜单",
                AccentBrush = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Accent"],
                Factory = () => new ContextMenuViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "thispc",
                Title = "此电脑",
                Subtitle = "管理设备、软件、驱动器在此电脑中的显示",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x6E, 0x56)),
                Factory = () => new ThisPcViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "nav",
                Title = "导航栏",
                Subtitle = "管理左侧固定的软件节点与快速访问项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBA, 0x75, 0x17)),
                Factory = () => new NavPaneViewModel(_journal, SetStatus)
            },
            new()
            {
                Key = "apps",
                Title = "应用管理",
                Subtitle = "卸载桌面与商店应用、winget 检测更新、常用运行库体检与常用系统工具库",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x64, 0xAB)),
                Factory = () => new AppManagerViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "drivers",
                Title = "驱动管理",
                Subtitle = "管理本机第三方驱动包、自动备份与卸载、检测安装官方与本地驱动更新",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x72, 0x85)),
                Factory = () => new DriverManagerViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "startup",
                Title = "启动与服务",
                Subtitle = "管理开机启动项（原生无损启用/禁用）、第三方后台服务与系统计划任务",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x48, 0x0F)),
                Factory = () => new StartupServiceViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "clean",
                Title = "系统清理",
                Subtitle = "安全清理临时文件、系统更新缓存、崩溃转储、缩略图缓存及扫描大文件",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x8A, 0x3E)),
                Factory = () => new SystemCleanViewModel(SetStatus, SetBusy)
            },
            new()
            {
                Key = "tweaks",
                Title = "系统调优与百宝箱",
                Subtitle = "Win11 体验与隐私开关、TCP/UDP 端口占用查询与网络修复、16 项深层管理面板直达",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAE, 0x3E, 0xC9)),
                Factory = () => new SystemTweakViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "registry",
                Title = "注册表管理",
                Subtitle = "按分类体检，只清理可证明无效的项",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x53, 0x4A, 0xB7)),
                Factory = () => new RegistryAuditViewModel(_journal, SetStatus, SetBusy)
            },
            new()
            {
                Key = "backup",
                Title = "备份与还原",
                Subtitle = "查看自动快照，随时回滚",
                AccentBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6B, 0x6B)),
                Factory = () => new BackupViewModel(_journal, SetStatus)
            }
        };

        _journal.Changed += SetStatus;
        UndoCommand = new RelayCommand(Undo);
        RestartExplorerCommand = new RelayCommand(RestartExplorer);
        ElevateCommand = new RelayCommand(Elevate);
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value) || value is null) return;
            CurrentViewModel = value.ViewModel;
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentSubtitle));

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
        if (SelectedNav is null) SelectedNav = NavItems[0];
        else if (CurrentViewModel is IModule module) module.Refresh();
    }

    private void SetStatus(string message) => StatusText = message;

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
