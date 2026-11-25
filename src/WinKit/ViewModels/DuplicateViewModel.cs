using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class DuplicateViewModel : ObservableObject, IModule
{
    private readonly DuplicateFinderService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private CancellationTokenSource? _cts;
    private int _rootsVersion;

    private bool _isBusy;
    private bool _includeHidden;
    private string _minSizeText = "1 MB";
    private string _summary = "";
    private string _errorText = "";

    public DuplicateViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Roots = new ObservableCollection<string>();
        Groups = new ObservableCollection<DuplicateGroup>();

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsBusy && Roots.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && _cts is { IsCancellationRequested: false });
        AddRootCommand = new RelayCommand(AddRoot, () => !IsBusy);
        RemoveRootCommand = new RelayCommand(p => RemoveRoot(p as string),
            p => !IsBusy && p is string root && Roots.Contains(root));
        ClearRootsCommand = new RelayCommand(ClearRoots, () => !IsBusy && Roots.Count > 0);
        DeleteGroupDuplicatesCommand = new RelayCommand(p => _ = DeleteGroupDuplicatesAsync(p as DuplicateGroup),
            p => !IsBusy && p is DuplicateGroup group && group.Count > 1 && Groups.Contains(group));
        OpenFileFolderCommand = new RelayCommand(p => OpenFileFolder(p as DuplicateFile));
        DeleteFileCommand = new RelayCommand(p => _ = DeleteFileAsync(p as DuplicateFile),
            p => !IsBusy && p is DuplicateFile file && Groups.Any(g => g.Count > 1 && g.Files.Contains(file)));
        SelectCommonRootsCommand = new RelayCommand(AddCommonRoots, () => !IsBusy);

        Roots.CollectionChanged += (_, _) =>
        {
            _rootsVersion++;
            Groups.Clear();
            OnPropertyChanged(nameof(HasGroups));
            SummaryText = "";
            ErrorText = "";
            ScanCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<string> Roots { get; }
    public ObservableCollection<DuplicateGroup> Groups { get; }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddRootCommand { get; }
    public RelayCommand RemoveRootCommand { get; }
    public RelayCommand ClearRootsCommand { get; }
    public RelayCommand DeleteGroupDuplicatesCommand { get; }
    public RelayCommand OpenFileFolderCommand { get; }
    public RelayCommand DeleteFileCommand { get; }
    public RelayCommand SelectCommonRootsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEditScanOptions));
            ScanCommand.RaiseCanExecuteChanged();
            _setBusy?.Invoke(value);
        }
    }

    public bool CanEditScanOptions => !IsBusy;

    public bool IncludeHidden
    {
        get => _includeHidden;
        set { if (!IsBusy) Set(ref _includeHidden, value); }
    }

    public string MinSizeText
    {
        get => _minSizeText;
        set { if (!IsBusy) Set(ref _minSizeText, value); }
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public bool HasGroups => Groups.Count > 0;

    public void Refresh()
    {
        if (!IsBusy && Roots.Count > 0) _ = ScanAsync();
    }

    private void AddCommonRoots()
    {
        if (IsBusy) return;

        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        foreach (var dir in candidates)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            if (Roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            Roots.Add(dir);
        }

        _status($"已加入 {Roots.Count} 个常用目录");
    }

    private void AddRoot()
    {
        if (IsBusy) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描重复文件的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true || IsBusy) return;
        if (Roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;

        Roots.Add(dialog.FolderName);
    }

    private void RemoveRoot(string? root)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(root)) return;
        Roots.Remove(root);
    }

    private void ClearRoots()
    {
        if (IsBusy) return;
        Roots.Clear();
    }

    private static long ParseMinSize(string text, out string? error)
    {
        error = null;
        var raw = (text ?? "").Trim().ToUpperInvariant();
        if (raw.Length == 0) return 1024;

        var units = new (string Suffix, long Factor)[]
        {
            ("GB", 1024L * 1024 * 1024),
            ("MB", 1024L * 1024),
            ("KB", 1024L),
            ("B", 1L)
        };

        foreach (var (suffix, factor) in units)
        {
            if (!raw.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var numberPart = raw[..^suffix.Length].Trim();
            if (!double.TryParse(numberPart, out var value) || value < 0)
            {
                error = $"无法识别的最小体积：{text}";
                return 1024;
            }

            return (long)(value * factor);
        }

        if (double.TryParse(raw, out var bytes) && bytes >= 0) return (long)bytes;

        error = $"无法识别的最小体积：{text}";
        return 1024;
    }

    private void Cancel()
    {
        if (!IsBusy || _cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        CancelCommand.RaiseCanExecuteChanged();
        _status("已请求取消…");
    }

    private async Task ScanAsync()
    {
        if (IsBusy) return;

        if (Roots.Count == 0)
        {
            MessageBox.Show("请先添加至少一个要扫描的文件夹（可以点「加入常用文件夹」快速开始）。",
                "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var minSize = ParseMinSize(MinSizeText, out var sizeError);
        if (sizeError is not null)
        {
            MessageBox.Show(sizeError, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scanRoots = Roots.ToArray();
        var includeHidden = IncludeHidden;
        var rootsVersion = _rootsVersion;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        ErrorText = "";
        Groups.Clear();
        OnPropertyChanged(nameof(HasGroups));
        SummaryText = "正在扫描…";

        try
        {
            var (groups, error) = await Task.Run(
                () => _service.Find(scanRoots, minSize, includeHidden, s => _status(s), token), token);

            if (rootsVersion != _rootsVersion)
            {
                ErrorText = "扫描范围已变化，本次结果已丢弃，请重新扫描";
                SummaryText = "扫描未应用";
                _status(ErrorText);
                return;
            }

            ErrorText = error ?? "";

            foreach (var group in groups) Groups.Add(group);
            OnPropertyChanged(nameof(HasGroups));

            if (error is not null || token.IsCancellationRequested)
            {
                var cancelled = token.IsCancellationRequested || error == "已取消扫描";
                ErrorText = error ?? "已取消扫描";
                SummaryText = cancelled
                    ? $"扫描已取消，结果不完整（已发现 {groups.Count} 组）"
                    : $"扫描失败：{error}（已发现 {groups.Count} 组，结果不完整）";
                _status(SummaryText);
                return;
            }

            SummaryText = DuplicateFinderService.Summarize(groups);
            _status($"重复文件扫描完成：{groups.Count} 组");
        }
        catch (OperationCanceledException)
        {
            ErrorText = "已取消扫描";
            SummaryText = "扫描已取消，结果不完整";
            _status(SummaryText);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SummaryText = $"扫描失败：{ex.Message}";
            _status(SummaryText);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private Task DeleteGroupDuplicatesAsync(DuplicateGroup? group)
    {
        if (IsBusy || group is null || group.Files.Count < 2 || !Groups.Contains(group))
            return Task.CompletedTask;

        // 默认保留路径字典序最先列出的那份；与单文件清理使用相同的复核流程。
        return DeleteFilesAsync(group, group.Files.Skip(1), group.Files[0].FullPath);
    }

    private Task DeleteFileAsync(DuplicateFile? file)
    {
        if (IsBusy || file is null) return Task.CompletedTask;
        var group = Groups.FirstOrDefault(g => g.Files.Contains(file));
        var targetPath = DuplicateFinderService.NormalizePath(file.FullPath);
        var keep = group?.Files.FirstOrDefault(f =>
            DuplicateFinderService.NormalizePath(f.FullPath) is { } path &&
            !StringComparer.OrdinalIgnoreCase.Equals(path, targetPath));
        if (group is null || targetPath is null || keep is null)
        {
            ErrorText = "无法确认同组的另一份保留文件，请重新扫描";
            _status(ErrorText);
            return Task.CompletedTask;
        }

        return DeleteFilesAsync(group, new[] { file }, keep.FullPath);
    }

    private async Task DeleteFilesAsync(DuplicateGroup group, IEnumerable<DuplicateFile> files, string keepPath)
    {
        if (IsBusy || !Groups.Contains(group)) return;

        var keep = DuplicateFinderService.NormalizePath(keepPath);
        if (keep is null)
        {
            ErrorText = "无法确认保留文件路径，已取消清理";
            _status(ErrorText);
            return;
        }

        var paths = files.Select(f => DuplicateFinderService.NormalizePath(f.FullPath))
            .OfType<string>()
            .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, keep))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            ErrorText = "没有可安全移入回收站的其它文件";
            _status(ErrorText);
            return;
        }

        var confirm = MessageBox.Show(
            $"将保留：\n{keep}\n\n把以下 {paths.Count} 份移入回收站：\n• " +
            string.Join("\n• ", paths) +
            "\n\n操作前会按扫描时的大小和完整 SHA-256 重新复核，变化或无法确认的文件会跳过。" +
            "\n移入回收站后仍可还原。确定继续吗？",
            "清理重复文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK || IsBusy || !Groups.Contains(group)) return;

        var expectedSize = group.Size;
        var expectedHash = group.Hash;
        IsBusy = true;
        ErrorText = "";
        SummaryText = "正在重新核验内容并移入回收站…";
        try
        {
            var result = await Task.Run(() =>
                DuplicateFinderService.MoveToRecycleBin(paths, keep, expectedSize, expectedHash));
            RemoveRecycledFiles(result.MovedPaths);
            SummaryText = result.Message;
            ErrorText = string.Join("\n\n", result.Failures);
            _status(result.Message);
        }
        catch (Exception ex)
        {
            ErrorText = $"{ex.Message}\n请重新扫描以确认当前文件状态";
            SummaryText = "清理未能完成";
            _status(SummaryText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveRecycledFiles(IReadOnlyList<string> movedPaths)
    {
        if (movedPaths.Count == 0) return;
        var moved = new HashSet<string>(movedPaths, StringComparer.OrdinalIgnoreCase);
        for (var i = Groups.Count - 1; i >= 0; i--)
        {
            var group = Groups[i];
            var remaining = group.Files.Where(f =>
                DuplicateFinderService.NormalizePath(f.FullPath) is not { } path || !moved.Contains(path)).ToList();
            if (remaining.Count == group.Files.Count) continue;
            if (remaining.Count < 2)
                Groups.RemoveAt(i);
            else
                Groups[i] = new DuplicateGroup { Hash = group.Hash, Size = group.Size, Files = remaining };
        }
        OnPropertyChanged(nameof(HasGroups));
    }

    private void OpenFileFolder(DuplicateFile? file)
    {
        if (file is null) return;

        try
        {
            Native.RevealInExplorer(file.FullPath);
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
