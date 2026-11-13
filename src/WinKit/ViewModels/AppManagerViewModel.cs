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
    private readonly UninstallService _uninstall = new();
    private readonly Action<string> _status;

    private List<InstalledAppItem> _all = new();
    private string _searchText = "";
    private string _sourceFilter = "all"; // all | desktop | store
    private bool _onlyUpdatable;
    private bool _isBusy;
    private string _summaryText = "";
    private string _hintText = "汇总本机已安装的传统桌面软件与 Microsoft Store / MSIX 打包应用。点「检查更新」将通过系统内置 winget 对比最新版本；卸载后自动扫描注册表残留（支持一键快照清理）与残留目录。";

    private DispatcherTimer? _uninstallTimer;
    private DateTime _uninstallDeadline;
    private InstalledAppItem? _lastUninstalled;

    public AppManagerViewModel(ChangeJournal journal, Action<string> status)
    {
        _service = new AppManagerService(journal);
        _status = status;

        Apps = new ObservableCollection<InstalledAppItem>();

        RefreshCommand = new RelayCommand(ManualRefresh);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync());
        UpgradeAppCommand = new RelayCommand(p => _ = UpgradeAppAsync(p as InstalledAppItem));
        UpgradeAllCommand = new RelayCommand(() => _ = UpgradeAllAsync());
        UninstallAppCommand = new RelayCommand(p => _ = UninstallAppAsync(p as InstalledAppItem));
        OpenLocationCommand = new RelayCommand(p => OpenLocation(p as InstalledAppItem));
        CopyPathCommand = new RelayCommand(p => CopyPath(p as InstalledAppItem));
        RecheckResidualCommand = new RelayCommand(RecheckResidual);
        ScanResidualCommand = new RelayCommand(p => ScanItemResidual(p as InstalledAppItem));

        FilterAllCommand = new RelayCommand(() => SourceFilter = "all");
        FilterDesktopCommand = new RelayCommand(() => SourceFilter = "desktop");
        FilterStoreCommand = new RelayCommand(() => SourceFilter = "store");
    }

    public ObservableCollection<InstalledAppItem> Apps { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand UpgradeAppCommand { get; }
    public RelayCommand UpgradeAllCommand { get; }
    public RelayCommand UninstallAppCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand RecheckResidualCommand { get; }
    public RelayCommand ScanResidualCommand { get; }

    public RelayCommand FilterAllCommand { get; }
    public RelayCommand FilterDesktopCommand { get; }
    public RelayCommand FilterStoreCommand { get; }

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
            ApplyFilter();
        }
    }

    public bool IsAllSelected => _sourceFilter == "all";
    public bool IsDesktopSelected => _sourceFilter == "desktop";
    public bool IsStoreSelected => _sourceFilter == "store";

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
        private set => Set(ref _isBusy, value);
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

    private void ManualRefresh()
    {
        if (_lastUninstalled is not null)
        {
            var pending = _lastUninstalled;
            StopWatch();
            HandleResidualCheck(pending);
            return;
        }

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
            _status($"已加载 {_all.Count} 个应用");
        }
        catch (Exception ex)
        {
            _status($"加载应用列表失败：{ex.Message}");
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
                _ => true
            })
            .Where(a => !_onlyUpdatable || a.HasUpdate)
            .Where(a => kw.Length == 0 || a.SearchBlob.Contains(kw))
            .OrderByDescending(a => a.HasUpdate)
            .ThenBy(a => a.Kind == AppSourceKind.Desktop ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture);

        foreach (var item in query) Apps.Add(item);

        var desktopCount = _all.Count(a => a.Kind == AppSourceKind.Desktop);
        var storeCount = _all.Count(a => a.Kind == AppSourceKind.Store);
        SummaryText = $"共 {_all.Count} 个应用（桌面 {desktopCount} · 商店/打包 {storeCount}）· 可更新 {UpdatableCount} 个 · 当前显示 {Apps.Count} 个";

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
                MessageBox.Show($"调用 winget 检查更新失败：\n{error}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
            "更新应用", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _service.UpgradeAppAsync(app, s => _status(s));
            _status(msg);

            if (ok)
            {
                app.Version = app.LatestVersion;
                app.LatestVersion = "";
                Refresh();
            }
            else
            {
                MessageBox.Show(msg, "更新结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
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
            "一键更新全部", MessageBoxButton.OKCancel, MessageBoxImage.Information);

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
                MessageBox.Show(
                    $"已完成 {succeeded} 个应用更新，以下 {failed.Count} 个未完成：\n\n• " + string.Join("\n• ", failed),
                    "批量更新结果", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (app is null || IsBusy) return;

        if (app.Kind == AppSourceKind.Store)
        {
            var confirmStore = MessageBox.Show(
                $"将卸载商店/打包应用「{app.Name}」：\n\n包名：{app.PackageFullName}\n\n确定继续吗？",
                "卸载打包应用", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (confirmStore != MessageBoxResult.OK) return;

            IsBusy = true;
            try
            {
                _status($"正在卸载「{app.Name}」…");
                var (ok, msg) = await _service.UninstallStoreAppAsync(app);
                _status(msg);
                if (!ok)
                {
                    MessageBox.Show(msg, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

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
            MessageBox.Show($"无法启动卸载程序：{ex.Message}\n\n你也可以点击右键菜单「扫描并清理残留」直接清理失效的卸载记录。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            HandleResidualCheck(app);
        }
    }

    private void StartUninstallWatch(InstalledAppItem app)
    {
        StopWatch();
        _lastUninstalled = app;
        OnPropertyChanged(nameof(CanRecheckResidual));
        OnPropertyChanged(nameof(RecheckResidualText));
        _uninstallDeadline = DateTime.UtcNow.AddMinutes(10);

        _uninstallTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _uninstallTimer.Tick += (_, _) =>
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
                _status("等待卸载超时：可随时点顶部「复查残留」手动检查。");
            }
        };
        _uninstallTimer.Start();
        _status($"正在等待「{app.Name}」卸载完成（也可随时点顶部「复查残留」）…");
    }

    private void StopWatch()
    {
        if (_uninstallTimer is null) return;
        _uninstallTimer.Stop();
        _uninstallTimer = null;
    }

    private void RecheckResidual()
    {
        if (_lastUninstalled is null) return;
        var pending = _lastUninstalled;
        StopWatch();
        HandleResidualCheck(pending);
    }

    private void ScanItemResidual(InstalledAppItem? app)
    {
        if (app is null) return;
        HandleResidualCheck(app);
    }

    private void HandleResidualCheck(InstalledAppItem app)
    {
        var report = _service.ScanResiduals(app);

        if (!report.HasAny)
        {
            _lastUninstalled = null;
            OnPropertyChanged(nameof(CanRecheckResidual));
            OnPropertyChanged(nameof(RecheckResidualText));
            _status($"「{app.Name}」未发现明显残留");
            MessageBox.Show($"「{app.Name}」未发现失效注册表残留或非空安装目录。",
                "残留复查", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
            return;
        }

        if (report.RegistryResiduals.Count > 0)
        {
            var preview = string.Join("\n• ", report.RegistryResiduals.Take(6).Select(r => r.Description));
            if (report.RegistryResiduals.Count > 6)
                preview += $"\n…等共 {report.RegistryResiduals.Count} 处注册表残留";

            var askClean = MessageBox.Show(
                $"发现「{app.Name}」共有 {report.RegistryResiduals.Count} 处注册表残留：\n\n• {preview}\n\n" +
                "是否立即一键清理这些注册表残留？（清理前自动存快照，可随时撤销）",
                "发现卸载注册表残留", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (askClean == MessageBoxResult.Yes)
            {
                var cleaned = _service.CleanRegistryResiduals(report);
                _status($"已清理「{app.Name}」的 {cleaned} 处注册表残留");
            }
        }

        if (report.FolderStillExists && !string.IsNullOrWhiteSpace(report.InstallLocation))
        {
            var askOpen = MessageBox.Show(
                $"原安装目录仍然存在且包含残留文件：\n{report.InstallLocation}\n\n" +
                "为防止误删个人数据，WinKit 不直接代删文件夹。是否立即在资源管理器中打开该目录？",
                "发现残留安装目录", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (askOpen == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = report.InstallLocation,
                        UseShellExecute = true
                    });
                }
                catch (Exception) { }
            }
        }

        _lastUninstalled = null;
        OnPropertyChanged(nameof(CanRecheckResidual));
        OnPropertyChanged(nameof(RecheckResidualText));
        Refresh();
    }

    private void OpenLocation(InstalledAppItem? app)
    {
        if (app is null || string.IsNullOrWhiteSpace(app.InstallLocation)) return;
        if (!Directory.Exists(app.InstallLocation))
        {
            MessageBox.Show($"目录不存在：\n{app.InstallLocation}", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app.InstallLocation,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
}
