using System.Collections.ObjectModel;
using System.IO;
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
    private string _residualTitle = "";
    private string _residualDetails = "";
    private bool _hasResidualFolder;
    private bool _hasResidualRegistry;

    private List<MenuEntry> _all = new();
    private DomainNode? _selectedDomain;
    private DomainNode? _selectedNewDomain;
    private string _searchText = "";
    private string _newSearchText = "";
    private bool _onlyThirdParty = true;
    private bool _onlyDisabled;
    private bool _onlyBlockedNew;
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

        RefreshCommand = new RelayCommand(ManualRefresh, () => !IsLoading);
        RecheckResidualCommand = new RelayCommand(RecheckResidual, () => CanRecheckResidual && !IsLoading);
        OpenResidualRegistryCommand = new RelayCommand(OpenResidualRegistry, () => HasResidualRegistry && !IsLoading);
        OpenResidualFolderCommand = new RelayCommand(OpenResidualFolder, () => HasResidualFolder && !IsLoading);
        DeleteCommand = new RelayCommand(p => Delete(p as MenuEntry), p => p is MenuEntry && !IsLoading);
        RenameCommand = new RelayCommand(p => Rename(p as MenuEntry), p => p is MenuEntry { CanRename: true } && !IsLoading);
        CopyPathCommand = new RelayCommand(CopyPath);
        CopyCommandPathCommand = new RelayCommand(CopyCommandPath);
        OpenRegeditCommand = new RelayCommand(OpenRegedit);
        ShowNewMenuCommand = new RelayCommand(() => ShowNewMenu = true);
        ShowClassicMenuCommand = new RelayCommand(() => ShowNewMenu = false);
        UninstallCommand = new RelayCommand(p => Uninstall(p as NewMenuItem), p => p is NewMenuItem && !IsLoading);
        ToggleBlockCommand = new RelayCommand(p => ToggleBlock(p as NewMenuItem), p => p is NewMenuItem && !IsLoading);

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
    public RelayCommand OpenResidualRegistryCommand { get; }
    public RelayCommand OpenResidualFolderCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand CopyCommandPathCommand { get; }
    public RelayCommand OpenRegeditCommand { get; }
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

    public string NewSearchText
    {
        get => _newSearchText;
        set
        {
            if (Set(ref _newSearchText, value)) ApplyNewMenuFilter();
        }
    }

    public bool OnlyBlockedNew
    {
        get => _onlyBlockedNew;
        set
        {
            if (Set(ref _onlyBlockedNew, value)) ApplyNewMenuFilter();
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
                SetError($"切换默认菜单模式失败：{ex.Message}");
            }
        }
    }

    public bool CanRecheckResidual => _lastUninstalled is not null;

    public string RecheckResidualText => _lastUninstalled is null
        ? "复查残留"
        : $"复查残留（{_lastUninstalled.DisplayName}）";

    public string ResidualTitle
    {
        get => _residualTitle;
        private set
        {
            if (Set(ref _residualTitle, value)) OnPropertyChanged(nameof(HasResidualResult));
        }
    }

    public string ResidualDetails
    {
        get => _residualDetails;
        private set => Set(ref _residualDetails, value);
    }

    public bool HasResidualResult => !string.IsNullOrWhiteSpace(_residualTitle);
    public bool HasResidualFolder => _hasResidualFolder;
    public bool HasResidualRegistry => _hasResidualRegistry;

    private void SetResidualResult(NewMenuItem item, string details, bool hasFolder = false, bool hasRegistry = false)
    {
        _lastUninstalled = item;
        _hasResidualFolder = hasFolder;
        _hasResidualRegistry = hasRegistry;
        ResidualTitle = $"卸载残留复查 · {item.DisplayName}";
        ResidualDetails = details;
        OnPropertyChanged(nameof(CanRecheckResidual));
        OnPropertyChanged(nameof(RecheckResidualText));
        OnPropertyChanged(nameof(HasResidualFolder));
        OnPropertyChanged(nameof(HasResidualRegistry));
        RecheckResidualCommand.RaiseCanExecuteChanged();
        OpenResidualRegistryCommand.RaiseCanExecuteChanged();
        OpenResidualFolderCommand.RaiseCanExecuteChanged();
    }

    public string NewMenuHint
    {
        get => _newMenuHint;
        private set => Set(ref _newMenuHint, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            RecheckResidualCommand.RaiseCanExecuteChanged();
            OpenResidualRegistryCommand.RaiseCanExecuteChanged();
            OpenResidualFolderCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }
    }

    public string EntryCountText => $"{Entries.Count} 项";

    private void ManualRefresh()
    {
        Refresh();
    }

    private void RecheckResidual()
    {
        if (IsLoading || _lastUninstalled is null) return;
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
            SetError("加载右键菜单失败：" + ex.Message);
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
        var kw = _newSearchText.Trim().ToLowerInvariant();

        IEnumerable<NewMenuItem> query = kind switch
        {
            MenuDomainKind.Unknown => NewMenuItems.Where(i => !i.ScopeConfirmed),
            MenuDomainKind.Drive => NewMenuItems.Where(i => i.ScopeConfirmed && MatchesDriveScope(i)),
            _ => NewMenuItems.Where(i => i.ScopeConfirmed && i.Scopes.Contains(kind))
        };

        query = query
            .Where(i => !_onlyBlockedNew || i.IsBlocked)
            .Where(i => kw.Length == 0 || i.SearchBlob.Contains(kw));

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
            SetError($"屏蔽或恢复菜单项失败：{ex.Message}");
        }
    }

    private void Uninstall(NewMenuItem? item)
    {
        if (item is null) return;

        var all = _uninstall.LoadAll();
        var candidates = _uninstall.Match(item.Location, item.DisplayName, all);

        if (candidates.Count == 0)
        {
            SetError($"没有找到「{item.DisplayName}」的卸载信息。安装位置：{(string.IsNullOrWhiteSpace(item.Location) ? "未知" : item.Location)}。请到「设置 → 应用」里查找。");
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
            "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _uninstall.Launch(chosen);
            _status($"已启动卸载程序：{chosen.Name}");
            StartResidualWatch(item);
        }
        catch (Exception ex)
        {
            SetError($"无法启动「{chosen.Name}」的卸载程序：{ex.Message}；路径：{chosen.UninstallString}");
        }
    }

    private void StartResidualWatch(NewMenuItem item)
    {
        StopWatch();
        _watching = item;
        SetResidualResult(item, "已启动卸载程序，正在等待卸载完成。完成后将在此显示复查结果，也可随时手动复查残留。");
        SetError(null);
        _uninstallDeadline = DateTime.UtcNow.AddMinutes(10);

        _uninstallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _uninstallTimer.Tick += (_, _) =>
        {
            if (IsLoading) return;
            var watching = _watching;
            if (watching is null)
            {
                StopWatch();
                return;
            }

            try
            {
                var still = _uninstall.FindStillRegistered(watching.Clsids);
                if (still.Count == 0)
                {
                    StopWatch();
                    ReportResidual(watching);
                }
                else if (DateTime.UtcNow > _uninstallDeadline)
                {
                    StopWatch();
                    SetResidualResult(watching, "等待卸载超时，尚未确认卸载完成。若已完成卸载，可点「复查残留」手动检查。");
                    _status($"等待「{watching.DisplayName}」卸载超时，可在本页手动复查。");
                }
            }
            catch (Exception ex)
            {
                StopWatch();
                SetResidualResult(watching, $"卸载监测失败：{ex.Message}\n可点「复查残留」重试。");
                SetError($"监测「{watching.DisplayName}」卸载状态失败：{ex.Message}");
            }
        };
        _uninstallTimer.Start();
        _status($"正在等待「{item.DisplayName}」卸载完成（最多等 10 分钟，也可随时点「复查残留」）…");
    }

    private void StopWatch()
    {
        _uninstallTimer?.Stop();
        _uninstallTimer = null;
        _watching = null;
    }

    private void ReportResidual(NewMenuItem item)
    {
        if (IsLoading) return;

        try
        {
            var issues = _uninstall.CheckResidual(item.DisplayName, item.Location, item.Clsids);
            var hasFolder = issues.Any(i => i.StartsWith("安装目录仍然存在且非空：", StringComparison.Ordinal));
            var hasRegistry = _uninstall.FindStillRegistered(item.Clsids).Count > 0
                              || item.Clsids.Any(ContextMenuService.IsBlocked);
            var details = issues.Count == 0
                ? "已卸载，未发现明显残留。"
                : $"发现 {issues.Count} 处残留：\n• " + string.Join("\n• ", issues)
                  + "\n\nWinKit 不会自动删除目录或注册表项；可使用下方操作自行检查。";

            SetResidualResult(item, details, hasFolder, hasRegistry);
            SetError(null);
            _status(issues.Count == 0
                ? $"「{item.DisplayName}」已卸载，未发现残留"
                : $"「{item.DisplayName}」复查完成，发现 {issues.Count} 处残留");
            Refresh();
        }
        catch (Exception ex)
        {
            SetResidualResult(item, $"残留复查失败：{ex.Message}\n可点「复查残留」重试。");
            SetError($"复查「{item.DisplayName}」卸载残留失败：{ex.Message}");
        }
    }

    private void OpenResidualRegistry()
    {
        if (IsLoading || !_hasResidualRegistry || _lastUninstalled is not { } item) return;

        try
        {
            foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
            {
                foreach (var package in item.Packages)
                {
                    foreach (var clsid in item.Clsids)
                    {
                        var normalized = clsid.Trim().Trim('{', '}');
                        var path = RegPath.Of(hive, $@"Software\Classes\PackagedCom\Package\{package}\Class\{{{normalized}}}");
                        if (!Reg.Exists(path)) continue;
                        if (!Native.OpenInRegedit(path.FullPath)) SetError("无法打开注册表编辑器，请确认未取消授权且系统允许启动 regedit。");
                        else _status($"已定位残留注册表项：{path.FullPath}");
                        return;
                    }
                }

                var blocked = RegPath.Of(hive, @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked");
                if (!item.Clsids.Any(clsid => Reg.HasValue(blocked, clsid))) continue;
                if (!Native.OpenInRegedit(blocked.FullPath)) SetError("无法打开注册表编辑器，请确认未取消授权且系统允许启动 regedit。");
                else _status($"已定位残留屏蔽记录：{blocked.FullPath}");
                return;
            }

            ReportResidual(item);
            SetError("原残留注册表项已发生变化，请根据更新后的复查结果继续处理。");
        }
        catch (Exception ex)
        {
            SetError($"定位残留注册表项失败：{ex.Message}");
        }
    }

    private void OpenResidualFolder()
    {
        if (IsLoading || !_hasResidualFolder || _lastUninstalled is not { } item) return;
        if (!Directory.Exists(item.Location))
        {
            ReportResidual(item);
            return;
        }

        try
        {
            Native.OpenPath(item.Location);
        }
        catch (Exception ex)
        {
            SetError($"打开残留目录失败：{ex.Message}");
        }
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
        if (Native.RequireAdmin("修改右键菜单") is { } adminNeeded)
        {
            Rollback(entry, enabled);
            SetError(adminNeeded);
            return;
        }

        try
        {
            _service.SetEnabled(entry, enabled);
            _status($"{(enabled ? "已启用" : "已禁用")}：{entry.DisplayName}");
            OnPropertyChanged(nameof(EntryCountText));
        }
        catch (Exception ex)
        {
            Rollback(entry, enabled);
            SetError($"{(enabled ? "启用" : "禁用")}「{entry.DisplayName}」失败：{ex.Message}");
        }
    }

    private void OnToggleNewMenuItem(NewMenuItem item, bool blocked)
    {
        if (Native.RequireAdmin("修改「新建」菜单") is { } adminNeeded)
        {
            Rollback(item, blocked);
            SetError(adminNeeded);
            return;
        }

        try
        {
            _newService.SetBlocked(item, blocked);
            _status($"{(blocked ? "已屏蔽" : "已恢复")}：{item.DisplayName}");
        }
        catch (Exception ex)
        {
            Rollback(item, blocked);
            SetError($"{(blocked ? "屏蔽" : "恢复")}「{item.DisplayName}」失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 回滚勾选状态。必须先摘掉回调：IsDisabled 的 setter 会再次触发 ToggleRequested，
    /// 若反向写入也失败就会无限互递归，StackOverflow 会直接杀掉进程、连提示都留不下。
    /// 其它四个页面（此电脑/导航栏/任务栏/系统调优）早就是这么写的，这里之前只写回了属性。
    /// </summary>
    private static void Rollback(MenuEntry entry, bool attempted)
    {
        var handler = entry.ToggleRequested;
        entry.ToggleRequested = null;
        entry.IsDisabled = !attempted;
        entry.ToggleRequested = handler;
    }

    private static void Rollback(NewMenuItem item, bool attempted)
    {
        var handler = item.ToggleRequested;
        item.ToggleRequested = null;
        item.IsBlocked = !attempted;
        item.ToggleRequested = handler;
    }

    private void Delete(MenuEntry? entry)
    {
        if (Native.RequireAdmin("删除菜单项") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }
        if (entry is null) return;

        var result = MessageBox.Show(
            $"将删除菜单项“{entry.DisplayName}”。\n\n删除前会自动保存快照，可通过“撤销上一步”恢复。\n\n{entry.RegistryPath}",
            "删除菜单项",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

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
            SetError($"删除菜单项失败：{ex.Message}");
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
            SetError($"重命名菜单项失败：{ex.Message}");
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

    private void OpenRegedit(object? parameter)
    {
        string? regPath = parameter switch
        {
            MenuEntry entry when entry.RegistryPath.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) => entry.RegistryPath,
            NewMenuItem item when item.Packages.Count > 0 =>
                $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\PackagedCom\Package\{item.Packages[0]}",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(regPath))
        {
            SetError("当前条目不是注册表键（如发送到快捷方式），无法在 regedit 中打开。");
            return;
        }

        if (Native.OpenInRegedit(regPath))
            _status($"已在注册表编辑器中定位：{regPath}");
        else
            SetError("无法打开注册表编辑器，请确认未取消授权且系统允许启动 regedit。");
    }

    private string _errorText = "";

    /// <summary>
    /// 页面级错误：开关切换失败时统一由 ErrorBar 展示。
    /// 这里原本弹 MessageBox，一闪就没了、也没留下任何痕迹，翻不到刚才失败的是哪一项。
    /// </summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public void SetError(string? message) => ErrorText = message ?? "";
}
