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

    public DiffViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Entries = new ObservableCollection<DiffEntry>();
        Filtered = new ObservableCollection<DiffEntry>();

        CompareCommand = new RelayCommand(() => _ = CompareAsync());
        CancelCommand = new RelayCommand(Cancel);
        BrowseLeftCommand = new RelayCommand(() => Browse(true));
        BrowseRightCommand = new RelayCommand(() => Browse(false));
        SwapCommand = new RelayCommand(Swap);
        CopyLeftPathCommand = new RelayCommand(p => CopyPath(p as DiffEntry, true));
        CopyRightPathCommand = new RelayCommand(p => CopyPath(p as DiffEntry, false));
        OpenLeftFolderCommand = new RelayCommand(p => OpenFolder(p as DiffEntry, true));
        OpenRightFolderCommand = new RelayCommand(p => OpenFolder(p as DiffEntry, false));
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
        set => Set(ref _leftPath, value);
    }

    public string RightPath
    {
        get => _rightPath;
        set => Set(ref _rightPath, value);
    }

    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    public bool CompareHash
    {
        get => _compareHash;
        set => Set(ref _compareHash, value);
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
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
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

    public void Refresh()
    {
        if (!string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath))
            _ = CompareAsync();
    }

    private void Browse(bool left)
    {
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
        (LeftPath, RightPath) = (RightPath, LeftPath);
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _status("已请求取消…");
    }

    private async Task CompareAsync()
    {
        if (IsBusy) return;

        var left = LeftPath.Trim().Trim('"');
        var right = RightPath.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            MessageBox.Show("请先选择左右两个路径（文件夹或同类型文件）。", "WinKit",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        ErrorText = "";
        Entries.Clear();
        Filtered.Clear();
        SummaryText = "正在对比…";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var (entries, error) = await Task.Run(
                () => _service.Compare(left, right, Recursive, CompareHash, s => _status(s), token), token);

            ErrorText = error ?? "";

            foreach (var entry in entries) Entries.Add(entry);
            ApplyFilter();

            SummaryText = DiffService.Summarize(entries);
            _status($"对比完成：{entries.Count} 项");
        }
        catch (OperationCanceledException)
        {
            SummaryText = "已取消";
            _status("已取消对比");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _status($"对比失败：{ex.Message}");
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

    private void CopyPath(DiffEntry? entry, bool left)
    {
        if (entry is null) return;

        var full = Path.Combine(left ? LeftPath : RightPath, entry.RelativePath);
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
        if (entry is null) return;

        var full = Path.Combine(left ? LeftPath : RightPath, entry.RelativePath);
        try
        {
            if (File.Exists(full))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{full}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(full))
            {
                Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
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
