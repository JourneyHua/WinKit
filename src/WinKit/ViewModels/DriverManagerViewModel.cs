using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    private string _operationText = "";
    private bool _isBusy;

    public DriverManagerViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Classes = new ObservableCollection<DriverClassNode>();
        Drivers = new ObservableCollection<DriverPackageItem>();
        WindowsUpdates = new ObservableCollection<WindowsDriverUpdateItem>();

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        CheckWindowsUpdatesCommand = new RelayCommand(() => Run(CheckWindowsUpdatesAsync), () => !IsBusy);
        InstallWindowsUpdateCommand = new RelayCommand(p => Run(() => InstallWindowsUpdatesAsync([(WindowsDriverUpdateItem)p!])),
            p => !IsBusy && p is WindowsDriverUpdateItem update && WindowsUpdates.Contains(update));
        InstallAllWindowsUpdatesCommand = new RelayCommand(() => Run(() => InstallWindowsUpdatesAsync(WindowsUpdates.ToList())),
            () => !IsBusy && HasWindowsUpdates);
        InstallLocalInfCommand = new RelayCommand(() => Run(InstallLocalInfAsync), () => !IsBusy);
        RestoreFromBackupCommand = new RelayCommand(() => Run(RestoreFromBackupAsync), () => !IsBusy && HasBackups);
        ScanHardwareCommand = new RelayCommand(() => Run(ScanHardwareAsync), () => !IsBusy);
        ExportDriverCommand = new RelayCommand(p => Run(() => ExportDriverAsync((DriverPackageItem)p!)), CanOperateOnDriver);
        UninstallDriverCommand = new RelayCommand(p => Run(() => UninstallDriverAsync((DriverPackageItem)p!)), CanOperateOnDriver);
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
            if (!Set(ref _isBusy, value)) return;
            _setBusy?.Invoke(value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string OperationText
    {
        get => _operationText;
        private set
        {
            if (Set(ref _operationText, value)) OnPropertyChanged(nameof(HasOperationText));
        }
    }

    public bool HasOperationText => OperationText.Length > 0;
    public bool HasWindowsUpdates => WindowsUpdates.Count > 0;
    public string InstallAllUpdatesText => $"安装全部更新（{WindowsUpdates.Count}）…";

    private bool _windowsUpdateRebootRequired;
    public bool WindowsUpdateRebootRequired
    {
        get => _windowsUpdateRebootRequired;
        private set => Set(ref _windowsUpdateRebootRequired, value);
    }

    private bool CanOperateOnDriver(object? item) => !IsBusy && item is DriverPackageItem driver && _all.Contains(driver);

    private void Report(string message)
    {
        OperationText = message;
        _status(message);
    }

    // 只有获取了忙碌状态的调用才能释放它。随后的自动刷新也在同一次操作里。
    private async void Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        SetError(null);
        OperationText = "";
        try { await action(); }
        catch (Exception ex)
        {
            OperationText = "";
            SetError($"操作未完成：{ex.Message}");
        }
        finally { IsBusy = false; }
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
    public string RestoreBackupText => $"从备份重装（{BackupCount}）…";

    public void Refresh() => Run(async () =>
    {
        Report("正在读取第三方驱动包…");
        if (await LoadAsync()) Report($"已加载 {_all.Count} 个第三方驱动包。");
        else Report("未能更新驱动列表。");
    });

    private async Task<bool> LoadAsync()
    {
        try
        {
            var (drivers, error) = await Task.Run(_service.LoadDrivers);
            if (!string.IsNullOrWhiteSpace(error))
            {
                InvalidateDrivers();
                SetError($"未能更新驱动列表：{error}");
                return false;
            }

            _all = drivers;
            BuildClassNodes();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            InvalidateDrivers();
            SetError($"未能更新驱动列表：{ex.Message}");
            return false;
        }

        await RefreshBackupCountAsync();
        return true;
    }

    private void InvalidateDrivers()
    {
        _all.Clear();
        BuildClassNodes();
        ApplyFilter();
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

    private async Task ExportDriverAsync(DriverPackageItem driver)
    {
        if (Native.RequireAdmin("备份驱动") is { } adminNeeded) { SetError(adminNeeded); return; }
        Report($"正在备份驱动包：{driver.DisplayTitle}…");
        var (ok, folder, error) = await _service.BackupDriverAsync(driver);
        if (!ok)
        {
            Report("驱动备份未完成。");
            SetError($"驱动备份失败：{error}");
            return;
        }
        Report($"已备份「{driver.DisplayTitle}」。可在「从备份重装」中选择此备份。位置：{folder}");
        await RefreshBackupCountAsync();
    }

    private async Task UninstallDriverAsync(DriverPackageItem driver)
    {
        if (Native.RequireAdmin("卸载驱动") is { } adminNeeded) { SetError(adminNeeded); return; }

        var riskWarning = driver.IsCritical
            ? $"这是「{driver.ClassTitle}」核心驱动，卸载可能导致黑屏、断网或设备无法使用。\n\n"
            : "使用此驱动的设备可能暂时无法使用。\n\n";
        if (MessageBox.Show(
            $"{riskWarning}将卸载驱动包：\n{driver.DisplayTitle}\n厂商：{driver.Provider}（版本 {driver.Version}）\n\n" +
            "WinKit 会先备份驱动包。备份失败时不会卸载。继续吗？",
            driver.IsCritical ? "确认卸载核心驱动" : "确认卸载驱动包",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        Report($"正在备份驱动包：{driver.DisplayTitle}…");
        var (backupOk, _, backupError) = await _service.BackupDriverAsync(driver);
        if (!backupOk)
        {
            Report("未卸载驱动。");
            SetError($"备份未完成，未卸载驱动：{backupError}");
            return;
        }

        Report($"备份已保存，正在卸载 {driver.DisplayTitle}…");
        var (ok, inUse, message) = await _service.UninstallDriverAsync(driver);
        await LoadAsync();
        if (ok)
        {
            Report($"已卸载「{driver.DisplayTitle}」。可从备份重装驱动包。");
            return;
        }

        Report("卸载未完成，备份已保留。");
        SetError(inUse
            ? $"驱动仍在使用，WinKit 未尝试强制卸载。请在设备管理器中核对使用它的设备。\n{message}"
            : $"卸载未完成：{message}");
    }

    private async Task RefreshBackupCountAsync()
    {
        try
        {
            BackupCount = (await Task.Run(_service.ListBackups)).Count;
            ApplyFilter();
        }
        catch (Exception ex) { SetError($"未能读取驱动备份：{ex.Message}"); }
    }

    private async Task CheckWindowsUpdatesAsync()
    {
        Report("正在检查 Windows Update 提供的驱动更新…");
        var (updates, error) = await _service.SearchWindowsDriverUpdatesAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            Report("未能检查驱动更新。");
            SetError(error);
            return;
        }

        SetWindowsUpdates(updates);
        Report(updates.Count == 0
            ? "未找到 Windows Update 提供的驱动更新。"
            : $"Windows Update 提供 {updates.Count} 个驱动更新。");
    }

    private void SetWindowsUpdates(IEnumerable<WindowsDriverUpdateItem> items)
    {
        WindowsUpdates.Clear();
        foreach (var item in items) WindowsUpdates.Add(item);
        NotifyWindowsUpdatesChanged();
    }

    private void NotifyWindowsUpdatesChanged()
    {
        OnPropertyChanged(nameof(HasWindowsUpdates));
        OnPropertyChanged(nameof(InstallAllUpdatesText));
    }

    private async Task InstallWindowsUpdatesAsync(IReadOnlyList<WindowsDriverUpdateItem> items)
    {
        if (Native.RequireAdmin("安装驱动更新") is { } adminNeeded) { SetError(adminNeeded); return; }
        var names = string.Join("\n• ", items.Take(8).Select(i => i.Title));
        if (items.Count > 8) names += $"\n…共 {items.Count} 个驱动更新";

        if (MessageBox.Show(
            $"将通过 Windows Update 下载并安装以下驱动：\n\n• {names}\n\n安装期间设备可能暂时中断，安装后可能需要重启电脑。继续吗？",
            "安装驱动更新", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        Report("正在下载并安装驱动更新…");
        var result = await _service.InstallWindowsDriverUpdatesAsync(items, _status);
        if (result.RebootRequired) WindowsUpdateRebootRequired = true;

        var installed = result.Installed.ToHashSet();
        foreach (var item in items)
        {
            if (installed.Contains(item)) WindowsUpdates.Remove(item);
            else item.InstallIssue = result.Issues.GetValueOrDefault(item, "本次未确认安装成功。");
        }
        NotifyWindowsUpdatesChanged();

        // 安装未完全成功时，本机驱动包也可能已变化。
        await LoadAsync();
        if (result.IncompleteReason is { } reason)
        {
            SetError(HasError ? $"{reason}\n{ErrorText}" : reason);
            var remaining = items.Count - installed.Count;
            var retry = remaining > 0 ? $"另有 {remaining} 个未确认成功；可重试或重新检查更新。" : "可重新检查更新。";
            Report($"驱动更新未完全成功；确认安装 {installed.Count} 个。{retry}");
            return;
        }

        Report($"已安装 {installed.Count} 个 Windows Update 驱动更新。");
    }

    private async Task InstallLocalInfAsync()
    {
        if (Native.RequireAdmin("安装本地驱动包") is { } adminNeeded) { SetError(adminNeeded); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要安装的驱动包",
            Filter = "驱动程序信息文件 (*.inf)|*.inf",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        if (MessageBox.Show(
            $"将从此文件安装驱动：\n\n{dialog.FileName}\n\n请确认驱动来自你信任的发布者。安装期间设备可能暂时中断。继续吗？",
            "安装本地驱动", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await InstallDriverPackageAsync(dialog.FileName, Path.GetFileName(dialog.FileName));
    }

    private async Task RestoreFromBackupAsync()
    {
        if (Native.RequireAdmin("从备份重装驱动") is { } adminNeeded) { SetError(adminNeeded); return; }
        var backups = await Task.Run(_service.ListBackups);
        BackupCount = backups.Count;
        ApplyFilter();
        if (!HasBackups) { Report("没有可用的驱动备份。"); return; }

        var labels = backups.Select((b, i) => $"{i + 1}. {b.TimeText}    {b.Summary}").ToList();
        var picked = ChoiceDialog.Pick(Application.Current.MainWindow, "从备份重装驱动",
            "选择要重新安装的驱动包。这不会回退其它系统更改，Windows 会决定设备实际使用的驱动。", labels);
        if (picked is null) return;
        var chosen = backups[labels.IndexOf(picked)];
        await InstallDriverPackageAsync(chosen.FolderPath, chosen.OriginalName);
    }

    private async Task InstallDriverPackageAsync(string path, string title)
    {
        Report($"正在安装驱动包：{title}…");
        var (ok, message) = await _service.InstallInfAsync(path);
        await LoadAsync();
        if (ok) Report($"Windows 已处理驱动包「{title}」。可在设备管理器查看当前使用的驱动。");
        else
        {
            Report("驱动安装未完成。");
            SetError(message);
        }
    }

    private async Task ScanHardwareAsync()
    {
        if (Native.RequireAdmin("扫描硬件改动") is { } adminNeeded) { SetError(adminNeeded); return; }
        Report("正在扫描硬件改动…");
        var (ok, message) = await _service.ScanHardwareChangesAsync();
        if (ok) await LoadAsync();
        else SetError(message);
        Report(ok ? "硬件扫描已完成。" : "未能完成硬件扫描。");
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
            Native.OpenPath(DriverManagerService.DriverBackupRoot);
        }
        catch (Exception ex)
        {
            SetError($"打开备份目录失败：{ex.Message}");
        }
    }

    private void OpenDeviceManager()
    {
        try
        {
            Native.OpenPath("devmgmt.msc");
            _status("已打开系统设备管理器 (devmgmt.msc)");
        }
        catch (Exception ex)
        {
            SetError($"打开设备管理器失败：{ex.Message}");
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
