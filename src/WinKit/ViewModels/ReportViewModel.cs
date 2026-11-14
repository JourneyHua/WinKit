using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class ReportViewModel : ObservableObject, IModule
{
    private readonly HealthReportService _service = new();
    private readonly MaintenanceService _maintenance = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private readonly Action<string> _mainStatus;

    private bool _isBusy;
    private HealthReport? _report;
    private MaintenanceStatus? _maintenanceStatus;
    private string _progressText = "";

    public ReportViewModel(Action<string> status, Action<string> mainStatus, Action<bool>? setBusy = null)
    {
        _status = status;
        _mainStatus = mainStatus;
        _setBusy = setBusy;

        Sections = new ObservableCollection<ReportSection>();

        GenerateCommand = new RelayCommand(() => _ = GenerateAsync());
        ExportMarkdownCommand = new RelayCommand(() => Export(true));
        ExportJsonCommand = new RelayCommand(() => Export(false));
        OpenReportFolderCommand = new RelayCommand(OpenReportFolder);

        RegisterTaskCommand = new RelayCommand(RegisterTask);
        DeleteTaskCommand = new RelayCommand(DeleteTask);
        RunTaskNowCommand = new RelayCommand(RunTaskNow);
    }

    public ObservableCollection<ReportSection> Sections { get; }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand ExportMarkdownCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand OpenReportFolderCommand { get; }
    public RelayCommand RegisterTaskCommand { get; }
    public RelayCommand DeleteTaskCommand { get; }
    public RelayCommand RunTaskNowCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public HealthReport? Report
    {
        get => _report;
        private set
        {
            if (!Set(ref _report, value)) return;
            OnPropertyChanged(nameof(ScoreText));
            OnPropertyChanged(nameof(GradeText));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(GeneratedText));
            OnPropertyChanged(nameof(HasReport));
        }
    }

    public bool HasReport => _report is not null;
    public string ScoreText => _report is null ? "—" : $"{_report.Score}";
    public string GradeText => _report is null ? "" : $"{_report.Grade} 级";
    public string SummaryText => _report?.SummaryText ?? "还没有生成报告";
    public string GeneratedText => _report is null ? "" : $"生成于 {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}";

    public MaintenanceStatus? MaintenanceStatus
    {
        get => _maintenanceStatus;
        private set
        {
            if (!Set(ref _maintenanceStatus, value)) return;
            OnPropertyChanged(nameof(MaintenanceText));
            OnPropertyChanged(nameof(HasTask));
            OnPropertyChanged(nameof(LastOutcomeText));
        }
    }

    public bool HasTask => _maintenanceStatus?.TaskExists == true;

    public string MaintenanceText
    {
        get
        {
            var status = _maintenanceStatus;
            if (status is null) return "";
            if (!status.TaskExists) return "未注册定期维护任务";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(status.NextRunText)) parts.Add($"下次 {status.NextRunText}");
            if (!string.IsNullOrWhiteSpace(status.LastRunText)) parts.Add($"上次 {status.LastRunText}");
            if (!string.IsNullOrWhiteSpace(status.LastResultText)) parts.Add($"结果码 {status.LastResultText}");
            return parts.Count == 0 ? "已注册" : string.Join(" · ", parts);
        }
    }

    public string LastOutcomeText
    {
        get
        {
            var last = MaintenanceService.LastOutcomeText();
            return string.IsNullOrWhiteSpace(last) ? "" : $"上次维护：{last}";
        }
    }

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    public void Refresh() => _ = GenerateAsync();

    private async Task GenerateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Sections.Clear();

        var progress = new Progress<string>(text => ProgressText = text);

        try
        {
            var report = await Task.Run(() => _service.Build(text => ((IProgress<string>)progress).Report(text)));
            // 任务状态也要读 schtasks，一并放到后台，避免卡住界面
            var status = await Task.Run(() => _maintenance.LoadStatus());

            Report = report;
            foreach (var section in report.Sections) Sections.Add(section);
            MaintenanceStatus = status;

            ProgressText = "";
            _status($"体检完成：{report.Score}/100（{report.Grade} 级），{report.SummaryText}");

            if (report.BadCount > 0)
                _mainStatus($"体检发现 {report.BadCount} 项异常，详见体检与维护页");
        }
        catch (Exception ex)
        {
            ProgressText = "";
            _status($"生成体检报告失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Export(bool markdown)
    {
        if (_report is null)
        {
            MessageBox.Show("请先生成体检报告。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(HealthReportService.ReportDirectory);
            var ext = markdown ? ".md" : ".json";
            var path = Path.Combine(HealthReportService.ReportDirectory,
                $"report-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");

            var content = markdown
                ? HealthReportService.ToMarkdown(_report)
                : HealthReportService.ToJson(_report);

            File.WriteAllText(path, content, new System.Text.UTF8Encoding(true));
            _status($"已导出：{path}");

            MessageBox.Show($"报告已保存到：\n{path}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenReportFolder()
    {
        try
        {
            Directory.CreateDirectory(HealthReportService.ReportDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = HealthReportService.ReportDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开目录失败：{ex.Message}");
        }
    }

    private void RegisterTask()
    {
        var confirm = MessageBox.Show(
            "将注册一个 Windows 计划任务：\n\n" +
            $"名称：{MaintenanceService.TaskName}\n" +
            "时间：每周日 03:00\n" +
            "动作：安全清理临时文件 + 生成体检报告\n\n" +
            "不安装后台服务，也不常驻；任务可在本页随时一键删除（删除前会导出任务 XML 备份）。确定继续吗？",
            "注册定期维护任务", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = _maintenance.Register();
        _status(message);

        if (!ok)
        {
            MessageBox.Show(message, "注册失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(message, "已注册", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        MaintenanceStatus = _maintenance.LoadStatus();
    }

    private void DeleteTask()
    {
        if (_maintenanceStatus?.TaskExists != true) return;

        var confirm = MessageBox.Show(
            $"确定删除计划任务「{MaintenanceService.TaskName}」吗？\n\n" +
            "删除前会先把任务 XML 导出到 %LOCALAPPDATA%\\WinKit\\TaskBackups，需要时可以再导入。",
            "删除定期维护任务", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = _maintenance.Delete();
        _status(message);

        if (!ok) MessageBox.Show(message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);

        MaintenanceStatus = _maintenance.LoadStatus();
    }

    private void RunTaskNow()
    {
        if (_maintenanceStatus?.TaskExists != true)
        {
            MessageBox.Show("请先注册定期维护任务。", "WinKit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = _maintenance.RunNow();
        _status(message);

        if (!ok)
        {
            MessageBox.Show(message, "触发失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            "已触发。任务在后台跑完（约 10~60 秒）后，日志会写到：\n" +
            MaintenanceService.LogPath + "\n\n稍后回到本页刷新即可看到上次结果。",
            "已触发", MessageBoxButton.OK, MessageBoxImage.Information);
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
