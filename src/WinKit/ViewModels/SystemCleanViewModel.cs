using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
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
    private readonly List<CleanupTargetItem> _scanTargets;
    private readonly CleanupTargetItem _recycleBin;

    private List<LargeFileItem> _allLargeFiles = [];
    private bool _showLargeView;
    private string _largeSearchText = "";
    private ScopeOption? _selectedScope;
    private ScopeOption? _largeResultsScope;
    private bool _isBusy;
    private string _junkSummaryText = "正在准备扫描…";
    private string _largeSummaryText = "请选择范围，扫描其中的大文件。";
    private string _junkActionText = "";

    public SystemCleanViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;
        Scopes = new ObservableCollection<ScopeOption>
        {
            new() { Key = "user", Title = "当前用户目录" }
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            Scopes.Add(new ScopeOption
            {
                Key = drive.RootDirectory.FullName,
                Title = $"{drive.Name.TrimEnd('\\')} 盘（可用 {ThisPcService.FormatSize(drive.AvailableFreeSpace)}）"
            });
        }
        _selectedScope = Scopes[0];

        _scanTargets = _service.BuildTargets();
        _recycleBin = _scanTargets.Single(t => t.IsRecycleBin);
        var elevated = Native.IsElevated();
        foreach (var target in _scanTargets.Where(t => !t.IsRecycleBin))
        {
            target.CanSelect = !target.RequiresAdmin || elevated;
            if (!target.CanSelect) target.IsSelected = false;
            target.SelectionChanged = UpdateJunkSummary;
            Targets.Add(target);
        }

        ShowJunkViewCommand = new RelayCommand(() => ShowView(false), () => !IsBusy);
        ShowLargeViewCommand = new RelayCommand(() => ShowView(true), () => !IsBusy);
        ScanJunkCommand = new RelayCommand(() => Run(ScanJunkAsync), () => !IsBusy);
        CleanSelectedCommand = new RelayCommand(() => RunWrite(CleanSelectedAsync), () => !IsBusy && HasSelectedFiles);
        EmptyRecycleBinCommand = new RelayCommand(() => RunWrite(EmptyRecycleBinAsync), () => !IsBusy && HasRecycleBinItems);
        RunDismCleanCommand = new RelayCommand(() => RunWrite(RunDismCleanAsync), () => !IsBusy);
        OpenStorageSenseCommand = new RelayCommand(() => Native.OpenPath("ms-settings:storagesense"));
        OpenTargetFolderCommand = new RelayCommand(p => OpenTargetFolder(p as CleanupTargetItem));
        ScanLargeFilesCommand = new RelayCommand(() => Run(ScanLargeFilesAsync), () => !IsBusy && SelectedScope is not null);
        OpenLargeFileFolderCommand = new RelayCommand(p => OpenLargeFileFolder((LargeFileItem)p!), CanUseLargeResult);
        CopyLargeFilePathCommand = new RelayCommand(p => CopyLargeFilePath((LargeFileItem)p!), CanUseLargeResult);
        RecycleLargeFileCommand = new RelayCommand(p => RunWrite(() => RecycleLargeFileAsync((LargeFileItem)p!)), CanUseLargeResult);
    }

    public ObservableCollection<CleanupTargetItem> Targets { get; } = [];
    public ObservableCollection<LargeFileItem> LargeFiles { get; } = [];
    public ObservableCollection<ScopeOption> Scopes { get; }

    public RelayCommand ShowJunkViewCommand { get; }
    public RelayCommand ShowLargeViewCommand { get; }
    public RelayCommand ScanJunkCommand { get; }
    public RelayCommand CleanSelectedCommand { get; }
    public RelayCommand EmptyRecycleBinCommand { get; }
    public RelayCommand RunDismCleanCommand { get; }
    public RelayCommand OpenStorageSenseCommand { get; }
    public RelayCommand OpenTargetFolderCommand { get; }
    public RelayCommand ScanLargeFilesCommand { get; }
    public RelayCommand OpenLargeFileFolderCommand { get; }
    public RelayCommand CopyLargeFilePathCommand { get; }
    public RelayCommand RecycleLargeFileCommand { get; }

    public bool ShowJunkView => !ShowLargeView;
    public bool ShowLargeView => _showLargeView;

    private void ShowView(bool large)
    {
        _showLargeView = large;
        OnPropertyChanged(nameof(ShowJunkView));
        OnPropertyChanged(nameof(ShowLargeView));
        if (large && _largeResultsScope is null) Run(ScanLargeFilesAsync);
    }

    public ScopeOption? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (!Set(ref _selectedScope, value)) return;
            ClearLargeResults(value is null ? "请选择扫描范围。" : $"尚未扫描「{value.Title}」。");
            SetError(null);
        }
    }

    public string LargeSearchText
    {
        get => _largeSearchText;
        set
        {
            if (Set(ref _largeSearchText, value) && _largeResultsScope is not null) ApplyLargeFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEditInputs));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditInputs => !IsBusy;

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

    public string JunkActionText
    {
        get => _junkActionText;
        private set
        {
            if (Set(ref _junkActionText, value)) OnPropertyChanged(nameof(HasJunkAction));
        }
    }

    public bool HasJunkAction => JunkActionText.Length > 0;
    public bool HasRecycleBinItems => _recycleBin.FileCount > 0;
    public string RecycleBinSummary => $"回收站（所有分区）：{_recycleBin.FileCount} 项，约 {_recycleBin.SizeText}";
    private IEnumerable<CleanupTargetItem> SelectedTargets => Targets.Where(t => t.CanSelect && t.IsSelected && t.FileCount > 0);
    public bool HasSelectedFiles => SelectedTargets.Any();

    public void Refresh() => Run(ShowLargeView ? ScanLargeFilesAsync : ScanJunkAsync);

    // 扫描与写入不能在同一页重叠，但只有写入会阻止切换模块或关闭窗口。
    private async void Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        SetError(null);
        try { await action(); }
        catch (Exception ex) { SetError($"操作未完成：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private void RunWrite(Func<Task> write) => RunWrite(async () =>
    {
        await write();
        return false;
    });

    // 写入完成后如需重新估算，只保留页面锁，不再阻止导航或关闭。
    private void RunWrite(Func<Task<bool>> write) => Run(async () =>
    {
        bool refreshJunk;
        _setBusy?.Invoke(true);
        try { refreshJunk = await write(); }
        catch (OperationCanceledException)
        {
            _status("操作已取消。");
            return;
        }
        finally { _setBusy?.Invoke(false); }

        if (refreshJunk) await RefreshJunkEstimatesAsync();
    });

    private Task ScanJunkAsync()
    {
        JunkActionText = "";
        return RefreshJunkEstimatesAsync();
    }

    private async Task RefreshJunkEstimatesAsync()
    {
        JunkSummaryText = "正在估算可清理空间… 切换模块后扫描仍会继续。";
        try { await _service.ScanTargetsAsync(_scanTargets); }
        catch (Exception ex)
        {
            JunkSummaryText = "未能更新清理估算，请重新扫描。";
            var error = $"未能更新清理估算：{ex.Message}";
            SetError(HasError ? $"{ErrorText}\n{error}" : error);
            return;
        }

        UpdateJunkSummary();
        OnPropertyChanged(nameof(HasRecycleBinItems));
        OnPropertyChanged(nameof(RecycleBinSummary));
    }

    private void UpdateJunkSummary()
    {
        var selectedCount = SelectedTargets.Count();
        JunkSummaryText = selectedCount > 0
            ? $"已选 {selectedCount} 类 · 预计释放 {ThisPcService.FormatSize(SelectedTargets.Sum(t => t.SizeBytes))}"
            : "没有选中可清理的文件";
        OnPropertyChanged(nameof(HasSelectedFiles));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task<bool> CleanSelectedAsync()
    {
        var selected = SelectedTargets.ToList();
        if (selected.Count == 0) return false;
        var list = string.Join("\n• ", selected.Select(s => $"{s.Title}（约 {s.SizeText}）"));
        if (MessageBox.Show(
            $"将永久删除以下项目中符合条件的文件，不经过回收站。\n\n• {list}\n\n最近 2 小时内创建或修改的文件会保留。无法删除的项目会跳过。继续清理吗？",
            "清理已选文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;

        JunkActionText = "正在清理已选文件…";
        var (freed, count, skipped) = await _service.CleanTargetsAsync(selected, _status);
        var note = skipped > 0 ? $"，另有 {skipped} 项未能处理" : "";
        JunkActionText = $"已删除 {count} 个文件（约 {ThisPcService.FormatSize(freed)}）{note}。";
        _status(JunkActionText);
        return true;
    }

    private async Task<bool> EmptyRecycleBinAsync()
    {
        if (!HasRecycleBinItems) return false;
        if (MessageBox.Show(
            $"{RecycleBinSummary}（上次扫描估算）。\n\n继续将永久删除所有分区回收站里的文件，包括扫描后新移入的文件。清空后无法从回收站恢复。\n\n确定永久清空吗？",
            "永久清空回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;

        JunkActionText = "正在清空回收站…";
        var (ok, message) = await _service.EmptyRecycleBinAsync();
        JunkActionText = ok ? message : "";
        if (!ok) SetError(message);
        _status(message);
        return true;
    }

    private async Task RunDismCleanAsync()
    {
        if (Native.RequireAdmin("清理旧版 Windows 组件") is { } adminNeeded) { SetError(adminNeeded); return; }
        if (MessageBox.Show(
            "将让 Windows 删除已被更新替代的旧版系统组件。此操作可能需要几分钟，WinKit 无法撤销。继续吗？",
            "清理旧版 Windows 组件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        JunkActionText = "正在清理旧版 Windows 组件…";
        var (ok, message) = await _service.RunDismComponentCleanupAsync(_status);
        JunkActionText = ok ? "已清理旧版 Windows 组件。" : "";
        if (!ok) SetError($"组件清理未完成：{message}");
        _status(ok ? JunkActionText : "组件清理未完成。");
    }

    private static void OpenTargetFolder(CleanupTargetItem? item)
    {
        if (item is { HasDirectory: true }) Native.OpenPath(item.PrimaryPath);
    }

    private async Task ScanLargeFilesAsync()
    {
        if (SelectedScope is not { } scope) return;
        ClearLargeResults($"正在扫描「{scope.Title}」… 切换模块后扫描仍会继续。");
        try
        {
            _allLargeFiles = await _service.ScanLargeFilesAsync(scope.Key);
            _largeResultsScope = scope;
            ApplyLargeFilter();
        }
        catch (Exception ex)
        {
            LargeSummaryText = $"未能扫描「{scope.Title}」。";
            SetError(ex.Message);
        }
    }

    private void ClearLargeResults(string message)
    {
        _largeResultsScope = null;
        _allLargeFiles.Clear();
        LargeFiles.Clear();
        LargeSummaryText = message;
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyLargeFilter()
    {
        if (_largeResultsScope is not { } scope) return;
        LargeFiles.Clear();
        var keyword = LargeSearchText.Trim();
        foreach (var file in _allLargeFiles.Where(f => f.SearchBlob.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            LargeFiles.Add(file);

        LargeSummaryText = _allLargeFiles.Count == 0
            ? $"在「{scope.Title}」可读取的目录中未找到 100 MB 及以上的文件。"
            : $"「{scope.Title}」· 显示 {LargeFiles.Count} 个 100 MB 及以上文件（最多列出最大的 300 个）";
    }

    private bool CanUseLargeResult(object? item) => !IsBusy && _largeResultsScope is not null &&
        ReferenceEquals(SelectedScope, _largeResultsScope) && item is LargeFileItem file && _allLargeFiles.Contains(file);

    private void OpenLargeFileFolder(LargeFileItem item)
    {
        if (File.Exists(item.FilePath)) Native.RevealInExplorer(item.FilePath);
        else SetError("文件已找不到，请重新扫描。");
    }

    private void CopyLargeFilePath(LargeFileItem item)
    {
        try
        {
            Clipboard.SetText(item.FilePath);
            _status($"已复制路径：{item.FilePath}");
        }
        catch (Exception ex) { SetError($"未能复制路径：{ex.Message}"); }
    }

    private async Task RecycleLargeFileAsync(LargeFileItem item)
    {
        if (!File.Exists(item.FilePath)) { SetError("文件已找不到，请重新扫描。"); return; }
        if (MessageBox.Show(
            $"将此文件移入系统回收站：\n\n{item.FilePath}\n大小：{item.SizeText}\n\n文件仍在回收站时可从那里恢复。继续吗？",
            "移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        await Task.Run(() =>
        {
            var current = new FileInfo(item.FilePath);
            if (!current.Exists || current.Length != item.SizeBytes || current.LastWriteTime != item.LastModified ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("文件自扫描后已变化或为链接，请重新扫描后再处理。");
            FileSystem.DeleteFile(item.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        });
        _allLargeFiles.Remove(item);
        ApplyLargeFilter();
        _status($"已移入回收站：{item.FileName}");
    }

    private string _errorText = "";
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
