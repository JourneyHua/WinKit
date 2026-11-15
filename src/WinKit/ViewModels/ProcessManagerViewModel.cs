using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class ProcessManagerViewModel : ObservableObject, IModule
{
    private readonly ProcessService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private readonly List<ProcessItem> _all = new();

    private bool _isBusy;
    private string _filter = "";
    private string _summary = "";
    private string _errorText = "";
    private ProcessItem? _selectedItem;

    public ProcessManagerViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;
        Items = new ObservableCollection<ProcessItem>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        KillCommand = new RelayCommand(p => Kill(p as ProcessItem, wholeTree: false));
        KillTreeCommand = new RelayCommand(p => Kill(p as ProcessItem, wholeTree: true));
        OpenLocationCommand = new RelayCommand(p => OpenLocation(p as ProcessItem));
        CopyCommand = new RelayCommand(p => Copy(p as ProcessItem));
    }

    public ObservableCollection<ProcessItem> Items { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand KillCommand { get; }
    public RelayCommand KillTreeCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand CopyCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                _setBusy?.Invoke(value);
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    /// <summary>按名字、描述、公司、路径或 PID 筛选；改一次就地重筛，不重新枚举进程。</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ApplyFilter();
        }
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public ProcessItem? SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
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

    /// <summary>空态只在「没在忙 且 一行都没有」时显示——筛选没命中时也算空，用户才知道是筛选的问题。</summary>
    public bool ShowEmptyState => !IsBusy && Items.Count == 0;

    public void SetError(string? message) => ErrorText = message ?? "";

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在枚举进程…");
            var (items, error) = await Task.Run(() => _service.Load());

            _all.Clear();
            _all.AddRange(items);

            ErrorText = error ?? "";
            SummaryText = ProcessService.Summarize(items);
            ApplyFilter();

            _status($"进程：{items.Count} 个");
        }
        catch (Exception ex)
        {
            SetError($"枚举进程失败：{ex.Message}");
            _status("枚举进程失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all)
        {
            if (item.Matches(_filter)) Items.Add(item);
        }

        SummaryText = _all.Count == 0
            ? "还没有枚举进程"
            : string.IsNullOrWhiteSpace(_filter)
                ? ProcessService.Summarize(_all)
                : $"筛选出 {Items.Count} 个进程（共 {_all.Count} 个）";

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void Kill(ProcessItem? item, bool wholeTree)
    {
        if (item is null) return;

        var what = wholeTree ? "它和它启动的所有子进程" : "它";
        var confirm = MessageBox.Show(
            $"结束「{item.Name}」（PID {item.Pid}）？\n\n会结束{what}。\n未保存的数据会丢失；如果是系统关键进程，Windows 会自己拦下来。",
            wholeTree ? "结束进程树" : "结束进程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var (ok, message) = _service.Kill(item, wholeTree);
        if (ok)
        {
            _status(message);
            _ = RefreshAsync();
        }
        else
        {
            SetError(message);
        }
    }

    private void OpenLocation(ProcessItem? item)
    {
        if (item is null) return;

        if (string.IsNullOrEmpty(item.ExecutablePath))
        {
            SetError($"读不到「{item.Name}」的映像路径（多半是受保护的系统进程），无法定位");
            return;
        }

        if (!Native.RevealInExplorer(item.ExecutablePath))
            SetError($"定位失败：{item.ExecutablePath} 可能已被移动或删除");
    }

    private void Copy(ProcessItem? item)
    {
        if (item is null) return;

        var text = $"""
            {item.Name}（PID {item.Pid}）
            描述：{item.Description}
            公司：{item.Company}
            路径：{item.ExecutablePath}
            内存：{item.MemoryText} · 线程：{item.ThreadCount}
            启动时间：{item.StartTimeText}
            """;

        try
        {
            Clipboard.SetText(text);
            _status("已复制进程信息");
        }
        catch (Exception ex)
        {
            SetError("复制失败：" + ex.Message);
        }
    }
}
