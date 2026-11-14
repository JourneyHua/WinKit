using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class WindowsUpdateViewModel : ObservableObject, IModule
{
    private readonly WindowsUpdateService _service;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private bool _isBusy;
    private string _stateText = "";
    private string _noteText = "";

    public WindowsUpdateViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new WindowsUpdateService(journal);
        _status = status;
        _setBusy = setBusy;

        Installed = new ObservableCollection<WindowsUpdateInfo>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        Pause7Command = new RelayCommand(() => Pause(7));
        Pause14Command = new RelayCommand(() => Pause(14));
        Pause35Command = new RelayCommand(() => Pause(35));
        ResumeCommand = new RelayCommand(Resume);
        CleanCacheCommand = new RelayCommand(() => _ = CleanCacheAsync());
        RestartServiceCommand = new RelayCommand(() => _ = RestartServiceAsync());
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenCacheFolderCommand = new RelayCommand(OpenCacheFolder);
    }

    public ObservableCollection<WindowsUpdateInfo> Installed { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand Pause7Command { get; }
    public RelayCommand Pause14Command { get; }
    public RelayCommand Pause35Command { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CleanCacheCommand { get; }
    public RelayCommand RestartServiceCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenCacheFolderCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public string NoteText
    {
        get => _noteText;
        private set
        {
            if (Set(ref _noteText, value)) OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_noteText);

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            var state = await Task.Run(() => _service.LoadState());
            var parts = new List<string>
            {
                state.PausedText,
                $"服务 {state.ServiceStateText}" + (state.ServiceRunning ? "（运行中）" : "（未运行）"),
                $"下载缓存 {state.UpdateCacheText}"
            };

            if (_service.PendingReboot()) parts.Add("待重启生效");
            StateText = string.Join(" · ", parts);

            var note = _service.PolicyNote();
            NoteText = note ?? state.Note;
            OnPropertyChanged(nameof(HasNote));

            _status("更新状态已刷新");

            var (installed, error) = await Task.Run(() => _service.LoadInstalled(40));
            Installed.Clear();
            foreach (var item in installed) Installed.Add(item);

            if (error is not null)
            {
                NoteText = error;
                OnPropertyChanged(nameof(HasNote));
            }
        }
        catch (Exception ex)
        {
            _status($"读取更新信息失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Pause(int days)
    {
        var confirm = MessageBox.Show(
            $"将暂停 Windows 更新 {days} 天（系统允许的上限是 35 天）。\n\n" +
            "暂停期间不会下载或安装功能更新，安全补丁也会一并暂停。确定继续吗？",
            "暂停 Windows 更新", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            _service.Pause(days);
            _status($"已暂停 Windows 更新 {days} 天");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"暂停失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Resume()
    {
        try
        {
            _service.Resume();
            _status("已恢复 Windows 更新");
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"恢复失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CleanCacheAsync()
    {
        if (IsBusy) return;

        var confirm = MessageBox.Show(
            "将清空 Windows 更新的下载缓存（C:\\Windows\\SoftwareDistribution\\Download）。\n\n" +
            "过程中会临时停止再启动 wuauserv 服务；已安装的补丁不受影响，正在下载的更新需要重新下载。确定继续吗？",
            "清理更新下载缓存", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (freed, count, error) = await Task.Run(() => _service.CleanCache(_status.Invoke));
            var freedText = ThisPcService.FormatSize(freed);

            if (error is not null)
            {
                _status($"清理完成（有提示）：{error}");
                MessageBox.Show($"清理了 {count} 个文件，释放 {freedText}。\n\n提示：{error}",
                    "清理结果", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _status($"更新缓存清理完成：{count} 个文件，释放 {freedText}");
                MessageBox.Show($"清理完成！\n\n删除文件：{count} 个\n释放空间：{freedText}",
                    "清理完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清理失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
    }

    private async Task RestartServiceAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        _status("正在重启 Windows 更新服务…");

        try
        {
            // sc stop 会等 service 真正停下，可能在 UI 线程阻塞几十秒，必须放后台
            var (stopOk, stopMsg) = await Task.Run(() => _service.RunService("wuauserv", "stop"));
            var (startOk, startMsg) = await Task.Run(() => _service.RunService("wuauserv", "start"));

            if (startOk)
            {
                _status("已重启 Windows 更新服务");
            }
            else
            {
                _status($"重启服务失败：{startMsg}");
                MessageBox.Show(
                    $"重启 Windows 更新服务失败：\n\n{startMsg}\n\n停止阶段：{(stopOk ? "成功" : stopMsg)}",
                    "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重启服务失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
    }

    private void OpenSettings()
        => Open("ms-settings:windowsupdate");

    private void OpenCacheFolder()
        => Open(@"C:\Windows\SoftwareDistribution\Download");

    private void Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
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
