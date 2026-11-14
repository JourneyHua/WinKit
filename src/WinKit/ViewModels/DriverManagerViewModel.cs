using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;
using WinKit.Views;

namespace WinKit.ViewModels;

public sealed class DriverClassNode : ObservableObject
{
    private int _count;

    public string Title { get; init; } = "";
    public bool IsAll { get; init; }

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}

public sealed class DriverManagerViewModel : ObservableObject, IModule
{
    private readonly DriverManagerService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private List<DriverPackageItem> _all = new();
    private DriverClassNode? _selectedClass;
    private string _searchText = "";
    private string _levelFilter = "all"; // all | critical | peripheral
    private string _summaryText = "";
    private bool _isBusy;

    public DriverManagerViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Classes = new ObservableCollection<DriverClassNode>();
        Drivers = new ObservableCollection<DriverPackageItem>();
        WindowsUpdates = new ObservableCollection<WindowsDriverUpdateItem>();

        RefreshCommand = new RelayCommand(Refresh);
        CheckWindowsUpdatesCommand = new RelayCommand(() => Run(CheckWindowsUpdatesAsync));
        InstallWindowsUpdateCommand = new RelayCommand(p => Run(() => InstallWindowsUpdateAsync(p as WindowsDriverUpdateItem)));
        InstallAllWindowsUpdatesCommand = new RelayCommand(() => Run(InstallAllWindowsUpdatesAsync));
        InstallLocalInfCommand = new RelayCommand(() => Run(InstallLocalInfAsync));
        RestoreFromBackupCommand = new RelayCommand(() => Run(RestoreFromBackupAsync));
        ScanHardwareCommand = new RelayCommand(() => Run(ScanHardwareAsync));
        ExportDriverCommand = new RelayCommand(p => Run(() => ExportDriverAsync(p as DriverPackageItem)));
        UninstallDriverCommand = new RelayCommand(p => Run(() => UninstallDriverAsync(p as DriverPackageItem)));
        CopyDriverInfoCommand = new RelayCommand(p => CopyDriverInfo(p as DriverPackageItem));
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        OpenDeviceManagerCommand = new RelayCommand(OpenDeviceManager);

