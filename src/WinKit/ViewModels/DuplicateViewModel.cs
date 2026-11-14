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

        ScanCommand = new RelayCommand(() => _ = ScanAsync());
        CancelCommand = new RelayCommand(Cancel);
        AddRootCommand = new RelayCommand(AddRoot);
        RemoveRootCommand = new RelayCommand(p => RemoveRoot(p as string));
        ClearRootsCommand = new RelayCommand(ClearRoots);
        DeleteGroupDuplicatesCommand = new RelayCommand(p => _ = DeleteGroupDuplicatesAsync(p as DuplicateGroup));
        OpenFileFolderCommand = new RelayCommand(p => OpenFileFolder(p as DuplicateFile));
        DeleteFileCommand = new RelayCommand(p => _ = DeleteFilesAsync(new[] { p as DuplicateFile }));
        SelectCommonRootsCommand = new RelayCommand(AddCommonRoots);
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
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public bool IncludeHidden
    {
        get => _includeHidden;
        set => Set(ref _includeHidden, value);
    }

    public string MinSizeText
    {
        get => _minSizeText;
        set => Set(ref _minSizeText, value);
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
        if (Roots.Count > 0) _ = ScanAsync();
    }

    private void AddCommonRoots()
    {
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
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描重复文件的文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        if (Roots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) return;

        Roots.Add(dialog.FolderName);
    }

    private void RemoveRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        Roots.Remove(root);
    }

    private void ClearRoots()
    {
        Roots.Clear();
        Groups.Clear();
        OnPropertyChanged(nameof(HasGroups));
        SummaryText = "";
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
        _cts?.Cancel();
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

        IsBusy = true;
        ErrorText = "";
        Groups.Clear();
        OnPropertyChanged(nameof(HasGroups));
        SummaryText = "正在扫描…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var (groups, error) = await Task.Run(
                () => _service.Find(Roots.ToList(), minSize, IncludeHidden, s => _status(s), token), token);

            ErrorText = error ?? "";

            foreach (var group in groups) Groups.Add(group);
            OnPropertyChanged(nameof(HasGroups));

            SummaryText = DuplicateFinderService.Summarize(groups);
            _status($"重复文件扫描完成：{groups.Count} 组");
        }
        catch (OperationCanceledException)
        {
            SummaryText = "已取消";
            _status("已取消扫描");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _status($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteGroupDuplicatesAsync(DuplicateGroup? group)
    {
        if (group is null || group.Files.Count < 2) return;

        // 默认保留最先列出的那份（路径字典序，通常是较早建立的），其余进回收站
        var keep = group.Files[0];
        var targets = group.Files.Skip(1).ToList();

        var confirm = MessageBox.Show(
            $"「{group.SizeText} × {group.Count}」这一组将保留：\n{keep.FullPath}\n\n" +
            $"并把这 {targets.Count} 份移入回收站：\n• " +
            string.Join("\n• ", targets.Select(t => t.FullPath)) +
            "\n\n移入回收站后仍可还原。确定继续吗？",
            "清理重复文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        await DeleteFilesAsync(targets);
    }

    private async Task DeleteFilesAsync(IEnumerable<DuplicateFile?> files)
    {
        var paths = files.Where(f => f is not null).Select(f => f!.FullPath).ToList();
        if (paths.Count == 0) return;

        if (paths.Count == 1)
        {
            var confirmOne = MessageBox.Show(
                $"将把这个文件移入回收站：\n\n{paths[0]}\n\n确定继续吗？",
                "移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirmOne != MessageBoxResult.OK) return;
        }

        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => DuplicateFinderService.MoveToRecycleBin(paths));
            _status(message);

            if (!ok) MessageBox.Show(message, "清理结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        // 删完立刻重扫，避免界面留着已经不存在的文件
        await ScanAsync();
    }

    private void OpenFileFolder(DuplicateFile? file)
    {
        if (file is null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{file.FullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
