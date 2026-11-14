using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class OverviewViewModel : ObservableObject, IModule
{
    private readonly SystemOverviewService _service = new();
    private readonly Action<string> _status;
    private bool _isLoading;
    private string _summary = "";

    public OverviewViewModel(Action<string> status)
    {
        _status = status;
        Cards = new ObservableCollection<OverviewCard>();
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        CopyCommand = new RelayCommand(p => Copy(p as OverviewItem));
        OpenSystemInfoCommand = new RelayCommand(() => Open("msinfo32.exe"));
        OpenTaskManagerCommand = new RelayCommand(() => Open("taskmgr.exe"));
        OpenAboutCommand = new RelayCommand(() => Open("ms-settings:about"));
    }

    public ObservableCollection<OverviewCard> Cards { get; }

    /// <summary>顶部关键指标：从各卡片里挑出"一眼要知道有没有问题"的那几项。</summary>
    public ObservableCollection<OverviewItem> KeyMetrics { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenSystemInfoCommand { get; }
    public RelayCommand OpenTaskManagerCommand { get; }
    public RelayCommand OpenAboutCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var cards = await Task.Run(() => _service.Load());

            Cards.Clear();
            foreach (var card in cards) Cards.Add(card);

            // 关键指标：内存占用、系统盘占用、待重启、异常项数
            KeyMetrics.Clear();
            foreach (var key in new[] { "内存", "已运行" })
            {
                var item = cards.SelectMany(c => c.Items).FirstOrDefault(i => i.Label == key);
                if (item is not null) KeyMetrics.Add(item);
            }

            var systemVolume = cards
                .FirstOrDefault(c => c.Title == "存储")?
                .Items.FirstOrDefault(i => i.Label.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
            if (systemVolume is not null) KeyMetrics.Add(systemVolume);

            var pendingReboot = cards
                .FirstOrDefault(c => c.Title == "健康与安全")?
                .Items.FirstOrDefault(i => i.Label == "待重启");
            if (pendingReboot is not null) KeyMetrics.Add(pendingReboot);

            var warnings = cards.SelectMany(c => c.Items).Count(i => i.Level is "warn" or "bad");
            SummaryText = warnings == 0
                ? "全部检查项正常"
                : $"{warnings} 项需要留意（标黄/标红）";
            _status("系统概览已刷新");
        }
        catch (Exception ex)
        {
            _status($"读取系统信息失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Copy(OverviewItem? item)
    {
        if (item is null) return;
        try
        {
            Clipboard.SetText($"{item.Label}：{item.Value}" + (string.IsNullOrWhiteSpace(item.Detail) ? "" : $"\n{item.Detail}"));
            _status($"已复制：{item.Label}");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
