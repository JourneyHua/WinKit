using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class DomainNode : ObservableObject
{
    private int _count;

    public MenuDomainKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}

public sealed class ContextMenuViewModel : ObservableObject, IModule
{
    private readonly ContextMenuService _service;
    private readonly NewMenuService _newService;
    private readonly UninstallService _uninstall = new();
    private readonly Action<string> _status;

    private DispatcherTimer? _uninstallTimer;
    private DateTime _uninstallDeadline;
    private NewMenuItem? _watching;
    private NewMenuItem? _lastUninstalled;

    private List<MenuEntry> _all = new();
    private DomainNode? _selectedDomain;
    private DomainNode? _selectedNewDomain;
    private string _searchText = "";
    private bool _onlyThirdParty = true;
    private bool _onlyDisabled;
    private bool _showNewMenu = true;
    private bool _useClassicMenuByDefault;
    private string _newMenuHint = "";
    private bool _isLoading;

    public ContextMenuViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new ContextMenuService(journal);
        _newService = new NewMenuService(journal);
        _status = status;

        Domains = new ObservableCollection<DomainNode>(
            MenuDomain.All.Select(d => new DomainNode
            {
                Kind = d.Kind,
                Title = d.Title,
                Description = d.Description
            }));

        Entries = new ObservableCollection<MenuEntry>();
        NewMenuItems = new ObservableCollection<NewMenuItem>();
        NewDomains = new ObservableCollection<DomainNode>();
        FilteredNewItems = new ObservableCollection<NewMenuItem>();

        RefreshCommand = new RelayCommand(ManualRefresh);
        RecheckResidualCommand = new RelayCommand(RecheckResidual);
        DeleteCommand = new RelayCommand(p => Delete(p as MenuEntry));
        RenameCommand = new RelayCommand(p => Rename(p as MenuEntry));
        CopyPathCommand = new RelayCommand(CopyPath);
        CopyCommandPathCommand = new RelayCommand(CopyCommandPath);
        ShowNewMenuCommand = new RelayCommand(() => ShowNewMenu = true);
        ShowClassicMenuCommand = new RelayCommand(() => ShowNewMenu = false);
        UninstallCommand = new RelayCommand(p => Uninstall(p as NewMenuItem));
        ToggleBlockCommand = new RelayCommand(p => ToggleBlock(p as NewMenuItem));

