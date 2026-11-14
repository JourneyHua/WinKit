using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class FileLockViewModel : ObservableObject, IModule
{
    private readonly FileLockService _service = new();
    private readonly Action<string> _status;
    private string _path = "";
    private string _resultText = "";
    private bool _isBusy;

    public FileLockViewModel(Action<string> status)
    {
        _status = status;
        Lockers = new ObservableCollection<FileLocker>();

        BrowseCommand = new RelayCommand(Browse);
        ScanCommand = new RelayCommand(() => _ = ScanAsync());
        KillCommand = new RelayCommand(p => Kill(p as FileLocker));
        KillAllCommand = new RelayCommand(() => _ = KillAllAsync());
        CopyPathCommand = new RelayCommand(p => CopyPath(p as FileLocker));
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as FileLocker));
        ClearCommand = new RelayCommand(Clear);
    }

    public ObservableCollection<FileLocker> Lockers { get; }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand KillCommand { get; }
    public RelayCommand KillAllCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearCommand { get; }

    public string PathText
    {
        get => _path;
        set => Set(ref _path, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool HasLockers => Lockers.Count > 0;

    public void Refresh()
    {
        if (!string.IsNullOrWhiteSpace(PathText)) _ = ScanAsync();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要检查占用的文件",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true) PathText = dialog.FileName;
    }

    private async Task ScanAsync()
    {
        if (IsBusy) return;

        var path = PathText.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("请先选择或粘贴一个文件/文件夹路径。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        Lockers.Clear();
        ResultText = "正在查询占用进程…";

        try
        {
            var (lockers, error) = await Task.Run(() => _service.FindLockers(path));

            foreach (var locker in lockers) Lockers.Add(locker);
            OnPropertyChanged(nameof(HasLockers));

            if (lockers.Count > 0)
            {
                ResultText = error is null
                    ? $"发现 {lockers.Count} 个进程正在占用"
                    : $"发现 {lockers.Count} 个进程（{error.Split('\n')[0]}）";
                _status($"发现 {lockers.Count} 个占用进程");
            }
            else if (error is not null)
            {
                ResultText = error;
                _status(error.Split('\n')[0]);
            }
            else
            {
                ResultText = "没有进程占用这个路径（可以安全删除/移动）";
                _status("未发现占用进程");
            }
        }
        catch (Exception ex)
        {
            ResultText = $"查询失败：{ex.Message}";
            _status(ResultText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Kill(FileLocker? locker)
    {
        if (locker is null) return;
        if (!locker.CanKill)
        {
            MessageBox.Show(locker.Note, "无法结束", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定结束「{locker.ProcessName}」（PID {locker.ProcessId}）吗？\n\n" +
            "该进程里未保存的数据会丢失。",
            "结束占用进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = _service.Kill(locker.ProcessId);
        _status(message);
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);

        _ = ScanAsync();
    }

    private async Task KillAllAsync()
    {
        var targets = Lockers.Where(l => l.CanKill).ToList();
        if (targets.Count == 0) return;

        var confirm = MessageBox.Show(
            $"将结束 {targets.Count} 个占用进程：\n\n• " +
            string.Join("\n• ", targets.Select(t => $"{t.ProcessName} (PID {t.ProcessId})")) +
            "\n\n这些进程里未保存的数据会丢失。确定继续吗？",
            "结束全部占用进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        // 每个 Kill 最多等 3 秒，遇到不响应终止的进程批量结束会假死，必须放到后台
        IsBusy = true;
        _status($"正在结束 {targets.Count} 个进程…");

        try
        {
            var results = await Task.Run(() =>
                targets.Select(t => (t.ProcessName, t.ProcessId, Result: _service.Kill(t.ProcessId))).ToList());

            var failed = results.Count(r => !r.Result.Ok);
            _status(failed == 0
                ? $"已结束 {targets.Count} 个进程"
                : $"已结束 {targets.Count - failed} 个，{failed} 个失败");

            if (failed > 0)
            {
                MessageBox.Show(
                    "以下进程未能结束（可能仍在退出中或受保护）：\n\n• " +
                    string.Join("\n• ", results.Where(r => !r.Result.Ok).Select(r => $"{r.ProcessName}（PID {r.ProcessId}）")),
                    "部分结束失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsBusy = false;
        }

        _ = ScanAsync();
    }

    private void CopyPath(FileLocker? locker)
    {
        if (locker is null) return;
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(locker.ExecutablePath)
                ? $"{locker.ProcessName} (PID {locker.ProcessId})"
                : locker.ExecutablePath);
            _status("已复制进程路径");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void OpenFolder(FileLocker? locker)
    {
        if (locker is null || string.IsNullOrWhiteSpace(locker.ExecutablePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{locker.ExecutablePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }

    private void Clear()
    {
        Lockers.Clear();
        OnPropertyChanged(nameof(HasLockers));
        ResultText = "";
        PathText = "";
    }
}
