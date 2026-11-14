using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class EventLogViewModel : ObservableObject, IModule
{
    private readonly EventLogService _service = new();
    private readonly Action<string> _status;
    private int _days = 7;
    private bool _isBusy;
    private string _summary = "";
    private string _errorText = "";
    private EventLogGroup? _selected;

    public EventLogViewModel(Action<string> status)
    {
        _status = status;
        Groups = new ObservableCollection<EventLogGroup>();
        Filtered = new ObservableCollection<EventLogGroup>();

        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        SetDaysCommand = new RelayCommand(p => SetDays(p));
        CopyCommand = new RelayCommand(p => Copy(p as EventLogGroup));
        OpenEventViewerCommand = new RelayCommand(OpenEventViewer);
    }

    public ObservableCollection<EventLogGroup> Groups { get; }
    public ObservableCollection<EventLogGroup> Filtered { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SetDaysCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenEventViewerCommand { get; }

    public EventLogGroup? SelectedGroup
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool Is7Days => _days == 7;
    public bool Is30Days => _days == 30;

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

    public void Refresh() => _ = LoadAsync();

    private void SetDays(object? parameter)
    {
        var days = parameter?.ToString() == "30" ? 30 : 7;
        if (days == _days) return;

        _days = days;
        OnPropertyChanged(nameof(Is7Days));
        OnPropertyChanged(nameof(Is30Days));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status($"正在读取近 {_days} 天的系统事件…");
            var (groups, error) = await Task.Run(() => _service.Load(_days));

            Groups.Clear();
            foreach (var group in groups) Groups.Add(group);

            Filtered.Clear();
            foreach (var group in groups) Filtered.Add(group);

            ErrorText = error ?? "";

            var blueScreen = groups.Count(g => g.Category == "蓝屏 / 意外关机");
            var crashes = groups.Count(g => g.Category == "应用崩溃");
            SummaryText = groups.Count == 0
                ? $"近 {_days} 天没有错误或严重级别事件"
                : $"近 {_days} 天共 {groups.Sum(g => g.Count)} 条错误事件，归并为 {groups.Count} 种（蓝屏相关 {blueScreen} 种，应用崩溃 {crashes} 种）";

            _status($"事件日志：{groups.Count} 种错误");
        }
        catch (Exception ex)
        {
            ErrorText = $"读取事件日志失败：{ex.Message}";
            _status(ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Copy(EventLogGroup? group)
    {
        if (group is null) return;

        var text = $"""
            来源：{group.Provider}
            事件 ID：{group.EventId}（{group.LevelText}）
            次数：{group.Count} 次（{group.FirstText} ~ {group.LastText}）
            日志：{group.LogName}
            示例：
            {group.Sample}
            """;

        try
        {
            Clipboard.SetText(text);
            _status("已复制事件详情");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void OpenEventViewer()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "eventvwr.msc",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