        _selectedDomain = Domains.FirstOrDefault();
    }

    public ObservableCollection<DomainNode> Domains { get; }

    public ObservableCollection<MenuEntry> Entries { get; }

    public ObservableCollection<NewMenuItem> NewMenuItems { get; }

    public ObservableCollection<DomainNode> NewDomains { get; }

    public ObservableCollection<NewMenuItem> FilteredNewItems { get; }

    public DomainNode? SelectedNewDomain
    {
        get => _selectedNewDomain;
        set
        {
            if (Set(ref _selectedNewDomain, value)) ApplyNewMenuFilter();
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RecheckResidualCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand CopyCommandPathCommand { get; }
    public RelayCommand ShowNewMenuCommand { get; }
    public RelayCommand ShowClassicMenuCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand ToggleBlockCommand { get; }

    public DomainNode? SelectedDomain
    {
        get => _selectedDomain;
        set
        {
            if (Set(ref _selectedDomain, value)) ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public bool OnlyThirdParty
    {
        get => _onlyThirdParty;
        set
        {
            if (Set(ref _onlyThirdParty, value)) ApplyFilter();
        }
    }

    public bool OnlyDisabled
    {
        get => _onlyDisabled;
        set
        {
            if (Set(ref _onlyDisabled, value)) ApplyFilter();
        }
    }

    public bool ShowNewMenu
    {
        get => _showNewMenu;
        set
        {
            if (!Set(ref _showNewMenu, value)) return;
            OnPropertyChanged(nameof(ShowClassicMenu));
        }
    }

    public bool ShowClassicMenu => !_showNewMenu;

    public bool UseClassicMenuByDefault
    {
        get => _useClassicMenuByDefault;
        set
        {
            if (value == _useClassicMenuByDefault) return;
            try
            {
                _newService.SetForcedClassicMenu(value);
                _useClassicMenuByDefault = value;
                OnPropertyChanged();
                UpdateNewMenuHint();
                _status(value
                    ? "已开启：默认直接展开经典右键菜单（重启资源管理器后生效）"
                    : "已恢复：默认使用 Win11 一级右键菜单（重启资源管理器后生效）");
            }
            catch (Exception ex)
            {
                OnPropertyChanged();
                MessageBox.Show($"切换默认菜单模式失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public bool CanRecheckResidual => _lastUninstalled is not null;

    public string RecheckResidualText => _lastUninstalled is null
        ? "复查残留"
        : $"复查残留（{_lastUninstalled.DisplayName}）";

    public string NewMenuHint
    {
        get => _newMenuHint;
        private set => Set(ref _newMenuHint, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string EntryCountText => $"{Entries.Count} 项";

    private void ManualRefresh()
    {
        if (_lastUninstalled is not null)
        {
            var pending = _lastUninstalled;
            StopWatch();
            ReportResidual(pending);
            return;
        }

        Refresh();
    }

    private void RecheckResidual()
    {
        if (_lastUninstalled is null) return;
        var pending = _lastUninstalled;
        StopWatch();
        ReportResidual(pending);
    }

    public void Refresh()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            _useClassicMenuByDefault = _newService.ForcedClassicMenuState() == 1;
            OnPropertyChanged(nameof(UseClassicMenuByDefault));

            _all = new List<MenuEntry>();
            foreach (var domain in MenuDomain.All)
            {
                var items = _service.Load(domain.Kind);
                foreach (var item in items)
                {
                    item.ToggleRequested = OnToggleEntry;
                    _all.Add(item);
                }

                var node = Domains.FirstOrDefault(d => d.Kind == domain.Kind);
                if (node is not null) node.Count = items.Count;
            }

            var newItems = _newService.Load();
            NewMenuItems.Clear();
            foreach (var item in newItems)
            {
                item.ToggleRequested = OnToggleNewMenuItem;
                NewMenuItems.Add(item);
            }

            BuildNewDomains();
            UpdateNewMenuHint();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _status("加载右键菜单失败：" + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateNewMenuHint()
    {
        var (found, total) = _newService.LastManifestStats;
        var classicPrefix = _useClassicMenuByDefault
            ? "【当前已开启“默认直接展开经典菜单”（需重启资源管理器生效），系统右键会跳过一级菜单；你仍可在此管理一级菜单项】 "
            : "";
        var manifestLine = total == 0
            ? "未发现打包应用注册的菜单项。"
            : found == 0
                ? $"清单解析：0/{total} 读取失败（AppRepository 可能被权限拦住），作用域一律标注“无法确认”。"
                : $"清单解析：{found}/{total} 成功，其余标注“无法确认”。";

        NewMenuHint = classicPrefix + manifestLine +
                      " 屏蔽会做两层：① 把 CLSID 写进 Shell Extensions\\Blocked；② 把 PackagedCom 里该命令类的登记改名（加 .WinKitDisabled 后缀）。" +
                      "只做①对打包应用经常无效，这正是 VS Code 之前删不掉的原因；状态列显示“已屏蔽·深度”才代表第二层生效。" +
                      "注意：驱动器右键会继承“文件夹”作用域的项，这类项在「继承」列有标注。" +
                      "内置项（打开、复制、粘贴、重命名、共享、发送到、属性、显示更多选项）由 Windows 硬编码，无法移除。";
    }

    private void BuildNewDomains()
    {
        var previous = _selectedNewDomain?.Kind;
        NewDomains.Clear();

        var order = new[]
        {
            MenuDomainKind.File, MenuDomainKind.Folder, MenuDomainKind.FolderBackground,
            MenuDomainKind.Drive, MenuDomainKind.DesktopBackground, MenuDomainKind.Library
        };

        foreach (var kind in order)
        {
            var count = kind == MenuDomainKind.Drive
                ? NewMenuItems.Count(i => i.ScopeConfirmed && MatchesDriveScope(i))
                : NewMenuItems.Count(i => i.ScopeConfirmed && i.Scopes.Contains(kind));

            if (count == 0) continue;
            NewDomains.Add(new DomainNode
            {
                Kind = kind,
                Title = kind switch
                {
                    MenuDomainKind.File => "文件",
                    MenuDomainKind.Folder => "文件夹",
                    MenuDomainKind.FolderBackground => "文件夹空白处",
                    MenuDomainKind.Drive => "驱动器",
                    MenuDomainKind.DesktopBackground => "桌面空白处",
                    _ => "库"
                },
                Description = kind == MenuDomainKind.Drive
                    ? "含声明 Drive 的项，以及被驱动器继承的文件夹作用域项"
                    : "",
                Count = count
            });
        }

        var unknown = NewMenuItems.Count(i => !i.ScopeConfirmed);
        if (unknown > 0)
        {
            NewDomains.Add(new DomainNode
            {
                Kind = MenuDomainKind.Unknown,
                Title = "作用域未确认",
                Count = unknown
            });
        }

        _selectedNewDomain = NewDomains.FirstOrDefault(d => d.Kind == previous) ?? NewDomains.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedNewDomain));
        ApplyNewMenuFilter();
    }

    /// <summary>驱动器右键会继承文件夹作用域的项（VS Code、PowerRename 就是这样出现在驱动器菜单里的）。</summary>
    private static bool MatchesDriveScope(NewMenuItem item) =>
        item.Scopes.Contains(MenuDomainKind.Drive) || item.Scopes.Contains(MenuDomainKind.Folder);

    private void ApplyNewMenuFilter()
    {
        FilteredNewItems.Clear();
        if (SelectedNewDomain is null) return;

        var kind = SelectedNewDomain.Kind;

        IEnumerable<NewMenuItem> query = kind switch
        {
            MenuDomainKind.Unknown => NewMenuItems.Where(i => !i.ScopeConfirmed),
            MenuDomainKind.Drive => NewMenuItems.Where(i => i.ScopeConfirmed && MatchesDriveScope(i)),
            _ => NewMenuItems.Where(i => i.ScopeConfirmed && i.Scopes.Contains(kind))
        };

        foreach (var item in query.OrderBy(i => i.DisplayName, StringComparer.CurrentCulture))
            FilteredNewItems.Add(item);

        foreach (var item in NewMenuItems)
            item.ShowDriveInherited = kind == MenuDomainKind.Drive
                                      && item.ScopeConfirmed
                                      && !item.Scopes.Contains(MenuDomainKind.Drive)
                                      && item.Scopes.Contains(MenuDomainKind.Folder);

        OnPropertyChanged(nameof(NewMenuCountText));
    }

    public string NewMenuCountText => $"{FilteredNewItems.Count} 个软件";

    private void ToggleBlock(NewMenuItem? item)
    {
        if (item is null) return;

        var target = !item.IsBlocked;
        try
        {
            _newService.SetBlocked(item, target);
            _status(target ? $"已屏蔽：{item.DisplayName}（重启资源管理器后生效）" : $"已恢复：{item.DisplayName}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Uninstall(NewMenuItem? item)
    {
        if (item is null) return;

        var all = _uninstall.LoadAll();
        var candidates = _uninstall.Match(item.Location, item.DisplayName, all);

        if (candidates.Count == 0)
        {
            MessageBox.Show(
                $"没有找到「{item.DisplayName}」的卸载信息。\n\n" +
                $"安装位置：{(string.IsNullOrWhiteSpace(item.Location) ? "未知" : item.Location)}\n\n" +
                "它可能是随其他软件一起安装的组件，请到「设置 → 应用」里查找。",
                "未找到卸载程序", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UninstallEntry? chosen;

        if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            var labels = candidates
                .Select(c => $"{c.DisplayText}    {c.InstallLocation}".Trim())
                .ToList();

            var picked = ChoiceDialog.Pick(Application.Current.MainWindow, "选择要卸载的条目",
                $"「{item.DisplayName}」对应多个卸载记录，请选择正确的一个（选错会卸载别的软件）：",
                labels);

            if (picked is null) return;
            chosen = candidates[labels.IndexOf(picked)];
        }

        var confirm = MessageBox.Show(
            $"将启动「{chosen.Name}」自带的卸载程序。\n\n" +
            $"卸载程序：{chosen.UninstallString}\n\n" +
            "WinKit 不会替你删除任何文件，卸载完成后会自动复查残留。继续吗？",
            "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _uninstall.Launch(chosen);
            _status($"已启动卸载程序：{chosen.Name}");
            StartResidualWatch(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动卸载程序：{ex.Message}\n\n路径：{chosen.UninstallString}",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartResidualWatch(NewMenuItem item)
    {
        StopWatch();
        _watching = item;
        _lastUninstalled = item;
        OnPropertyChanged(nameof(CanRecheckResidual));
        OnPropertyChanged(nameof(RecheckResidualText));
        _uninstallDeadline = DateTime.UtcNow.AddMinutes(10);

        _uninstallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _uninstallTimer.Tick += (_, _) =>
        {
            var still = _uninstall.FindStillRegistered(item.Clsids);

            if (still.Count == 0)
            {
                StopWatch();
                ReportResidual(item);
            }
            else if (DateTime.UtcNow > _uninstallDeadline)
            {
                StopWatch();
                _status("等待超时：可能取消了卸载。可随时点顶部「复查残留」或「重新扫描」手动检查。");
            }
        };
        _uninstallTimer.Start();

        _status($"正在等待「{item.DisplayName}」卸载完成（最多等 10 分钟，也可随时点「复查残留」）…");
    }

    private void StopWatch()
    {
        if (_uninstallTimer is null) return;
        _uninstallTimer.Stop();
        _uninstallTimer = null;
    }

    private void ReportResidual(NewMenuItem item)
    {
        var issues = _uninstall.CheckResidual(item.DisplayName, item.Location, item.Clsids);

        if (issues.Count == 0)
        {
            _lastUninstalled = null;
            OnPropertyChanged(nameof(CanRecheckResidual));
            OnPropertyChanged(nameof(RecheckResidualText));
            _status($"「{item.DisplayName}」已卸载，未发现残留");
            MessageBox.Show($"「{item.DisplayName}」已卸载，未发现明显残留。",
                "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
            return;
        }

        _status($"「{item.DisplayName}」复查完成，发现 {issues.Count} 处残留");
        MessageBox.Show(
            $"「{item.DisplayName}」发现以下残留（若卸载尚未结束，可等卸载完成后再点「复查残留」）：\n\n• " + string.Join("\n• ", issues) +
            "\n\nWinKit 只报告，不代你删文件。注册表项可在「注册表管理」里定位处理。",
            "卸载残留复查", MessageBoxButton.OK, MessageBoxImage.Warning);

        Refresh();
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        if (SelectedDomain is null) return;

        var keyword = _searchText.Trim().ToLowerInvariant();

        var query = _all
            .Where(e => e.Domain == SelectedDomain.Kind)
            .Where(e => !_onlyThirdParty || !e.IsSystem)
            .Where(e => !_onlyDisabled || e.IsDisabled)
            .Where(e => keyword.Length == 0 || e.SearchBlob.Contains(keyword))
            .OrderBy(e => e.DisplayName, StringComparer.CurrentCulture);

        foreach (var entry in query) Entries.Add(entry);
        OnPropertyChanged(nameof(EntryCountText));
    }

    private void OnToggleEntry(MenuEntry entry, bool enabled)
    {
        try
        {
            _service.SetEnabled(entry, enabled);
            _status($"{(enabled ? "已启用" : "已禁用")}：{entry.DisplayName}");
            OnPropertyChanged(nameof(EntryCountText));
        }
        catch (Exception ex)
        {
            entry.IsDisabled = !enabled;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnToggleNewMenuItem(NewMenuItem item, bool blocked)
    {
        try
        {
            _newService.SetBlocked(item, blocked);
            _status($"{(blocked ? "已屏蔽" : "已恢复")}：{item.DisplayName}");
        }
        catch (Exception ex)
        {
            item.IsBlocked = !blocked;
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete(MenuEntry? entry)
    {
        if (entry is null) return;

        var result = MessageBox.Show(
            $"将删除菜单项“{entry.DisplayName}”。\n\n删除前会自动保存快照，可通过“撤销上一步”恢复。\n\n{entry.RegistryPath}",
            "删除菜单项",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK) return;

        try
        {
            _service.Delete(entry);
            _all.Remove(entry);
            Entries.Remove(entry);
            _status($"已删除：{entry.DisplayName}");
            OnPropertyChanged(nameof(EntryCountText));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Rename(MenuEntry? entry)
    {
        if (entry is null || !entry.CanRename) return;

        var name = InputDialog.Prompt(Application.Current.MainWindow, "重命名菜单项",
            $"为“{entry.DisplayName}”设置显示名称\n（只改显示名，不改功能）", entry.DisplayName);

        if (string.IsNullOrWhiteSpace(name) || name == entry.DisplayName) return;

        try
        {
            _service.Rename(entry, name.Trim());
            _status($"已重命名：{name}");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重命名失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyPath(object? parameter)
    {
        var text = parameter switch
        {
            MenuEntry entry => entry.RegistryPath,
            NewMenuItem item => string.IsNullOrWhiteSpace(item.ClsidText)
                ? item.Package
                : $"{item.Package} ({item.ClsidText})",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            _status($"已复制：{text}");
        }
        catch (Exception)
        {
            // 剪贴板被占用时忽略
        }
    }

    private void CopyCommandPath(object? parameter)
    {
        var text = parameter switch
        {
            MenuEntry entry => !string.IsNullOrWhiteSpace(entry.CommandPath) ? entry.CommandPath : entry.RegistryPath,
            NewMenuItem item => !string.IsNullOrWhiteSpace(item.Location) ? item.Location : item.Package,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            _status($"已复制路径：{text}");
        }
        catch (Exception)
        {
            // 剪贴板被占用时忽略
        }
    }
}
