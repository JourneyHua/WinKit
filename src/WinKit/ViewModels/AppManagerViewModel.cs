using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class AppManagerViewModel : ObservableObject, IModule
{
    private readonly AppManagerService _service;
    private readonly RuntimeCheckService _runtimeService = new();
    private readonly SystemToolCatalogService _toolService = new();
    private readonly UninstallService _uninstall = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private List<InstalledAppItem> _all = new();
    private List<RuntimeComponentItem> _allRuntimes = new();
    private List<SystemToolItem> _allTools = new();
    private string _searchText = "";
    private string _runtimeSearchText = "";
    private string _toolSearchText = "";
    private string _sourceFilter = "all"; // all | desktop | store
    private string _subView = "apps"; // apps | runtimes | tools | uninstall
    private bool _onlyUpdatable;
    private bool _onlyMissingRuntimes;
    private bool _onlyUninstalledTools;
    private bool _isBusy;
    private string _summaryText = "";
    private string _runtimeSummaryText = "";
    private string _toolSummaryText = "";
    private string _hintText = "汇总本机已安装的传统桌面软件与 Microsoft Store / MSIX 打包应用。点「检查更新」将通过系统内置 winget 对比最新版本；卸载后自动扫描注册表残留（支持一键快照清理）与残留目录。";

    private DispatcherTimer? _uninstallTimer;
    private DateTime _uninstallDeadline;
    private InstalledAppItem? _lastUninstalled;
    private AppResidualReport? _residualReport;
    private string _residualTitle = "";
    private string _residualDetails = "";

    public AppManagerViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new AppManagerService(journal);
        _status = status;
        _setBusy = setBusy;

        Apps = new ObservableCollection<InstalledAppItem>();
        Runtimes = new ObservableCollection<RuntimeComponentItem>();
        Tools = new ObservableCollection<SystemToolItem>();
        UninstallCenter = new UninstallCenterViewModel(journal, status);

        RefreshCommand = new RelayCommand(ManualRefresh, () => !IsBusy);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), () => !IsBusy);
        UpgradeAppCommand = new RelayCommand(p => _ = UpgradeAppAsync(p as InstalledAppItem), p => p is InstalledAppItem { HasUpdate: true } && !IsBusy);
        UpgradeAllCommand = new RelayCommand(() => _ = UpgradeAllAsync(), () => HasUpdatable && !IsBusy);
        UninstallAppCommand = new RelayCommand(p => _ = UninstallAppAsync(p as InstalledAppItem), p => p is InstalledAppItem && !IsBusy);
        OpenLocationCommand = new RelayCommand(p => OpenLocation(p as InstalledAppItem));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as InstalledAppItem));
        OpenRegeditCommand = new RelayCommand(p => OpenRegedit(p as InstalledAppItem));
        RecheckResidualCommand = new RelayCommand(RecheckResidual, () => CanRecheckResidual && !IsBusy);
        CleanResidualRegistryCommand = new RelayCommand(CleanResidualRegistry, () => HasRegistryResiduals && !IsBusy);
        OpenResidualFolderCommand = new RelayCommand(OpenResidualFolder, () => HasResidualFolder && !IsBusy);
        ScanResidualCommand = new RelayCommand(p => ScanItemResidual(p as InstalledAppItem), p => p is InstalledAppItem && !IsBusy);

        FilterAllCommand = new RelayCommand(() => SourceFilter = "all");
        FilterDesktopCommand = new RelayCommand(() => SourceFilter = "desktop");
        FilterStoreCommand = new RelayCommand(() => SourceFilter = "store");
        FilterScoopCommand = new RelayCommand(() => SourceFilter = "scoop");

        ShowAppsViewCommand = new RelayCommand(() => SubView = "apps");
        ShowRuntimesViewCommand = new RelayCommand(() => SubView = "runtimes");
        ShowToolsViewCommand = new RelayCommand(() => SubView = "tools");
        ShowUninstallCenterCommand = new RelayCommand(() => SubView = "uninstall");
        CheckRuntimesCommand = new RelayCommand(RefreshRuntimes);
        InstallRuntimeCommand = new RelayCommand(p => _ = InstallRuntimeAsync(p as RuntimeComponentItem));
        InstallAllMissingRuntimesCommand = new RelayCommand(() => _ = InstallAllMissingRuntimesAsync());

        RefreshToolsCommand = new RelayCommand(RefreshTools);
        LaunchToolCommand = new RelayCommand(p => LaunchTool(p as SystemToolItem));
        InstallToolCommand = new RelayCommand(p => _ = InstallToolAsync(p as SystemToolItem));
        InstallSelectedToolsCommand = new RelayCommand(() => _ = InstallSelectedToolsAsync());
        SelectUninstalledToolsCommand = new RelayCommand(SelectUninstalledTools);
        OpenToolLocationCommand = new RelayCommand(p => OpenToolLocation(p as SystemToolItem));
    }

    public ObservableCollection<InstalledAppItem> Apps { get; }
    public ObservableCollection<RuntimeComponentItem> Runtimes { get; }
    public ObservableCollection<SystemToolItem> Tools { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand UpgradeAppCommand { get; }
    public RelayCommand UpgradeAllCommand { get; }
    public RelayCommand UninstallAppCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand OpenRegeditCommand { get; }
    public RelayCommand RecheckResidualCommand { get; }
    public RelayCommand CleanResidualRegistryCommand { get; }
    public RelayCommand OpenResidualFolderCommand { get; }
    public RelayCommand ScanResidualCommand { get; }

    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterDesktopCommand { get; }
    public RelayCommand FilterStoreCommand { get; }
    public RelayCommand FilterScoopCommand { get; }

    public RelayCommand ShowAppsViewCommand { get; }
    public RelayCommand ShowRuntimesViewCommand { get; }
    public RelayCommand ShowToolsViewCommand { get; }
    public RelayCommand ShowUninstallCenterCommand { get; }
    public RelayCommand CheckRuntimesCommand { get; }
    public RelayCommand InstallRuntimeCommand { get; }
    public RelayCommand InstallAllMissingRuntimesCommand { get; }

    public RelayCommand RefreshToolsCommand { get; }
    public RelayCommand LaunchToolCommand { get; }
    public RelayCommand InstallToolCommand { get; }
    public RelayCommand InstallSelectedToolsCommand { get; }
    public RelayCommand SelectUninstalledToolsCommand { get; }
    public RelayCommand OpenToolLocationCommand { get; }

    public string SubView
    {
        get => _subView;
        set
        {
            if (!Set(ref _subView, value)) return;
            OnPropertyChanged(nameof(ShowInstalledApps));
            OnPropertyChanged(nameof(ShowRuntimes));
            OnPropertyChanged(nameof(ShowTools));
            OnPropertyChanged(nameof(ShowUninstallCenter));
            if (value == "runtimes" && _allRuntimes.Count == 0)
                RefreshRuntimes();
            if (value == "tools" && _allTools.Count == 0)
                RefreshTools();
        }
    }

    public bool ShowInstalledApps => _subView == "apps";
    public bool ShowRuntimes
    {
        get => _subView == "runtimes";
        set => SubView = value ? "runtimes" : "apps";
    }
    public bool ShowTools
    {
        get => _subView == "tools";
        set => SubView = value ? "tools" : "apps";
    }
    public bool ShowUninstallCenter
    {
        get => _subView == "uninstall";
        set => SubView = value ? "uninstall" : "apps";
    }

    /// <summary>卸载残留清理面板（拖入文件夹 / 快捷方式 → 识别 → 扫残留 → 清理）。</summary>
    public UninstallCenterViewModel UninstallCenter { get; }

    public string ToolSearchText
    {
        get => _toolSearchText;
        set
        {
            if (Set(ref _toolSearchText, value)) ApplyToolFilter();
        }
    }

    public bool OnlyUninstalledTools
    {
        get => _onlyUninstalledTools;
        set
        {
            if (Set(ref _onlyUninstalledTools, value)) ApplyToolFilter();
        }
    }

    public string ToolSummaryText
    {
        get => _toolSummaryText;
        private set => Set(ref _toolSummaryText, value);
    }

    public int SelectedToolsCount => _allTools.Count(t => t.IsSelected);
    public bool HasSelectedTools => SelectedToolsCount > 0;
    public string InstallSelectedToolsText => $"一键安装勾选项（{SelectedToolsCount}）";

    public string RuntimeSearchText
    {
        get => _runtimeSearchText;
        set
        {
            if (Set(ref _runtimeSearchText, value)) ApplyRuntimeFilter();
        }
    }

    public bool OnlyMissingRuntimes
    {
        get => _onlyMissingRuntimes;
        set
        {
            if (Set(ref _onlyMissingRuntimes, value)) ApplyRuntimeFilter();
        }
    }

    public string RuntimeSummaryText
    {
        get => _runtimeSummaryText;
        private set => Set(ref _runtimeSummaryText, value);
    }

    public int MissingRuntimeCount => _allRuntimes.Count(r => !r.IsInstalled);
    public bool HasMissingRuntimes => MissingRuntimeCount > 0;
    public string InstallAllMissingText => $"一键补齐缺失运行库（{MissingRuntimeCount}）";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    public string SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (!Set(ref _sourceFilter, value)) return;
            OnPropertyChanged(nameof(IsAllSelected));
            OnPropertyChanged(nameof(IsDesktopSelected));
            OnPropertyChanged(nameof(IsStoreSelected));
            OnPropertyChanged(nameof(IsScoopSelected));
            ApplyFilter();
        }
    }

    public bool IsAllSelected => _sourceFilter == "all";
    public bool IsDesktopSelected => _sourceFilter == "desktop";
    public bool IsStoreSelected => _sourceFilter == "store";
    public bool IsScoopSelected => _sourceFilter == "scoop";

    public bool OnlyUpdatable
    {
        get => _onlyUpdatable;
        set
        {
            if (Set(ref _onlyUpdatable, value)) ApplyFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                _setBusy?.Invoke(value);
                RefreshCommand.RaiseCanExecuteChanged();
                CheckUpdatesCommand.RaiseCanExecuteChanged();
                UpgradeAppCommand.RaiseCanExecuteChanged();
                UpgradeAllCommand.RaiseCanExecuteChanged();
                UninstallAppCommand.RaiseCanExecuteChanged();
                RecheckResidualCommand.RaiseCanExecuteChanged();
                CleanResidualRegistryCommand.RaiseCanExecuteChanged();
                OpenResidualFolderCommand.RaiseCanExecuteChanged();
                ScanResidualCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string HintText
    {
        get => _hintText;
        private set => Set(ref _hintText, value);
    }

    public int UpdatableCount => _all.Count(a => a.HasUpdate);
    public bool HasUpdatable => UpdatableCount > 0;
    public string UpgradeAllText => $"一键更新全部（{UpdatableCount}）";

    public bool CanRecheckResidual => _lastUninstalled is not null;
    public string RecheckResidualText => _lastUninstalled is null
        ? "复查残留"
        : $"复查残留（{_lastUninstalled.Name}）";

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
    public bool HasRegistryResiduals => _residualReport is { RegistryResiduals.Count: > 0 };
    public bool HasResidualFolder => _residualReport is { FolderStillExists: true }
                                     && !string.IsNullOrWhiteSpace(_residualReport.InstallLocation);

    private void SetResidualResult(InstalledAppItem app, string details, AppResidualReport? report = null)
    {
        _lastUninstalled = app;
        _residualReport = report;
        ResidualTitle = $"卸载残留复查 · {app.Name}";
        ResidualDetails = details;
        OnPropertyChanged(nameof(CanRecheckResidual));
        OnPropertyChanged(nameof(RecheckResidualText));
        OnPropertyChanged(nameof(HasRegistryResiduals));
        OnPropertyChanged(nameof(HasResidualFolder));
        RecheckResidualCommand.RaiseCanExecuteChanged();
        CleanResidualRegistryCommand.RaiseCanExecuteChanged();
        OpenResidualFolderCommand.RaiseCanExecuteChanged();
    }

    private void ManualRefresh()
    {
        Refresh();
    }

    public void Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            var previousUpdates = _all
                .Where(a => a.HasUpdate)
                .ToDictionary(a => a.Id, a => (a.LatestVersion, a.WingetId), StringComparer.OrdinalIgnoreCase);

            _all = _service.LoadAll();

            foreach (var item in _all)
            {
                if (previousUpdates.TryGetValue(item.Id, out var prev)
                    && !string.Equals(item.Version, prev.LatestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    item.LatestVersion = prev.LatestVersion;
                    item.WingetId = prev.WingetId;
                }
            }

            ApplyFilter();
            if (_allRuntimes.Count == 0)
            {
                _allRuntimes = _runtimeService.CheckAll();
                ApplyRuntimeFilter();
            }
            if (_allTools.Count == 0)
            {
                LoadToolsInternal();
            }
            _status($"已加载 {_all.Count} 个应用 · 运行库就绪 {_allRuntimes.Count(r => r.IsInstalled)}/{_allRuntimes.Count} · 常用工具已装 {_allTools.Count(t => t.IsInstalled)}/{_allTools.Count}");
        }
        catch (Exception ex)
        {
            SetError($"加载应用列表失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Apps.Clear();
        var kw = _searchText.Trim().ToLowerInvariant();

        var query = _all
            .Where(a => _sourceFilter switch
            {
                "desktop" => a.Kind == AppSourceKind.Desktop,
                "store" => a.Kind == AppSourceKind.Store,
                "scoop" => a.Kind == AppSourceKind.Scoop,
                _ => true
            })
            .Where(a => !_onlyUpdatable || a.HasUpdate)
            .Where(a => kw.Length == 0 || a.SearchBlob.Contains(kw))
            .OrderByDescending(a => a.HasUpdate)
            .ThenBy(a => a.Kind == AppSourceKind.Desktop ? 0 : a.Kind == AppSourceKind.Scoop ? 1 : 2)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture);

        foreach (var item in query) Apps.Add(item);

        var desktopCount = _all.Count(a => a.Kind == AppSourceKind.Desktop);
        var storeCount = _all.Count(a => a.Kind == AppSourceKind.Store);
        var scoopCount = _all.Count(a => a.Kind == AppSourceKind.Scoop);
        SummaryText = $"共 {_all.Count} 个应用（桌面 {desktopCount} · 商店/打包 {storeCount} · Scoop {scoopCount}）" +
                      $"· 可更新 {UpdatableCount} 个 · 当前显示 {Apps.Count} 个";

        OnPropertyChanged(nameof(UpdatableCount));
        OnPropertyChanged(nameof(HasUpdatable));
        OnPropertyChanged(nameof(UpgradeAllText));
    }

    private async Task CheckUpdatesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在通过 winget 检查已安装软件的可用更新…");
            var (matched, raw, error) = await _service.CheckUpdatesAsync(_all);

            if (!string.IsNullOrWhiteSpace(error))
            {
                _status($"检查更新失败：{error}");
                SetError($"调用 winget 检查更新失败：{error}");
                return;
            }

            ApplyFilter();
            _status(matched > 0
                ? $"发现 {matched} 个可更新应用（winget 共返回 {raw.Count} 条升级记录）"
                : "所有已匹配软件均为最新版本");
        }
        catch (Exception ex)
        {
            _status($"检查更新异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UpgradeAppAsync(InstalledAppItem? app)
    {
        if (app is null || IsBusy) return;

        var confirm = MessageBox.Show(
            $"将通过 winget 更新「{app.Name}」：\n\n" +
            $"当前版本：{(string.IsNullOrWhiteSpace(app.Version) ? "未知" : app.Version)}\n" +
            $"目标版本：{app.LatestVersion}\n\n" +
            "优先以静默模式后台升级；若安装器要求交互，会自动弹出安装向导。确定继续吗？",
            "更新应用", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        var updated = false;
        try
        {
            var (ok, msg) = await _service.UpgradeAppAsync(app, s => _status(s));
            _status(msg);

            if (ok)
            {
                app.Version = app.LatestVersion;
                app.LatestVersion = "";
                updated = true;
            }
            else
            {
                SetError(msg);
            }
        }
        finally
        {
            IsBusy = false;
        }

        // Refresh() 内部有 if (IsBusy) return，必须等 busy 复位后再刷新，否则列表与统计停在更新前
        if (updated) Refresh();
    }

    private async Task UpgradeAllAsync()
    {
        var targets = Apps.Where(a => a.HasUpdate).ToList();
        if (targets.Count == 0) targets = _all.Where(a => a.HasUpdate).ToList();
        if (targets.Count == 0 || IsBusy) return;

        var names = string.Join("\n• ", targets.Take(10).Select(t => $"{t.Name} ({t.VersionText})"));
        if (targets.Count > 10) names += $"\n…等共 {targets.Count} 个应用";

        var confirm = MessageBox.Show(
            $"将依次通过 winget 更新以下 {targets.Count} 个应用：\n\n• {names}\n\n确定开始批量更新吗？",
            "一键更新全部", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        var succeeded = 0;
        var failed = new List<string>();

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                var prefix = $"[{i + 1}/{targets.Count}] ";
                var (ok, msg) = await _service.UpgradeAppAsync(item, s => _status(prefix + s));
                if (ok)
                {
                    succeeded++;
                    item.Version = item.LatestVersion;
                    item.LatestVersion = "";
                }
                else
                {
                    failed.Add($"{item.Name}：{msg}");
                }
            }

            _status($"批量更新完成：成功 {succeeded} 个，失败 {failed.Count} 个");
            if (failed.Count > 0)
            {
                SetError($"已完成 {succeeded} 个应用更新，以下 {failed.Count} 个未完成：\n• " + string.Join("\n• ", failed));
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task UninstallAppAsync(InstalledAppItem? app)
    {
        if (Native.RequireAdmin("卸载软件") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }
        if (app is null || IsBusy) return;

        if (app.Kind == AppSourceKind.Scoop)
        {
            if (!ScoopService.CommandAvailable())
            {
                SetError("找不到 scoop.cmd，请确认 Scoop 已安装并加入 PATH。");
                return;
            }

            var confirmScoop = MessageBox.Show(
                $"将通过 scoop 卸载「{app.Name}」：\n\nscoop uninstall {app.ScoopName}\n\n" +
                "不删除任何个人数据；如需保留配置可用 scoop uninstall 的 -p 参数（本工具不代传）。确定继续吗？",
                "卸载 Scoop 包", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            if (confirmScoop != MessageBoxResult.OK) return;

            IsBusy = true;
            try
            {
                _status($"正在卸载 Scoop 包「{app.Name}」…");
                var (ok, msg) = await new ScoopService().RunScoopAsync($"uninstall {app.ScoopName}", TimeSpan.FromMinutes(3));
                _status(ok ? $"已卸载 Scoop 包：{app.Name}" : $"卸载失败：{msg}");
                if (!ok)
                {
                    SetError(msg);
                    return;
                }
            }
            finally
            {
                IsBusy = false;
            }

            Refresh();
            return;
        }

        if (app.Kind == AppSourceKind.Store)
        {
            var confirmStore = MessageBox.Show(
                $"将卸载商店/打包应用「{app.Name}」：\n\n包名：{app.PackageFullName}\n\n确定继续吗？",
                "卸载打包应用", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

            if (confirmStore != MessageBoxResult.OK) return;

            IsBusy = true;
            try
            {
                _status($"正在卸载「{app.Name}」…");
                var (ok, msg) = await _service.UninstallStoreAppAsync(app);
                _status(msg);
                if (!ok)
                {
                    SetError(msg);
                    return;
                }
            }
            finally
            {
                IsBusy = false;
            }

            Refresh();
            return;
        }

        var confirm = MessageBox.Show(
            $"将启动「{app.Name}」自带的官方卸载程序：\n\n{app.UninstallString}\n\n" +
            "卸载完成后，WinKit 会自动扫描残留注册表项（可一键快照清理）和残留安装目录。继续吗？",
            "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _uninstall.Launch(new UninstallEntry
            {
                Name = app.Name,
                UninstallString = app.UninstallString,
                InstallLocation = app.InstallLocation,
                RegistryPath = app.RegistryPath,
                Version = app.Version
            });

            _status($"已启动卸载程序：{app.Name}");
            StartUninstallWatch(app);
        }
        catch (Exception ex)
        {
            SetResidualResult(app, $"无法启动卸载程序：{ex.Message}\n请检查卸载信息后重试。");
            SetError($"无法启动卸载程序：{ex.Message}");
        }
    }

    private void StartUninstallWatch(InstalledAppItem app)
    {
        StopWatch();
        SetResidualResult(app, "已启动卸载程序，正在等待卸载完成。完成后将在此显示复查结果，也可随时手动复查残留。");
        _uninstallDeadline = DateTime.UtcNow.AddMinutes(10);

        _uninstallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _uninstallTimer.Tick += (_, _) =>
        {
            if (IsBusy) return;
            try
            {
                var regGone = !Reg.Exists(RegPath.Of(app.Hive, app.SubKey));
                var exe = ContextMenuService.ExtractExecutable(app.UninstallString);
                var exeGone = !string.IsNullOrWhiteSpace(exe)
                              && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                              && !File.Exists(Environment.ExpandEnvironmentVariables(exe));

                if (regGone || exeGone)
                {
                    StopWatch();
                    HandleResidualCheck(app);
                }
                else if (DateTime.UtcNow > _uninstallDeadline)
                {
                    StopWatch();
                    SetResidualResult(app, "等待卸载超时，尚未确认卸载完成。若已完成卸载，可点「复查残留」手动检查。");
                    _status($"等待「{app.Name}」卸载超时，可在应用页手动复查残留。");
                }
            }
            catch (Exception ex)
            {
                StopWatch();
                SetResidualResult(app, $"卸载监测失败：{ex.Message}\n可点「复查残留」重试。");
                SetError($"监测「{app.Name}」卸载状态失败：{ex.Message}");
                _status($"「{app.Name}」卸载监测失败，详情已保留在应用页。");
            }
        };
        _uninstallTimer.Start();
        _status($"正在等待「{app.Name}」卸载完成（也可随时手动复查残留）…");
    }

    private void StopWatch()
    {
        if (_uninstallTimer is null) return;
        _uninstallTimer.Stop();
        _uninstallTimer = null;
    }

    private void RecheckResidual()
    {
        if (IsBusy || _lastUninstalled is null) return;
        var pending = _lastUninstalled;
        StopWatch();
        HandleResidualCheck(pending);
    }

    private void ScanItemResidual(InstalledAppItem? app)
    {
        if (app is null || IsBusy) return;

        // 用户在列表上点「扫描残留」是想清干净的，直接把他带到能清干净的地方，
        // 而不是弹一串「这个目录 WinKit 不代删」的提示
        var folder = GetInstallPath(app);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            SubView = "uninstall";
            UninstallCenter.AcceptPath(folder);
            return;
        }

        HandleResidualCheck(app);
    }

    private static string GetInstallPath(InstalledAppItem app)
    {
        var path = app.InstallLocation;
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim().Trim('"');

        if (Directory.Exists(path)) return path;
        if (File.Exists(path)) return Path.GetDirectoryName(path) ?? "";
        return "";
    }

    private void HandleResidualCheck(InstalledAppItem app)
    {
        if (IsBusy) return;

        try
        {
            var report = _service.ScanResiduals(app);
            SetResidualResult(app, BuildResidualDetails(report), report);
            SetError(null);
            _status(report.HasAny
                ? $"「{app.Name}」复查完成，发现 {report.RegistryResiduals.Count + (report.FolderStillExists ? 1 : 0)} 处残留"
                : $"「{app.Name}」未发现明显残留");
            Refresh();
        }
        catch (Exception ex)
        {
            SetResidualResult(app, $"残留扫描失败：{ex.Message}\n可点「复查残留」重试。");
            SetError($"扫描「{app.Name}」卸载残留失败：{ex.Message}");
        }
    }

    private static string BuildResidualDetails(AppResidualReport report)
    {
        if (!report.HasAny) return "未发现失效注册表项或非空安装目录残留。";

        var lines = new List<string>();
        if (report.RegistryResiduals.Count > 0)
        {
            lines.Add($"发现 {report.RegistryResiduals.Count} 处注册表残留：");
            lines.AddRange(report.RegistryResiduals.Take(8).Select(r => $"• {r.Description}"));
            if (report.RegistryResiduals.Count > 8)
                lines.Add($"…另有 {report.RegistryResiduals.Count - 8} 处");
            lines.Add("清理只会在你确认后执行，并会先创建可撤销快照。");
        }

        if (report.FolderStillExists)
        {
            lines.Add($"安装目录仍包含文件：{report.InstallLocation}");
            lines.Add("为避免误删个人数据，WinKit 不会自动删除目录。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void CleanResidualRegistry()
    {
        if (IsBusy || _lastUninstalled is not { } app
                   || _residualReport is not { RegistryResiduals.Count: > 0 } report) return;

        try
        {
            report = _service.ScanResiduals(app);
            SetResidualResult(app, BuildResidualDetails(report), report);
            if (report.RegistryResiduals.Count == 0)
            {
                SetError(null);
                _status($"「{app.Name}」已无可清理的注册表残留");
                return;
            }
        }
        catch (Exception ex)
        {
            SetResidualResult(app, $"清理前复查失败：{ex.Message}");
            SetError($"清理前无法确认「{app.Name}」的残留状态：{ex.Message}");
            return;
        }

        var preview = string.Join("\n• ", report.RegistryResiduals.Take(8).Select(r => r.Description));
        if (report.RegistryResiduals.Count > 8)
            preview += $"\n…另有 {report.RegistryResiduals.Count - 8} 处";

        var confirm = MessageBox.Show(Application.Current.MainWindow,
            $"将清理「{app.Name}」的 {report.RegistryResiduals.Count} 处注册表残留：\n\n• {preview}\n\n" +
            "操作前会创建可撤销快照。继续清理吗？",
            "清理卸载注册表残留", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        if (Native.RequireAdmin("清理注册表残留") is { } adminNeeded)
        {
            SetError(adminNeeded);
            return;
        }

        IsBusy = true;
        try
        {
            var cleaned = _service.CleanRegistryResiduals(report);
            var refreshedReport = _service.ScanResiduals(app);
            SetResidualResult(app,
                $"已清理 {cleaned} 处注册表残留，操作前已创建可撤销快照。\n\n{BuildResidualDetails(refreshedReport)}",
                refreshedReport);
            SetError(null);
            _status($"已清理「{app.Name}」的 {cleaned} 处注册表残留");
        }
        catch (Exception ex)
        {
            SetResidualResult(app, $"清理或复查失败：{ex.Message}\n可检查错误提示后重新复查。");
            SetError($"清理「{app.Name}」注册表残留失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
    }

    private void OpenResidualFolder()
    {
        if (IsBusy || _lastUninstalled is not { } app
                   || _residualReport is not { FolderStillExists: true } report) return;

        if (!Directory.Exists(report.InstallLocation))
        {
            HandleResidualCheck(app);
            return;
        }

        try
        {
            Native.OpenPath(report.InstallLocation);
        }
        catch (Exception ex)
        {
            SetError($"打开残留目录失败：{ex.Message}");
        }
    }

    private void OpenLocation(InstalledAppItem? app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.InstallLocation)) return;
        if (!Directory.Exists(app.InstallLocation))
        {
            SetError($"目录不存在：{app.InstallLocation}");
            return;
        }

        try
        {
            Native.OpenPath(app.InstallLocation);
        }
        catch (Exception ex)
        {
            SetError($"打开目录失败：{ex.Message}");
        }
    }

    private void CopyPath(InstalledAppItem? app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.CopyablePath)) return;
        try
        {
            Clipboard.SetText(app.CopyablePath);
            _status($"已复制路径：{app.CopyablePath}");
        }
        catch (Exception) { }
    }

    private void OpenRegedit(InstalledAppItem? app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.RegistryPath))
        {
            SetError("该条目没有直接关联的本地注册表路径。");
            return;
        }

        if (Native.OpenInRegedit(app.RegistryPath))
            _status($"已在注册表编辑器中定位：{app.Name}");
        else
            SetError("无法打开注册表编辑器，请确认未取消授权且系统允许启动 regedit。");
    }

    public void RefreshRuntimes()
    {
        try
        {
            _allRuntimes = _runtimeService.CheckAll();
            ApplyRuntimeFilter();
            var installed = _allRuntimes.Count(r => r.IsInstalled);
            _status($"运行库体检完成：共 {_allRuntimes.Count} 项，已安装 {installed} 项，缺失 {_allRuntimes.Count - installed} 项");
        }
        catch (Exception ex)
        {
            SetError($"运行库检测失败：{ex.Message}");
        }
    }

    private void ApplyRuntimeFilter()
    {
        Runtimes.Clear();
        var kw = _runtimeSearchText.Trim().ToLowerInvariant();

        var query = _allRuntimes
            .Where(r => !_onlyMissingRuntimes || !r.IsInstalled)
            .Where(r => kw.Length == 0 || r.SearchBlob.Contains(kw))
            .OrderBy(r => r.IsInstalled ? 1 : 0)
            .ThenBy(r => r.Category)
            .ThenBy(r => r.Name);

        foreach (var item in query) Runtimes.Add(item);

        var installedCount = _allRuntimes.Count(r => r.IsInstalled);
        var missingCount = _allRuntimes.Count - installedCount;
        RuntimeSummaryText = $"共检测 {_allRuntimes.Count} 项核心运行库 · 已就绪 {installedCount} 项 · 缺失 {missingCount} 项 · 当前显示 {Runtimes.Count} 项";

        OnPropertyChanged(nameof(MissingRuntimeCount));
        OnPropertyChanged(nameof(HasMissingRuntimes));
        OnPropertyChanged(nameof(InstallAllMissingText));
    }

    private async Task InstallRuntimeAsync(RuntimeComponentItem? item)
    {
        if (item is null || IsBusy) return;

        var actionName = item.IsInstalled ? "重新安装 / 修复" : "安装";
        var sourceDesc = item.InstallId.StartsWith("DISM:", StringComparison.OrdinalIgnoreCase)
            ? $"系统 DISM 组件启用 ({item.InstallId})"
            : string.IsNullOrWhiteSpace(item.InstallId)
                ? "系统内置组件"
                : $"winget 官方源 ({item.InstallId})";

        var confirm = MessageBox.Show(
            $"即将{actionName}「{item.Name} ({item.Architecture})」：\n\n" +
            $"安装方式：{sourceDesc}\n" +
            $"组件说明：{item.Description}\n\n" +
            "确定继续吗？",
            $"{actionName}运行库", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _runtimeService.InstallRuntimeAsync(item, s => _status(s));
            _status(msg);
            RefreshRuntimes();

            if (!ok)
            {
                SetError(msg);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAllMissingRuntimesAsync()
    {
        var missing = _allRuntimes.Where(r => !r.IsInstalled).ToList();
        if (missing.Count == 0 || IsBusy) return;

        var listPreview = string.Join("\n• ", missing.Take(10).Select(m => $"{m.Name} ({m.Architecture})"));
        if (missing.Count > 10) listPreview += $"\n…等共 {missing.Count} 项";

        var confirm = MessageBox.Show(
            $"将依次通过官方源安装本机缺失的 {missing.Count} 项运行库：\n\n• {listPreview}\n\n" +
            "确定开始一键补齐吗？",
            "一键补齐缺失运行库", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        var okCount = 0;
        var failed = new List<string>();

        try
        {
            for (var i = 0; i < missing.Count; i++)
            {
                var item = missing[i];
                var prefix = $"[{i + 1}/{missing.Count}] ";
                var (ok, msg) = await _runtimeService.InstallRuntimeAsync(item, s => _status(prefix + s));
                if (ok) okCount++;
                else failed.Add($"{item.Name}：{msg}");
            }

            RefreshRuntimes();
            _status($"运行库批量补齐完成：成功 {okCount} 项，失败 {failed.Count} 项");

            if (failed.Count > 0)
            {
                SetError($"已成功安装 {okCount} 项运行库，以下 {failed.Count} 项未完成：\n• " + string.Join("\n• ", failed));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshTools()
    {
        try
        {
            LoadToolsInternal();
            var installed = _allTools.Count(t => t.IsInstalled);
            _status($"系统工具库检测完成：收录 {_allTools.Count} 款，已安装 {installed} 款，未安装 {_allTools.Count - installed} 款");
        }
        catch (Exception ex)
        {
            SetError($"检测系统工具库失败：{ex.Message}");
        }
    }

    private void LoadToolsInternal()
    {
        _allTools = _toolService.LoadCatalog();
        foreach (var tool in _allTools)
        {
            tool.SelectionChanged = OnToolSelectionChanged;
        }
        ApplyToolFilter();
    }

    private void OnToolSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedToolsCount));
        OnPropertyChanged(nameof(HasSelectedTools));
        OnPropertyChanged(nameof(InstallSelectedToolsText));
    }

    private void ApplyToolFilter()
    {
        Tools.Clear();
        var kw = _toolSearchText.Trim().ToLowerInvariant();

        var query = _allTools
            .Where(t => !_onlyUninstalledTools || !t.IsInstalled)
            .Where(t => kw.Length == 0 || t.SearchBlob.Contains(kw));

        foreach (var item in query) Tools.Add(item);

        var installedCount = _allTools.Count(t => t.IsInstalled);
        ToolSummaryText = $"共收录 {_allTools.Count} 款纯净系统与装机工具 · 已安装 {installedCount} 款 · 未安装 {_allTools.Count - installedCount} 款 · 当前显示 {Tools.Count} 款";
        OnToolSelectionChanged();
    }

    private void SelectUninstalledTools()
    {
        var uninstalled = Tools.Where(t => !t.IsInstalled).ToList();
        var allSelected = uninstalled.Count > 0 && uninstalled.All(t => t.IsSelected);
        foreach (var t in uninstalled)
            t.IsSelected = !allSelected;
        OnToolSelectionChanged();
    }

    private void LaunchTool(SystemToolItem? tool)
    {
        if (tool is null) return;
        var (ok, msg) = _toolService.LaunchTool(tool);
        _status(msg);
        if (!ok)
        {
            SetError(msg);
        }
    }

    private void OpenToolLocation(SystemToolItem? tool)
    {
        if (tool is null || string.IsNullOrWhiteSpace(tool.InstallLocation) || !Directory.Exists(tool.InstallLocation))
            return;

        try
        {
            Native.OpenPath(tool.InstallLocation);
        }
        catch (Exception ex)
        {
            SetError($"打开目录失败：{ex.Message}");
        }
    }

    private async Task InstallToolAsync(SystemToolItem? tool)
    {
        if (tool is null || IsBusy) return;

        var actionName = tool.IsInstalled ? "更新 / 重装" : "安装";
        var confirm = MessageBox.Show(
            $"即将通过系统 winget 官方源{actionName}「{tool.Name}」：\n\n" +
            $"Winget ID：{tool.WingetId}\n" +
            $"分类：{tool.Category}\n" +
            $"简介：{tool.Description}\n\n" +
            "确定继续吗？",
            $"{actionName}系统工具", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _toolService.InstallOrUpdateToolAsync(tool, s => _status(s));
            _status(msg);
            RefreshTools();
            if (!ok)
            {
                SetError(msg);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallSelectedToolsAsync()
    {
        var targets = _allTools.Where(t => t.IsSelected).ToList();
        if (targets.Count == 0 || IsBusy) return;

        var preview = string.Join("\n• ", targets.Take(10).Select(t => $"{t.Name} ({t.WingetId})"));
        if (targets.Count > 10) preview += $"\n…等共 {targets.Count} 款工具";

        var confirm = MessageBox.Show(
            $"将依次通过系统 winget 官方源安装/更新以下 {targets.Count} 款工具：\n\n• {preview}\n\n确定开始批量安装吗？",
            "批量安装系统工具", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        var okCount = 0;
        var failed = new List<string>();

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                var prefix = $"[{i + 1}/{targets.Count}] ";
                var (ok, msg) = await _toolService.InstallOrUpdateToolAsync(item, s => _status(prefix + s));
                if (ok) okCount++;
                else failed.Add($"{item.Name}：{msg}");
            }

            RefreshTools();
            _status($"批量安装系统工具完成：成功 {okCount} 款，失败 {failed.Count} 款");

            if (failed.Count > 0)
            {
                SetError($"已成功安装 {okCount} 款工具，以下 {failed.Count} 款未完成：\n• " + string.Join("\n• ", failed));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string _errorText = "";

    /// <summary>页面级错误：失败时统一由 ErrorBar 展示，不再只靠底部状态栏一行灰字。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public void SetError(string? message) => ErrorText = message ?? "";}
