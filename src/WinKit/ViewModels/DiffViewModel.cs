using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class DiffViewModel : ObservableObject, IModule
{
    private readonly DiffService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private CancellationTokenSource? _cts;

    private string _leftPath = "";
    private string _rightPath = "";
    private bool _recursive = true;
    private bool _compareHash;
    private bool _onlyDifferences = true;
    private bool _isBusy;
    private string _summary = "";
    private string _errorText = "";
    private ComparisonScope? _resultScope;
    private readonly HashSet<DiffEntry> _resultEntries = [];

    private sealed record ComparisonScope(
        string LeftPath, string RightPath, bool Recursive, bool CompareHash, bool SingleFile);

    public DiffViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Entries = new ObservableCollection<DiffEntry>();
        Filtered = new ObservableCollection<DiffEntry>();

        CompareCommand = new RelayCommand(() => _ = CompareAsync(),
            () => CanEditInputs && !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath));
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        BrowseLeftCommand = new RelayCommand(() => Browse(true), () => CanEditInputs);
        BrowseRightCommand = new RelayCommand(() => Browse(false), () => CanEditInputs);
        SwapCommand = new RelayCommand(Swap, () => CanEditInputs);
        CopyLeftPathCommand = new RelayCommand(p => CopyPath(p as DiffEntry, true), CanUseResult);
        CopyRightPathCommand = new RelayCommand(p => CopyPath(p as DiffEntry, false), CanUseResult);
        OpenLeftFolderCommand = new RelayCommand(p => OpenFolder(p as DiffEntry, true), CanUseResult);
        OpenRightFolderCommand = new RelayCommand(p => OpenFolder(p as DiffEntry, false), CanUseResult);
    }

    public ObservableCollection<DiffEntry> Entries { get; }
    public ObservableCollection<DiffEntry> Filtered { get; }

    public RelayCommand CompareCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseLeftCommand { get; }
    public RelayCommand BrowseRightCommand { get; }
    public RelayCommand SwapCommand { get; }
    public RelayCommand CopyLeftPathCommand { get; }
    public RelayCommand CopyRightPathCommand { get; }
    public RelayCommand OpenLeftFolderCommand { get; }
    public RelayCommand OpenRightFolderCommand { get; }

    public string LeftPath
    {
        get => _leftPath;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (Set(ref _leftPath, value)) InvalidateResults();
        }
    }

    public string RightPath
    {
        get => _rightPath;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (Set(ref _rightPath, value)) InvalidateResults();
        }
    }

    public bool Recursive
    {
        get => _recursive;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (Set(ref _recursive, value)) InvalidateResults();
        }
    }

    public bool CompareHash
    {
        get => _compareHash;
        set
        {
            if (IsBusy) { OnPropertyChanged(); return; }
            if (Set(ref _compareHash, value)) InvalidateResults();
        }
    }

    public bool OnlyDifferences
    {
        get => _onlyDifferences;
        set
        {
            if (Set(ref _onlyDifferences, value)) ApplyFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEditInputs));
            CompareCommand.RaiseCanExecuteChanged();
            _setBusy?.Invoke(value);
        }
    }

    public bool CanEditInputs => !IsBusy;

    public string ResultScopeText => _resultScope is { } scope
        ? $"左：{scope.LeftPath}\n右：{scope.RightPath}\n" +
          $"{(scope.SingleFile ? "单文件对比" : scope.Recursive ? "包含子目录" : "仅当前目录")} · " +
          (scope.CompareHash ? "校验内容哈希" : "按大小和修改时间比较")
        : "";

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

    public void Refresh()
    {
        if (!string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath))
            _ = CompareAsync();
    }

    private void InvalidateResults()
    {
        _resultScope = null;
        _resultEntries.Clear();
        Entries.Clear();
        Filtered.Clear();
        ErrorText = "";
        SummaryText = "对比条件已更改，请重新对比";
        OnPropertyChanged(nameof(ResultScopeText));
        CompareCommand.RaiseCanExecuteChanged();
    }

    private bool CanUseResult(object? value)
        => !IsBusy && _resultScope is not null && value is DiffEntry entry && _resultEntries.Contains(entry);

    private void Browse(bool left)
    {
        if (IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = left ? "选择左侧文件夹" : "选择右侧文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        if (left) LeftPath = dialog.FolderName;
        else RightPath = dialog.FolderName;
    }

    private void Swap()
    {
        if (IsBusy) return;
        (LeftPath, RightPath) = (RightPath, LeftPath);
    }

    private void Cancel()
    {
        if (!IsBusy) return;
        _cts?.Cancel();
        _status("已请求取消…");
    }

    private async Task CompareAsync()
    {
        if (IsBusy) return;

        var left = LeftPath.Trim().Trim('"');
        var right = RightPath.Trim().Trim('"');
        var recursive = Recursive;
        var compareHash = CompareHash;

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            MessageBox.Show("请先选择左右两个路径（文件夹或同类型文件）。", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        ErrorText = "";
        _resultScope = null;
        _resultEntries.Clear();
        OnPropertyChanged(nameof(ResultScopeText));
        Entries.Clear();
        Filtered.Clear();
        SummaryText = "正在对比…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var (entries, error, scope) = await Task.Run(() =>
            {
                var fullLeft = Path.GetFullPath(left);
                var fullRight = Path.GetFullPath(right);
                var capturedScope = new ComparisonScope(fullLeft, fullRight, recursive, compareHash,
                    File.Exists(fullLeft) && File.Exists(fullRight));
                var result = _service.Compare(capturedScope.LeftPath, capturedScope.RightPath, capturedScope.Recursive,
                    capturedScope.CompareHash, s => _status(s), token);
                return (result.Entries, result.Error, capturedScope);
            }, token);

            _resultScope = scope;
            _resultEntries.UnionWith(entries);
            OnPropertyChanged(nameof(ResultScopeText));
            ErrorText = error ?? "";

            foreach (var entry in entries) Entries.Add(entry);
            ApplyFilter();

            if (error is not null || token.IsCancellationRequested)
            {
                var cancelled = token.IsCancellationRequested || error == "已取消对比";
                ErrorText = error ?? "已取消对比";
                SummaryText = cancelled
                    ? $"对比已取消，结果不完整（已返回 {entries.Count} 项）"
                    : $"对比失败：{error}（已返回 {entries.Count} 项，结果不完整）";
                _status(SummaryText);
                return;
            }

            SummaryText = DiffService.Summarize(entries);
            _status($"对比完成：{entries.Count} 项");
        }
        catch (OperationCanceledException)
        {
            ErrorText = "已取消对比";
            SummaryText = "对比已取消，结果不完整";
            _status(SummaryText);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SummaryText = $"对比失败：{ex.Message}";
            _status(SummaryText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var entry in Entries)
        {
            if (_onlyDifferences && entry.Kind == DiffKind.Same) continue;
            Filtered.Add(entry);
        }
    }

    internal string? ResolveResultPath(DiffEntry? entry, bool left)
    {
        // 行操作只接受本次结果中的对象，不能把旧行与另一轮的范围重新组合。
        if (entry is null || _resultScope is not { } scope || !_resultEntries.Contains(entry)) return null;
        var root = left ? scope.LeftPath : scope.RightPath;
        return scope.SingleFile ? root : Path.Combine(root, entry.RelativePath);
    }

    private void CopyPath(DiffEntry? entry, bool left)
    {
        if (IsBusy || ResolveResultPath(entry, left) is not { } full) return;
        try
        {
            Clipboard.SetText(full);
            _status($"已复制：{full}");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void OpenFolder(DiffEntry? entry, bool left)
    {
        if (IsBusy || ResolveResultPath(entry, left) is not { } full) return;
        try
        {
            if (File.Exists(full))
            {
                Native.RevealInExplorer(full);
            }
            else
            {
                _status("该侧不存在这个路径");
            }
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
