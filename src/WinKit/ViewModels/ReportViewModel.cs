using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class ReportViewModel : ObservableObject, IModule
{
    private readonly HealthReportService _service = new();
    private readonly MaintenanceService _maintenance = new();
    private readonly Func<Action<string>, HealthReport> _buildReport;
    private readonly Func<MaintenanceStatus> _loadMaintenanceStatus;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private HealthReport? _report;
    private MaintenanceStatus? _maintenanceStatus;
    private bool _generating;
    private bool _saving;
    private bool _readingTask;
    private bool _changingTask;
    private bool _waitingForRun;
    private string _progressText = "";
    private string _feedbackText = "";
    private string _maintenanceProgress = "";
    private string _errorText = "";
    private bool _hasReportFolder;

    public ReportViewModel(Action<string> status, Action<bool>? setBusy = null)
        : this(status, setBusy, null, null)
    {
    }

    internal ReportViewModel(Action<string> status, Action<bool>? setBusy,
        Func<Action<string>, HealthReport>? buildReport, Func<MaintenanceStatus>? loadMaintenanceStatus)
    {
        _status = status;
        _setBusy = setBusy;
        _buildReport = buildReport ?? (progress => _service.Build(progress));
        _loadMaintenanceStatus = loadMaintenanceStatus ?? _maintenance.LoadStatus;
        GenerateCommand = new RelayCommand(Regenerate, () => !_generating && !_saving);
        ExportMarkdownCommand = new RelayCommand(() => _ = ExportAsync(true), CanSave);
        ExportJsonCommand = new RelayCommand(() => _ = ExportAsync(false), CanSave);
        OpenReportFolderCommand = new RelayCommand(OpenReportFolder, () => HasReportFolder);
        RegisterTaskCommand = new RelayCommand(() => _ = RegisterTaskAsync(), () => ShowEnableTask && !TaskBusy);
        DeleteTaskCommand = new RelayCommand(() => _ = DeleteTaskAsync(), () => ShowDisableTask && !TaskBusy);
        RunTaskNowCommand = new RelayCommand(() => _ = RunTaskNowAsync(), () => ShowRunTask && !TaskBusy);
        RefreshTaskCommand = new RelayCommand(() => _ = RefreshMaintenanceAsync(), () => !TaskBusy);
    }

    public ObservableCollection<ReportSection> Sections { get; } = new();
    public RelayCommand GenerateCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand OpenReportFolderCommand { get; }
    public RelayCommand RegisterTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand RunTaskNowCommand { get; }
    public RelayCommand RefreshTaskCommand { get; }

    public bool IsBusy => _generating || _saving || _readingTask || _changingTask;
    private bool TaskBusy => _readingTask || _changingTask || _waitingForRun;
    public bool HasReport => _report is not null;
    public bool HasReportFolder
    {
        get => _hasReportFolder;
        private set
        {
            if (Set(ref _hasReportFolder, value)) OpenReportFolderCommand.RaiseCanExecuteChanged();
        }
    }
    public string GenerateText => HasReport ? "重新体检" : "开始体检";
    public string ScoreText => _report is null ? "—" : $"{_report.Score}";
    public string GradeText => _report is null ? "" : $"{_report.Grade} 级";
    public string SummaryText => _report?.SummaryText ?? "还没有生成报告";
    public string GeneratedText => _report is null ? "" : $"生成于 {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

    private bool NeedsTaskEnabled => _maintenanceStatus is { Known: true, IsEnabled: false, IsRunning: false };
    private bool IsElevated { get; } = Native.IsElevated();
    public bool ShowEnableTask => NeedsTaskEnabled && IsElevated;
    public bool ShowElevateForTask => NeedsTaskEnabled && !IsElevated;
    public bool ShowRunTask => _maintenanceStatus is { Known: true, IsEnabled: true, IsRunning: false } && !_waitingForRun;
    public bool ShowDisableTask => ShowRunTask && IsElevated;
    public bool ShowElevateToDisable => !IsElevated && (ShowRunTask || _maintenanceStatus is { AccessDenied: true });
    public bool ShowRefreshTask => _maintenanceStatus is { Known: false } || _maintenanceStatus is { IsRunning: true } && !_waitingForRun;

    public string MaintenanceText
    {
        get
        {
            var status = _maintenanceStatus;
            if (status is null) return "正在读取维护安排…";
            if (!status.Known) return status.Note;
            if (_waitingForRun || status.IsRunning) return "维护正在后台运行，可继续使用 WinKit。";
            if (!status.TaskExists) return "每周维护未启用。";
            if (!status.IsEnabled) return "每周维护已在 Windows 中停用。";
            return string.IsNullOrEmpty(status.NextRunText) ? "每周维护已启用。" : $"下次维护：{status.NextRunText}";
        }
    }

    public string LastOutcomeText => _maintenanceStatus switch
    {
        { LastRunAt: not null } status => $"上次运行：{status.LastRunText} · {status.LastResultText}",
        { LastOutcome.Length: > 0 } status => $"上次维护{status.LastOutcome}",
        _ => ""
    };

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public string FeedbackText
    {
        get => _feedbackText;
        private set => Set(ref _feedbackText, value);
    }

    public string MaintenanceProgress
    {
        get => _maintenanceProgress;
        private set => Set(ref _maintenanceProgress, value);
    }

    public void Refresh()
    {
        // 导航复用当前会话的报告和生成时间；首次读取仍在后台完成。
        // 未知维护状态也保留，交给显式刷新重试，避免反复进页查询任务计划程序。
        if (_report is null) _ = GenerateAsync();
        if (_maintenanceStatus is null) _ = RefreshMaintenanceAsync();
    }

    private void Regenerate()
    {
        // “重新体检”始终重新采集；不把会话快照作为最新系统状态。
        _ = GenerateAsync();
        _ = RefreshMaintenanceAsync();
    }

    private async Task GenerateAsync()
    {
        if (_generating || _saving) return;
        _generating = true;
        ErrorText = "";
        ProgressText = "正在体检…";
        NotifyCommands();
        var progress = new Progress<string>(text => { if (_generating) ProgressText = text; });

        try
        {
            var report = await Task.Run(() => _buildReport(((IProgress<string>)progress).Report));
            _report = report;
            Sections.Clear();
            foreach (var section in report.Sections) Sections.Add(section);
            foreach (var property in new[] { nameof(HasReport), nameof(GenerateText), nameof(ScoreText), nameof(GradeText), nameof(SummaryText), nameof(GeneratedText) })
                OnPropertyChanged(property);
        }
        catch (Exception ex)
        {
            SetError($"未能生成体检报告。{ex.Message}");
        }
        finally
        {
            _generating = false;
            ProgressText = "";
            NotifyCommands();
        }
    }

    private bool CanSave() => HasReport && !_saving && !_generating;

    private async Task ExportAsync(bool markdown)
    {
        if (!CanSave() || _report is not { } report) return;
        _saving = true;
        ErrorText = "";
        FeedbackText = "正在保存报告…";
        NotifyCommands();

        try
        {
            var path = await Task.Run(() =>
            {
                Directory.CreateDirectory(HealthReportService.ReportDirectory);
                var ext = markdown ? ".md" : ".json";
                // 临时重名时也不覆盖同一秒内保存的另一份报告。
                var output = Path.Combine(HealthReportService.ReportDirectory,
                    $"report-{DateTime.Now:yyyyMMdd-HHmmss-fff}{ext}");
                var content = markdown ? HealthReportService.ToMarkdown(report) : HealthReportService.ToJson(report);
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(file, new UTF8Encoding(true));
                writer.Write(content);
                return output;
            });
            HasReportFolder = true;
            FeedbackText = $"已保存报告：{Path.GetFileName(path)}";
            _status(FeedbackText);
        }
        catch (Exception ex)
        {
            FeedbackText = "";
            SetError($"未能保存报告。{ex.Message}");
        }
        finally
        {
            _saving = false;
            NotifyCommands();
        }
    }

    private void OpenReportFolder()
    {
        if (!Native.OpenPath(HealthReportService.ReportDirectory))
        {
            HasReportFolder = Directory.Exists(HealthReportService.ReportDirectory);
            SetError("无法打开报告文件夹。");
        }
    }

    private async Task RefreshMaintenanceAsync()
    {
        if (TaskBusy) return;
        _readingTask = true;
        NotifyTaskState();
        try
        {
            await ReadTaskStateAsync();
            HasReportFolder = Directory.Exists(HealthReportService.ReportDirectory);
        }
        finally
        {
            _readingTask = false;
            NotifyTaskState();
        }
        if (_maintenanceStatus is { IsRunning: true } status) _ = ObserveRunAsync(status.LastRunAt, true);
    }

    private async Task<MaintenanceStatus> ReadTaskStateAsync() =>
        _maintenanceStatus = await Task.Run(_loadMaintenanceStatus);

    private async Task RegisterTaskAsync()
    {
        if (!ShowEnableTask || TaskBusy) return;
        var resumeExisting = _maintenanceStatus?.TaskExists == true;
        var confirm = MessageBox.Show(
            resumeExisting
                ? "按 Windows 中已有的时间安排恢复维护？WinKit 会先确认任务仍指向当前程序。"
                : "Windows 将在每周日 03:00 清理过期的临时文件和缓存，并保存体检报告。不会清空回收站；你可以随时关闭。",
            "启用每周维护？", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        Func<(bool Ok, string Message)> action = resumeExisting ? _maintenance.Enable : () => _maintenance.Register();
        await RunTaskActionAsync("正在启用每周维护…", action);
    }

    private async Task DeleteTaskAsync()
    {
        if (!ShowDisableTask || TaskBusy) return;
        var confirm = MessageBox.Show("WinKit 会先备份当前安排，再取消后续的每周维护。已生成的报告不会删除。",
            "关闭每周维护？", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm == MessageBoxResult.OK) await RunTaskActionAsync("正在备份并关闭每周维护…", _maintenance.Delete);
    }

    private async Task RunTaskNowAsync()
    {
        if (!ShowRunTask || TaskBusy) return;
        var confirm = MessageBox.Show("现在清理过期的临时文件和缓存并保存体检报告？清理无法撤销，不会清空回收站。",
            "立即维护？", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        var previousRun = _maintenanceStatus?.LastRunAt;
        if (await RunTaskActionAsync("正在请求立即维护…", _maintenance.RunNow))
            _ = ObserveRunAsync(previousRun, _maintenanceStatus is { IsRunning: true });
    }

    private async Task<bool> RunTaskActionAsync(string progress, Func<(bool Ok, string Message)> action)
    {
        if (TaskBusy) return false;
        _changingTask = true;
        _setBusy?.Invoke(true);
        MaintenanceProgress = progress;
        ErrorText = "";
        NotifyTaskState();
        try
        {
            var result = await Task.Run(action);
            MaintenanceProgress = result.Ok ? result.Message : "";
            if (!result.Ok) SetError(result.Message);
            return result.Ok;
        }
        catch (Exception ex)
        {
            MaintenanceProgress = "";
            SetError($"维护设置未能完成。{ex.Message}");
            return false;
        }
        finally
        {
            try { await ReadTaskStateAsync(); }
            finally
            {
                _changingTask = false;
                NotifyTaskState();
                _setBusy?.Invoke(false);
            }
        }
    }

    private async Task ObserveRunAsync(DateTime? previousRun, bool sawRunning)
    {
        if (_waitingForRun) return;
        _waitingForRun = true;
        NotifyTaskState();
        try
        {
            // 任务被 Windows 调起后才独立运行；不锁导航，不把“已请求”当作“已完成”。
            for (var i = 0; i < 60; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var status = await ReadTaskStateAsync();
                NotifyTaskState();
                if (!status.Known || !status.TaskExists) break;
                if (status.IsRunning) sawRunning = true;
                else if (sawRunning || status.LastRunAt != previousRun)
                {
                    HasReportFolder = Directory.Exists(HealthReportService.ReportDirectory);
                    MaintenanceProgress = string.IsNullOrWhiteSpace(status.LastResultText) ? "维护已结束。"
                        : $"维护{status.LastResultText}。";
                    return;
                }
            }
            MaintenanceProgress = "暂未取得维护结果。";
        }
        finally
        {
            _waitingForRun = false;
            NotifyTaskState();
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsBusy));
        GenerateCommand.RaiseCanExecuteChanged();
        ExportMarkdownCommand.RaiseCanExecuteChanged();
        ExportJsonCommand.RaiseCanExecuteChanged();
        OpenReportFolderCommand.RaiseCanExecuteChanged();
    }

    private void NotifyTaskState()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(MaintenanceText), nameof(LastOutcomeText), nameof(ShowEnableTask),
                     nameof(ShowElevateForTask), nameof(ShowElevateToDisable), nameof(ShowRunTask), nameof(ShowDisableTask), nameof(ShowRefreshTask) })
            OnPropertyChanged(property);
        RegisterTaskCommand.RaiseCanExecuteChanged();
        DeleteTaskCommand.RaiseCanExecuteChanged();
        RunTaskNowCommand.RaiseCanExecuteChanged();
        RefreshTaskCommand.RaiseCanExecuteChanged();
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
    public void SetError(string? message) => ErrorText = message ?? "";
}