        FilterLevelAllCommand = new RelayCommand(() => LevelFilter = "all");
        FilterLevelCriticalCommand = new RelayCommand(() => LevelFilter = "critical");
        FilterLevelPeripheralCommand = new RelayCommand(() => LevelFilter = "peripheral");
    }

    public ObservableCollection<DriverClassNode> Classes { get; }
    public ObservableCollection<DriverPackageItem> Drivers { get; }
    public ObservableCollection<WindowsDriverUpdateItem> WindowsUpdates { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CheckWindowsUpdatesCommand { get; }
    public RelayCommand InstallWindowsUpdateCommand { get; }
    public RelayCommand InstallAllWindowsUpdatesCommand { get; }
    public RelayCommand InstallLocalInfCommand { get; }
    public RelayCommand RestoreFromBackupCommand { get; }
    public RelayCommand ScanHardwareCommand { get; }
    public RelayCommand ExportDriverCommand { get; }
    public RelayCommand UninstallDriverCommand { get; }
    public RelayCommand CopyDriverInfoCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand OpenDeviceManagerCommand { get; }

    public RelayCommand FilterLevelAllCommand { get; }
    public RelayCommand FilterLevelCriticalCommand { get; }
    public RelayCommand FilterLevelPeripheralCommand { get; }

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (!Set(ref _levelFilter, value)) return;
            OnPropertyChanged(nameof(IsLevelAll));
            OnPropertyChanged(nameof(IsLevelCritical));
            OnPropertyChanged(nameof(IsLevelPeripheral));
            ApplyFilter();
        }
    }

    public bool IsLevelAll => _levelFilter == "all";
    public bool IsLevelCritical => _levelFilter == "critical";
    public bool IsLevelPeripheral => _levelFilter == "peripheral";

    public DriverClassNode? SelectedClass
    {
        get => _selectedClass;
        set
        {
            if (Set(ref _selectedClass, value)) ApplyFilter();
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

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                _setBusy?.Invoke(value);
            }
        }
    }

    public bool HasWindowsUpdates => WindowsUpdates.Count > 0;
    public string InstallAllUpdatesText => $"一键安装官方更新（{WindowsUpdates.Count}）";

    /// <summary>
    /// 统一收口异步命令：命令用 `_ = XxxAsync()` 发起时，若方法内部只 try/finally 没有 catch，
    /// 异常会被丢弃的 Task 吞掉，界面永久停在“进行中”且用户看不到任何错误。
    /// </summary>
    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _status($"操作失败：{ex.Message}");
            MessageBox.Show($"操作失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private int _backupCount;
    public int BackupCount
    {
        get => _backupCount;
        private set
        {
            if (Set(ref _backupCount, value))
            {
                OnPropertyChanged(nameof(HasBackups));
                OnPropertyChanged(nameof(RestoreBackupText));
            }
        }
    }

    public bool HasBackups => BackupCount > 0;
    public string RestoreBackupText => $"从备份还原（{BackupCount}）";

    public void Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在枚举第三方驱动包（pnputil）…");
            var (drivers, error) = _service.LoadDrivers();
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status($"读取驱动失败：{error}");
                return;
            }

            _all = drivers;
            BackupCount = _service.ListBackups().Count;
            BuildClassNodes();
            ApplyFilter();
            _status($"已加载 {_all.Count} 个第三方驱动包");
        }
        catch (Exception ex)
        {
            _status($"读取驱动异常：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    private void BuildClassNodes()
    {
        var prevTitle = _selectedClass?.Title;
        Classes.Clear();

        Classes.Add(new DriverClassNode
        {
            Title = "全部类别",
            IsAll = true,
            Count = _all.Count
        });

        var grouped = _all
            .GroupBy(d => d.ClassTitle)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCulture);

        foreach (var group in grouped)
        {
            Classes.Add(new DriverClassNode
            {
                Title = group.Key,
                IsAll = false,
                Count = group.Count()
            });
        }

        _selectedClass = Classes.FirstOrDefault(c => c.Title == prevTitle) ?? Classes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedClass));
    }

    private void ApplyFilter()
    {
        Drivers.Clear();
        var kw = _searchText.Trim().ToLowerInvariant();
        var cls = _selectedClass;

        var query = _all
            .Where(d => cls is null || cls.IsAll || d.ClassTitle == cls.Title)
            .Where(d => _levelFilter switch
            {
                "critical" => d.IsCritical,
                "peripheral" => !d.IsCritical,
                _ => true
            })
            .Where(d => kw.Length == 0 || d.SearchBlob.Contains(kw))
            .OrderBy(d => d.ClassTitle, StringComparer.CurrentCulture)
            .ThenBy(d => d.Provider, StringComparer.CurrentCulture)
            .ThenBy(d => d.OriginalName, StringComparer.CurrentCulture);

        foreach (var item in query) Drivers.Add(item);

        var criticalCount = _all.Count(d => d.IsCritical);
        SummaryText = $"第三方驱动包共 {_all.Count} 个（核心驱动 {criticalCount} 个）· 已备份 {BackupCount} 份 · 当前显示 {Drivers.Count} 个";
    }

    private async Task ExportDriverAsync(DriverPackageItem? driver)
    {
        if (driver is null || IsBusy) return;
        IsBusy = true;

        try
        {
            _status($"正在导出备份驱动包：{driver.DisplayTitle}…");
            var (ok, folder, err) = await _service.BackupDriverAsync(driver);
            BackupCount = _service.ListBackups().Count;
            ApplyFilter();

            if (ok)
            {
                _status($"已备份驱动至：{folder}");
                MessageBox.Show($"驱动包「{driver.DisplayTitle}」已完整备份到：\n\n{folder}\n\n可随时通过顶部「从备份还原」重新安装。",
                    "备份成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _status("备份驱动失败");
                MessageBox.Show($"导出驱动备份失败：\n{err}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallDriverAsync(DriverPackageItem? driver)
    {
        if (driver is null || IsBusy) return;

        var riskWarning = driver.IsCritical
            ? $"【高风险警告】「{driver.ClassTitle}」属于系统核心硬件驱动（如显卡、网卡、存储或主板芯片组），卸载可能导致黑屏、断网或外设失灵！\n\n"
            : "";

        var confirm = MessageBox.Show(
            $"{riskWarning}将卸载第三方驱动包：\n" +
            $"名称：{driver.DisplayTitle}\n" +
            $"类别：{driver.ClassTitle}\n" +
            $"厂商：{driver.Provider}（版本 {driver.Version}）\n\n" +
            "卸载前会自动调用 pnputil 将该驱动包完整导出备份至本地，随时可一键还原。确定继续吗？",
            driver.IsCritical ? "确认卸载核心驱动" : "确认卸载驱动包",
            MessageBoxButton.OKCancel,
            driver.IsCritical ? MessageBoxImage.Error : MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            _status($"正在备份驱动包：{driver.DisplayTitle}…");
            var (backupOk, backupFolder, backupErr) = await _service.BackupDriverAsync(driver);
            if (!backupOk)
            {
                var proceedWithoutBackup = MessageBox.Show(
                    $"自动导出驱动备份失败（{backupErr}）。\n\n为安全起见建议取消操作；是否仍要在无备份的情况下继续卸载？",
                    "备份未成功", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceedWithoutBackup != MessageBoxResult.Yes) return;
            }
            else
            {
                BackupCount = _service.ListBackups().Count;
                _status($"已备份至 {backupFolder}，正在卸载 {driver.PublishedName}…");
            }

            var (ok, inUse, msg) = await _service.UninstallDriverAsync(driver, force: false);
            if (!ok && inUse)
            {
                var askForce = MessageBox.Show(
                    $"该驱动当前正被设备使用：\n\n{msg}\n\n是否强制卸载（/force）？强制卸载后对应设备可能立即停止工作或回退为系统基础驱动。",
                    "驱动正在使用中", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (askForce == MessageBoxResult.Yes)
                {
                    _status($"正在强制卸载 {driver.PublishedName}…");
                    (ok, _, msg) = await _service.UninstallDriverAsync(driver, force: true);
                }
                else
                {
                    _status("已取消强制卸载（驱动备份已保留）");
                    return;
                }
            }

            if (ok)
            {
                _status($"已卸载：{driver.DisplayTitle}");
                IsBusy = false;
                await LoadAsync();
            }
            else
            {
                _status("卸载驱动未完成");
                MessageBox.Show($"卸载未完成：\n\n{msg}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckWindowsUpdatesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在通过 Windows Update 检索微软官方 WHQL 驱动更新（可能需要 10~30 秒）…");
            var (updates, err) = await _service.SearchWindowsDriverUpdatesAsync();

            WindowsUpdates.Clear();
            foreach (var u in updates) WindowsUpdates.Add(u);
            OnPropertyChanged(nameof(HasWindowsUpdates));
            OnPropertyChanged(nameof(InstallAllUpdatesText));

            if (!string.IsNullOrWhiteSpace(err))
            {
                _status(err);
                MessageBox.Show(err, "在线驱动检测", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (WindowsUpdates.Count == 0)
            {
                _status("未发现待安装的 Windows Update 官方驱动更新");
                MessageBox.Show("当前硬件在 Windows Update 官方通道下均已是最新驱动。",
                    "驱动已是最新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _status($"发现 {WindowsUpdates.Count} 个微软官方 WHQL 驱动更新");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallWindowsUpdateAsync(WindowsDriverUpdateItem? item)
    {
        if (item is null || IsBusy) return;
        await RunInstallWindowsUpdatesAsync(new[] { item });
    }

    private async Task InstallAllWindowsUpdatesAsync()
    {
        if (WindowsUpdates.Count == 0 || IsBusy) return;
        await RunInstallWindowsUpdatesAsync(WindowsUpdates.ToList());
    }

    private async Task RunInstallWindowsUpdatesAsync(IReadOnlyList<WindowsDriverUpdateItem> items)
    {
        var names = string.Join("\n• ", items.Take(8).Select(i => i.Title));
        if (items.Count > 8) names += $"\n…等共 {items.Count} 个官方驱动更新";

        var confirm = MessageBox.Show(
            $"将从微软 Windows Update 官方通道下载并安装以下驱动：\n\n• {names}\n\n确定继续吗？",
            "安装官方驱动更新", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _service.InstallWindowsDriverUpdatesAsync(items, s => _status(s));
            _status(msg);
            MessageBox.Show(msg, ok ? "安装完成" : "安装未完成",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok)
            {
                foreach (var installed in items)
                    WindowsUpdates.Remove(installed);
                OnPropertyChanged(nameof(HasWindowsUpdates));
                OnPropertyChanged(nameof(InstallAllUpdatesText));
                IsBusy = false;
                await LoadAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallLocalInfAsync()
    {
        if (IsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装或更新的本地驱动包 (.inf)",
            Filter = "驱动程序信息文件 (*.inf)|*.inf",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        try
        {
            _status($"正在安装本地驱动包：{Path.GetFileName(dialog.FileName)}…");
            var (ok, msg) = await _service.InstallInfAsync(dialog.FileName);
            _status(ok ? $"已安装驱动：{Path.GetFileName(dialog.FileName)}" : "驱动安装失败");
            MessageBox.Show(msg, ok ? "驱动安装完成" : "驱动安装失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok)
            {
                IsBusy = false;
                await LoadAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreFromBackupAsync()
    {
        if (IsBusy) return;

        var backups = _service.ListBackups();
        BackupCount = backups.Count;
        if (backups.Count == 0)
        {
            MessageBox.Show("还没有已备份的驱动包。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var labels = backups.Select(b => $"{b.TimeText}    {b.Summary}").ToList();
        var picked = ChoiceDialog.Pick(
            Application.Current.MainWindow,
            "从备份还原驱动",
            $"备份目录：{DriverManagerService.DriverBackupRoot}\n请选择要重新安装的驱动包备份：",
            labels);

        if (picked is null) return;
        var chosen = backups[labels.IndexOf(picked)];

        IsBusy = true;
        try
        {
            _status($"正在从备份重装驱动：{chosen.Summary}…");
            var (ok, msg) = await _service.InstallInfAsync(chosen.FolderPath);
            _status(ok ? $"已还原驱动：{chosen.OriginalName}" : "驱动还原失败");
            MessageBox.Show(msg, ok ? "还原完成" : "还原失败",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (ok)
            {
                IsBusy = false;
                await LoadAsync();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanHardwareAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在扫描硬件改动（pnputil /scan-devices）…");
            var (_, msg) = await _service.ScanHardwareChangesAsync();
            _status(msg);
            IsBusy = false;
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyDriverInfo(DriverPackageItem? item)
    {
        if (item is null) return;
        var text = $"{item.DisplayTitle} | {item.ClassTitle} | {item.Provider} | {item.VersionAndDate}";
        try
        {
            Clipboard.SetText(text);
            _status($"已复制驱动信息：{item.DisplayTitle}");
        }
        catch (Exception) { }
    }

    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(DriverManagerService.DriverBackupRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = DriverManagerService.DriverBackupRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开备份目录失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "devmgmt.msc",
                UseShellExecute = true
            });
            _status("已打开系统设备管理器 (devmgmt.msc)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开设备管理器失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
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
