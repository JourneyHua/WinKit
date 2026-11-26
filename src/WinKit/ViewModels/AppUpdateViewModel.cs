using System.IO;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

/// <summary>更新的网络工作不占模块忙碌锁；只有用户主动重启才进入退出流程。</summary>
public sealed class AppUpdateViewModel : ObservableObject, IDisposable
{
    private readonly AppUpdateService _service;
    private readonly Func<bool> _isAppBusy;
    private readonly Func<bool> _hasPendingDrafts;
    private readonly Func<AppUpdateDownload, CancellationToken, Task<bool>> _prepareRestart;
    private readonly Action _closeAfterReady;
    private readonly Version _currentVersion;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private AppUpdateRelease? _release;
    private AppUpdateDownload? _download;
    private bool _isPanelOpen;
    private bool _isChecking;
    private bool _isDownloading;
    private bool _isPreparing;
    private bool _closed;
    private bool _isRestartCommitted;
    private string _dismissedVersion = "";
    private string _statusText = "仅检查正式稳定版；点击下载后才传输更新文件。";
    private string _errorText = "";
    private double _downloadPercent;
    private string _progressText = "";
    private bool _automaticStarted;
    private bool _automaticChecksEnabled = true;

    public AppUpdateViewModel(AppUpdateService service, Func<bool> isAppBusy,
        Func<bool> hasPendingDrafts,
        Func<AppUpdateDownload, CancellationToken, Task<bool>> prepareRestart,
        Action closeAfterReady, Version? currentVersion = null)
    {
        _service = service;
        try { _automaticChecksEnabled = service.AutomaticChecksEnabled; } catch { /* 后台检查会安全跳过无法读取的偏好。 */ }
        _isAppBusy = isAppBusy;
        _hasPendingDrafts = hasPendingDrafts;
        _prepareRestart = prepareRestart;
        _closeAfterReady = closeAfterReady;
        var version = currentVersion ?? typeof(AppUpdateViewModel).Assembly.GetName().Version ?? new Version(1, 0, 0);
        _currentVersion = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        CheckCommand = new RelayCommand(() => _ = CheckAsync(true), () => !IsWorking);
        DownloadCommand = new RelayCommand(() => _ = DownloadAsync(), () => _release is not null && _download is null && !IsWorking);
        RestartCommand = new RelayCommand(() => _ = RestartAsync(), () => _download is not null && !IsWorking && !_isAppBusy());
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => _isChecking || _isDownloading);
        ShowCommand = new RelayCommand(() => IsPanelOpen = true);
        EntryCommand = new RelayCommand(() => { IsPanelOpen = true; if (_release is null && !IsWorking) _ = CheckAsync(true); }, () => !_isPreparing);
        DismissCommand = new RelayCommand(() => { _dismissedVersion = _release?.VersionText ?? ""; IsPanelOpen = false; NotifyState(); }, () => !_isPreparing);
        OpenDownloadFolderCommand = new RelayCommand(() => Native.OpenPath(_service.DownloadDirectory), () => Directory.Exists(_service.DownloadDirectory));
    }

    public RelayCommand CheckCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ShowCommand { get; }
    public RelayCommand EntryCommand { get; }
    public bool IsRestartCommitted => _isRestartCommitted;
    public void CancelPendingRestart() { if (_isPreparing && !_isRestartCommitted) _operation?.Cancel(); }
    public void ShowRestartNotice(AppUpdateInstallNotice notice)
    {
        IsPanelOpen = true;
        StatusText = notice.Message;
        ErrorText = notice.Updated ? "" : notice.Message;
    }
    public RelayCommand DismissCommand { get; }
    public RelayCommand OpenDownloadFolderCommand { get; }
    public AppUpdateRelease? AvailableRelease => _release;
    public bool HasUpdate => _release is not null;
    public bool IsVisible => IsPanelOpen || (_release is not null && _dismissedVersion != _release.VersionText);
    public bool IsWorking => _isChecking || _isDownloading || _isPreparing;
    public bool IsChecking => _isChecking;
    public bool IsDownloading => _isDownloading;
    public bool IsPreparing => _isPreparing;
    public bool IsReady => _download is not null;
    public bool CanDownload => _release is not null && _download is null && !IsWorking;
    public bool CanCancel => _isChecking || _isDownloading;
    public bool IsPanelOpen { get => _isPanelOpen; private set { if (Set(ref _isPanelOpen, value)) NotifyState(); } }
    public bool AutomaticChecksEnabled
    {
        get => _automaticChecksEnabled;
        set
        {
            try { _service.AutomaticChecksEnabled = value; _automaticChecksEnabled = value; OnPropertyChanged(); }
            catch (Exception ex) { ErrorText = "无法保存更新偏好：" + ex.Message; IsPanelOpen = true; }
        }
    }
    public string Title => _release is null ? "WinKit 更新" : $"WinKit {_release.VersionText} 可更新";
    public string CurrentVersionText => $"当前版本 v{_currentVersion}";
    public string ReleaseNotes => string.IsNullOrWhiteSpace(_release?.ReleaseNotes) ? "此版本未提供更新说明。" : _release!.ReleaseNotes;
    public string ReleaseSummary => _release is null ? "Windows 11 · x64 · 正式稳定版" : $"{_release.PublishedAt.ToLocalTime():yyyy-MM-dd} · Windows 11 x64 · {FormatSize(_release.Size)}";
    public string EntryText => _isChecking ? "正在检查更新…" : _isDownloading ? $"下载更新 {_downloadPercent:0}%" : _isPreparing ? "准备重启更新…" : _download is not null ? "更新已就绪" : _release is not null ? $"新版 {_release.VersionText}" : "检查 WinKit 更新";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public double DownloadPercent { get => _downloadPercent; private set => Set(ref _downloadPercent, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public bool ShowDraftWarning => _download is not null && _hasPendingDrafts();
    public string RestartHint => _isAppBusy() ? "当前操作完成后可重启更新" : "重启将关闭当前窗口；未应用的更改不会保留";

    public void NotifyHostStateChanged()
    {
        RestartCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ShowDraftWarning));
        OnPropertyChanged(nameof(RestartHint));
    }

    public void StartAutomaticChecks()
    {
        if (_automaticStarted || _closed) return;
        _automaticStarted = true;
        _ = AutomaticLoopAsync();
    }

    private async Task AutomaticLoopAsync()
    {
        var token = _lifetime.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            while (!token.IsCancellationRequested)
            {
                await CheckAsync(false);
                await Task.Delay(TimeSpan.FromHours(1), token);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task CheckAsync(bool manual)
    {
        if (_closed || IsWorking) return;
        if (!manual)
        {
            try { if (!AutomaticChecksEnabled || !_service.IsAutomaticCheckDue()) return; }
            catch { return; }
        }
        if (manual) IsPanelOpen = true;
        ErrorText = "";
        _isChecking = true;
        if (manual) StatusText = "正在检查 WinKit 正式版…";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        NotifyState();
        try
        {
            var release = await _service.CheckAsync(_currentVersion, manual, _operation.Token);
            if (_closed) return;
            if (release is null)
            {
                if (manual) StatusText = $"当前 v{_currentVersion} 已是最新正式版。";
                _release = null;
                _download = null;
            }
            else
            {
                if (_release?.Version != release.Version || !string.Equals(_release.Sha256, release.Sha256, StringComparison.OrdinalIgnoreCase)) _download = null;
                _release = release;
                StatusText = _download is null ? "有新版可用。查看说明后，点击下载更新。" : "下载已完成并通过校验，可在方便时重启更新。";
            }
        }
        catch (OperationCanceledException) { if (manual && !_closed) StatusText = "已取消检查，可随时重试。"; }
        catch (Exception ex) { if (manual && !_closed) { ErrorText = "检查失败：" + ex.Message; StatusText = "未更改当前程序，可点击检查重试。"; } }
        finally { _isChecking = false; EndOperation(); }
    }

    internal async Task DownloadAsync()
    {
        if (_closed || !CanDownload || _release is not { } release) return;
        IsPanelOpen = true;
        ErrorText = "";
        _isDownloading = true;
        DownloadPercent = 0;
        StatusText = "正在下载新版；你可以继续使用工具。";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        NotifyState();
        try
        {
            var progress = new Progress<AppUpdateProgress>(value =>
            {
                if (_closed || !_isDownloading) return;
                DownloadPercent = value.TotalBytes > 0 ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes, 0, 100) : 0;
                ProgressText = $"{FormatSize(value.BytesReceived)} / {FormatSize(value.TotalBytes)}";
                OnPropertyChanged(nameof(EntryText));
            });
            _download = await _service.DownloadAsync(release, progress, _operation.Token);
            if (_closed) return;
            DownloadPercent = 100;
            StatusText = "下载已完成并通过校验。点击重启并更新时才替换当前程序。";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText = "已取消下载。当前程序未更改，可重新下载。"; }
        catch (Exception ex) { if (!_closed) { ErrorText = "下载失败：" + ex.Message; StatusText = "当前程序未更改，可重新下载。"; } }
        finally { _isDownloading = false; EndOperation(); }
    }

    internal async Task RestartAsync()
    {
        if (_closed || _download is not { } download || IsWorking || _isAppBusy()) return;
        ErrorText = "";
        _isPreparing = true;
        StatusText = "正在准备更新，当前窗口会在助手就绪后关闭…";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        NotifyState();
        try
        {
            if (!await _service.ValidateDownloadAsync(download, _operation.Token))
            {
                _download = null;
                throw new IOException("下载文件已变化，请重新下载。");
            }
            if (_isAppBusy()) throw new InvalidOperationException("请等待当前操作完成后再重启更新。");
            if (await _prepareRestart(download, _operation.Token))
            {
                _isRestartCommitted = true;
                _closeAfterReady();
            }
            else StatusText = "更新尚未启动，当前窗口保持打开。可重试或打开下载目录。";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText = "已取消重启更新，当前程序保持不变。"; }
        catch (Exception ex) { if (!_closed) { ErrorText = "无法开始更新：" + ex.Message; StatusText = "当前窗口保持打开，可重试或打开下载目录。"; } }
        finally { _isPreparing = false; EndOperation(); }
    }

    private void EndOperation()
    {
        _operation?.Dispose();
        _operation = null;
        NotifyState();
    }

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(HasUpdate), nameof(IsVisible), nameof(IsWorking), nameof(IsChecking), nameof(IsDownloading), nameof(IsPreparing), nameof(IsReady), nameof(CanDownload), nameof(CanCancel), nameof(Title), nameof(CurrentVersionText), nameof(ReleaseNotes), nameof(ReleaseSummary), nameof(EntryText), nameof(ShowDraftWarning), nameof(RestartHint) }) OnPropertyChanged(property);
        EntryCommand.RaiseCanExecuteChanged(); CheckCommand.RaiseCanExecuteChanged(); DownloadCommand.RaiseCanExecuteChanged(); RestartCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); DismissCommand.RaiseCanExecuteChanged(); OpenDownloadFolderCommand.RaiseCanExecuteChanged();
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(0, bytes) / 1024d:0.0} KB";

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _lifetime.Cancel();
        _operation?.Cancel();
        _service.Dispose();
        _lifetime.Dispose();
    }
}
