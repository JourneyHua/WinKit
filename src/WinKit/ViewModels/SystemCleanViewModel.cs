using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class ScopeOption
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
}

public sealed class SystemCleanViewModel : ObservableObject, IModule
{
    private readonly SystemCleanService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private List<LargeFileItem> _allLargeFiles = new();
    private string _subView = "junk"; // junk | large
    private string _largeSearchText = "";
    private ScopeOption? _selectedScope;
    private bool _isBusy;
    private string _junkSummaryText = "";
    private string _largeSummaryText = "";

    public SystemCleanViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Targets = new ObservableCollection<CleanupTargetItem>();
        LargeFiles = new ObservableCollection<LargeFileItem>();
        Scopes = new ObservableCollection<ScopeOption>
        {
            new() { Key = "user", Title = $"当前用户目录 ({Environment.UserName})" }
        };

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            Scopes.Add(new ScopeOption
            {
                Key = drive.RootDirectory.FullName,
                Title = $"{drive.Name.TrimEnd('\\')} 盘（可用 {ThisPcService.FormatSize(drive.AvailableFreeSpace)}）"
            });
        }

        _selectedScope = Scopes.FirstOrDefault();

        foreach (var t in _service.BuildTargets())
        {
            t.SelectionChanged = UpdateJunkSummary;
            Targets.Add(t);
        }

        ShowJunkViewCommand = new RelayCommand(() => SubView = "junk");
        ShowLargeViewCommand = new RelayCommand(() => SubView = "large");

        ScanJunkCommand = new RelayCommand(() => _ = ScanJunkAsync());
        CleanSelectedCommand = new RelayCommand(() => _ = CleanSelectedAsync());
        RunDismCleanCommand = new RelayCommand(() => _ = RunDismCleanAsync());
        OpenStorageSenseCommand = new RelayCommand(OpenStorageSense);
        OpenTargetFolderCommand = new RelayCommand(p => OpenTargetFolder(p as CleanupTargetItem));

        ScanLargeFilesCommand = new RelayCommand(() => _ = ScanLargeFilesAsync());
        OpenLargeFileFolderCommand = new RelayCommand(p => OpenLargeFileFolder(p as LargeFileItem));
        CopyLargeFilePathCommand = new RelayCommand(p => CopyLargeFilePath(p as LargeFileItem));
        RecycleLargeFileCommand = new RelayCommand(p => RecycleLargeFile(p as LargeFileItem));
    }

    public ObservableCollection<CleanupTargetItem> Targets { get; }
    public ObservableCollection<LargeFileItem> LargeFiles { get; }
    public ObservableCollection<ScopeOption> Scopes { get; }

    public RelayCommand ShowJunkViewCommand { get; }
    public RelayCommand ShowLargeViewCommand { get; }

    public RelayCommand ScanJunkCommand { get; }
    public RelayCommand CleanSelectedCommand { get; }
    public RelayCommand RunDismCleanCommand { get; }
    public RelayCommand OpenStorageSenseCommand { get; }
    public RelayCommand OpenTargetFolderCommand { get; }

    public RelayCommand ScanLargeFilesCommand { get; }
    public RelayCommand OpenLargeFileFolderCommand { get; }
    public RelayCommand CopyLargeFilePathCommand { get; }
    public RelayCommand RecycleLargeFileCommand { get; }

    public string SubView
    {
        get => _subView;
        set
        {
            if (!Set(ref _subView, value)) return;
            OnPropertyChanged(nameof(ShowJunkView));
            OnPropertyChanged(nameof(ShowLargeView));
            if (value == "large" && _allLargeFiles.Count == 0)
            {
                _ = ScanLargeFilesAsync();
            }
        }
    }

    public bool ShowJunkView => _subView == "junk";
    public bool ShowLargeView => _subView == "large";

    public ScopeOption? SelectedScope
    {
        get => _selectedScope;
        set => Set(ref _selectedScope, value);
    }

    public string LargeSearchText
    {
        get => _largeSearchText;
        set
        {
            if (Set(ref _largeSearchText, value)) ApplyLargeFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public string JunkSummaryText
    {
        get => _junkSummaryText;
        private set => Set(ref _junkSummaryText, value);
    }

    public string LargeSummaryText
    {
        get => _largeSummaryText;
        private set => Set(ref _largeSummaryText, value);
    }

    public long SelectedJunkBytes => Targets.Where(t => t.IsSelected).Sum(t => t.SizeBytes);
    public string CleanButtonText => SelectedJunkBytes > 0
        ? $"一键清理勾选项（预计释放 {ThisPcService.FormatSize(SelectedJunkBytes)}）"
        : "一键清理勾选项";

    public void Refresh()
    {
        _ = ScanJunkAsync();
    }

    private async Task ScanJunkAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在扫描临时文件、更新缓存与系统垃圾体积…");
            await _service.ScanTargetsAsync(Targets);
            UpdateJunkSummary();
            var total = Targets.Sum(t => t.SizeBytes);
            _status($"垃圾与缓存扫描完成：共发现 {ThisPcService.FormatSize(total)} 可清理空间");
        }
        catch (Exception ex)
        {
            _status($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateJunkSummary()
    {
        var totalBytes = Targets.Sum(t => t.SizeBytes);
        var selectedBytes = SelectedJunkBytes;
        var selectedCount = Targets.Count(t => t.IsSelected);
        JunkSummaryText = $"共扫描 {Targets.Count} 类缓存与临时目录 · 总计 {ThisPcService.FormatSize(totalBytes)} · 已勾选 {selectedCount} 类（{ThisPcService.FormatSize(selectedBytes)}）";
        OnPropertyChanged(nameof(SelectedJunkBytes));
        OnPropertyChanged(nameof(CleanButtonText));
    }

    private async Task CleanSelectedAsync()
    {
        var selected = Targets.Where(t => t.IsSelected && t.SizeBytes > 0).ToList();
        if (selected.Count == 0 || IsBusy)
        {
            MessageBox.Show("当前勾选项没有可清理的文件，请先扫描或勾选有体积的条目。", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var listText = string.Join("\n• ", selected.Select(s => $"{s.Title} ({s.SizeText})"));
        var confirm = MessageBox.Show(
            $"将清理以下 {selected.Count} 类缓存与临时文件：\n\n• {listText}\n\n" +
            "正在被系统或应用占用的临时文件会自动跳过，不会影响正在运行的程序。确定立即清理吗？",
            "确认清理", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (freed, count) = await _service.CleanTargetsAsync(selected, s => _status(s));
            await _service.ScanTargetsAsync(Targets);
            UpdateJunkSummary();

            var freedText = ThisPcService.FormatSize(freed);
            _status($"清理完成：共删除 {count} 个文件，释放 {freedText} 空间");
            MessageBox.Show($"清理完成！\n\n已清理文件数：{count} 个\n成功释放空间：{freedText}",
                "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDismCleanAsync()
    {
        if (IsBusy) return;

        var confirm = MessageBox.Show(
            "将调用系统内置命令：\ndism.exe /Online /Cleanup-Image /StartComponentCleanup\n\n" +
            "该操作会安全清理 Windows 更新后被取代的旧版 WinSxS 系统组件备份（通常耗时 1~5 分钟）。确定开始吗？",
            "DISM 组件存储清理", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, msg) = await _service.RunDismComponentCleanupAsync(s => _status(s));
            _status(msg);
            MessageBox.Show(msg, "DISM 组件清理", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenStorageSense()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "ms-settings:storagesense", UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private void OpenTargetFolder(CleanupTargetItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.PrimaryPath) || !Directory.Exists(item.PrimaryPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.PrimaryPath, UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private async Task ScanLargeFilesAsync()
    {
        if (IsBusy || SelectedScope is null) return;
        IsBusy = true;

        try
        {
            _status($"正在扫描「{SelectedScope.Title}」中大于 100 MB 的大文件…");
            _allLargeFiles = await _service.ScanLargeFilesAsync(SelectedScope.Key);
            ApplyLargeFilter();
            _status($"大文件扫描完成：共找到 {_allLargeFiles.Count} 个大于 100 MB 的文件");
        }
        catch (Exception ex)
        {
            _status($"扫描大文件失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyLargeFilter()
    {
        LargeFiles.Clear();
        var kw = _largeSearchText.Trim().ToLowerInvariant();

        var query = _allLargeFiles
            .Where(f => kw.Length == 0 || f.SearchBlob.Contains(kw));

        foreach (var item in query) LargeFiles.Add(item);

        var totalBytes = LargeFiles.Sum(f => f.SizeBytes);
        LargeSummaryText = $"扫描范围：{SelectedScope?.Title} · 大于 100 MB 的文件共 {_allLargeFiles.Count} 个（当前显示 {LargeFiles.Count} 个，合计 {ThisPcService.FormatSize(totalBytes)}）";
    }

    private void OpenLargeFileFolder(LargeFileItem? item)
    {
        if (item is null || !File.Exists(item.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception) { }
    }

    private void CopyLargeFilePath(LargeFileItem? item)
    {
        if (item is null) return;
        try
        {
            Clipboard.SetText(item.FilePath);
            _status($"已复制路径：{item.FilePath}");
        }
        catch (Exception) { }
    }

    private void RecycleLargeFile(LargeFileItem? item)
    {
        if (item is null || !File.Exists(item.FilePath)) return;

        var confirm = MessageBox.Show(
            $"确定将大文件移入回收站吗？\n\n文件：{item.FileName}\n大小：{item.SizeText}\n路径：{item.FilePath}\n\n（文件只会移入系统回收站，不会直接永久粉碎）",
            "移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            FileSystem.DeleteFile(item.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _allLargeFiles.Remove(item);
            LargeFiles.Remove(item);
            ApplyLargeFilter();
            _status($"已移入回收站：{item.FileName} ({item.SizeText})");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"移入回收站失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
