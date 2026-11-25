using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class FileLockViewModel : ObservableObject, IModule
{
    private readonly FileLockService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private string _path = "";
    private string _resultText = "";
    private string _queriedPath = "";
    private bool _isBusy;
    private int _pathVersion;

    public FileLockViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;
        Lockers = new ObservableCollection<FileLocker>();
        BrowseCommand = new RelayCommand(Browse);
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsBusy);
        KillCommand = new RelayCommand(p => _ = KillAsync(p as FileLocker),
            p => CanEnd(p as FileLocker));
        KillAllCommand = new RelayCommand(() => _ = KillAllAsync(),
            () => !IsBusy && HasCurrentResults && Lockers.Any(IsKillable));
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
        set
        {
            if (!Set(ref _path, value)) return;
            _pathVersion++;
            QueriedPath = "";
            Lockers.Clear();
            OnPropertyChanged(nameof(HasLockers));
            SetError("");
            ResultText = string.IsNullOrWhiteSpace(value)
                ? "请选择或粘贴路径，然后点击「查询占用」。"
                : "路径已修改，请点击「查询占用」获取该路径的结果。";
            UpdateCommands();
        }
    }

    public string QueriedPath
    {
        get => _queriedPath;
        private set
        {
            if (Set(ref _queriedPath, value)) OnPropertyChanged(nameof(HasCurrentResults));
        }
    }

    public bool HasCurrentResults => !string.IsNullOrEmpty(QueriedPath);
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            _setBusy?.Invoke(value);
            UpdateCommands();
        }
    }

    public bool HasLockers => Lockers.Count > 0;
    private static bool IsKillable(FileLocker locker) => locker.CanKill && locker.StartTimeUtcTicks.HasValue
        && locker.ProcessId > 4 && locker.ProcessId != Environment.ProcessId
        && locker.AppTypeText != "系统关键进程";
    private bool CanEnd(FileLocker? locker) => !IsBusy && HasCurrentResults
        && locker is not null && Lockers.Contains(locker) && IsKillable(locker);

    private void UpdateCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        KillCommand.RaiseCanExecuteChanged();
        KillAllCommand.RaiseCanExecuteChanged();
    }

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
            ResultText = "请先选择或粘贴一个文件/文件夹路径。";
            return;
        }

        var version = _pathVersion;
        IsBusy = true;
        try
        {
            await LoadLockersAsync(path, version);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 调用方持有 busy，结束进程后的复查沿用同一次操作，不递归进入扫描入口。
    private async Task LoadLockersAsync(string path, int version)
    {
        QueriedPath = "";
        Lockers.Clear();
        OnPropertyChanged(nameof(HasLockers));
        SetError("");
        ResultText = "正在查询占用进程…";
        UpdateCommands();
        try
        {
            var (lockers, error) = await Task.Run(() => _service.FindLockers(path));
            if (version != _pathVersion) return;
            QueriedPath = path;
            foreach (var locker in lockers) Lockers.Add(locker);
            OnPropertyChanged(nameof(HasLockers));
            ResultText = lockers.Count > 0
                ? error is null
                    ? $"发现 {lockers.Count} 个进程正在占用"
                    : $"发现 {lockers.Count} 个进程（{error.Split('\n')[0]}）"
                : error ?? "本次查询未发现占用进程；占用情况可能随时变化。";
            _status(ResultText);
        }
        catch (Exception ex)
        {
            if (version != _pathVersion) return;
            ResultText = $"查询失败：{ex.Message}";
            SetError(ResultText);
            _status(ResultText);
        }
        finally
        {
            UpdateCommands();
        }
    }

    private async Task KillAsync(FileLocker? locker)
    {
        if (!CanEnd(locker)) return;
        await EndProcessesAsync(new[] { locker! });
    }

    private async Task KillAllAsync()
    {
        if (IsBusy || !HasCurrentResults) return;
        var targets = Lockers.Where(IsKillable).ToList();
        if (targets.Count == 0) return;
        await EndProcessesAsync(targets);
    }

    private async Task EndProcessesAsync(IReadOnlyList<FileLocker> targets)
    {
        if (IsBusy || !HasCurrentResults || targets.Any(t => !Lockers.Contains(t) || !IsKillable(t))) return;
        var path = QueriedPath;
        var version = _pathVersion;
        var confirm = MessageBox.Show(
            $"已查询路径：{path}\n\n将结束 {targets.Count} 个占用进程：\n\n• " +
            string.Join("\n• ", targets.Select(t => $"{t.ProcessName} (PID {t.ProcessId})")) +
            "\n\n这些进程里未保存的数据会丢失，应用或服务可能中断。确定继续吗？",
            "结束占用进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK || IsBusy || version != _pathVersion) return;

        IsBusy = true;
        SetError("");
        ResultText = $"正在结束 {targets.Count} 个进程…";
        try
        {
            var results = await Task.Run(() => targets.Select(t =>
                (t.ProcessName, Result: _service.Kill(t))).ToList());
            var failed = results.Count(r => !r.Result.Ok);
            var outcome = failed == 0
                ? $"结束请求已处理（{targets.Count} 个进程）"
                : $"结束请求已处理，{failed} 个失败";
            _status(outcome);
            if (version != _pathVersion) return;
            var failures = string.Join("\n", results.Where(r => !r.Result.Ok)
                .Select(r => $"{r.ProcessName}：{r.Result.Message}"));
            await LoadLockersAsync(path, version);
            if (version != _pathVersion) return;
            ResultText = $"{outcome}；{ResultText}";
            if (failed > 0) SetError(failures);
        }
        catch (Exception ex)
        {
            if (version == _pathVersion) SetError($"结束进程失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyPath(FileLocker? locker)
    {
        if (locker is null) return;
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(locker.ExecutablePath)
                ? $"{locker.ProcessName} (PID {locker.ProcessId})" : locker.ExecutablePath);
            _status("已复制进程路径");
        }
        catch (Exception ex) { SetError("复制失败：" + ex.Message); }
    }

    private void OpenFolder(FileLocker? locker)
    {
        if (locker is null || string.IsNullOrWhiteSpace(locker.ExecutablePath)) return;
        try { Native.RevealInExplorer(locker.ExecutablePath); }
        catch (Exception ex) { _status($"打开失败：{ex.Message}"); }
    }

    private void Clear()
    {
        PathText = "";
        Lockers.Clear();
        QueriedPath = "";
        OnPropertyChanged(nameof(HasLockers));
        ResultText = "请选择或粘贴路径，然后点击「查询占用」。";
        UpdateCommands();
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
