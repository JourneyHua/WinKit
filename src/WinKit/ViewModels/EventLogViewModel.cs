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
    private string _loadingText = "";
    private EventLogGroup? _selected;

    public EventLogViewModel(Action<string> status)
    {
        _status = status;
        Groups = new ObservableCollection<EventLogGroup>();
        Filtered = new ObservableCollection<EventLogGroup>();

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
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
        private set
        {
            if (Set(ref _isBusy, value)) RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string LoadingText
    {
        get => _loadingText;
        private set => Set(ref _loadingText, value);
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
        var changed = days != _days;
        _days = days;
        // 点按已选中的范围也保持选中。
        OnPropertyChanged(nameof(Is7Days));
        OnPropertyChanged(nameof(Is30Days));
        if (!changed) return;

        if (IsBusy) LoadingText = $"将读取近 {days} 天的事件…";
        else Refresh();
    }

    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = "";

        try
        {
            while (true)
            {
                var days = _days;
                LoadingText = $"正在读取近 {days} 天的事件…";
                try
                {
                    var result = await Task.Run(() => _service.Read(days));
                    // 不将旧查询的结果贴上新范围标签；合并快速切换，仅读取最后选中的范围。
                    if (days != _days) continue;
                    ShowResult(days, result);
                }
                catch (Exception ex)
                {
                    if (days != _days) continue;
                    ShowReadFailure(days, ex.Message);
                }
                break;
            }
        }
        finally
        {
            LoadingText = "";
            IsBusy = false;
        }
    }

    private void ShowResult(int days, EventLogReadResult result)
    {
        if (result.SuccessfulLogs == 0)
        {
            ShowReadFailure(days, result.Error);
            return;
        }

        SelectedGroup = null;
        Groups.Clear();
        Filtered.Clear();
        foreach (var group in result.Groups)
        {
            Groups.Add(group);
            Filtered.Add(group);
        }

        var partial = result.Error is not null;
        ErrorText = partial ? $"部分日志未能读取：{result.Error}" : "";
        var groups = result.Groups;
        var scope = partial || result.LimitedLogs > 0 ? $"近 {days} 天已读取的日志" : $"近 {days} 天";
        SummaryText = groups.Count == 0
            ? $"{scope}中未发现错误或严重事件。"
            : $"{scope}中发现 {groups.Sum(g => g.Count)} 条错误或严重事件，归并为 {groups.Count} 组。";
        if (result.LimitedLogs > 0)
            SummaryText += $" {result.LimitedLogs} 个日志已达到读取上限（每个最多 {result.MaxPerLog} 条），仅显示最近记录。";
        _status(partial ? "事件日志：部分日志未能读取" : "事件日志已更新");
    }

    private void ShowReadFailure(int days, string? error)
    {
        var preserved = string.IsNullOrEmpty(SummaryText) ? "" : "，下方仍显示上一次结果";
        ErrorText = $"未能读取近 {days} 天的事件{preserved}。{error}";
        _status("事件日志读取失败");
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
            Native.OpenPath("eventvwr.msc");
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
