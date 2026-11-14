using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class DiskHealthViewModel : ObservableObject, IModule
{
    private readonly DiskHealthService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;
    private bool _isBusy;
    private string _summary = "";
    private string _errorText = "";

    public DiskHealthViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;
        Disks = new ObservableCollection<DiskHealthItem>();
        Volumes = new ObservableCollection<VolumeHealthItem>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        CopyCommand = new RelayCommand(p => Copy(p as DiskHealthItem));
    }

    public ObservableCollection<DiskHealthItem> Disks { get; }
    public ObservableCollection<VolumeHealthItem> Volumes { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

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

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在读取磁盘健康信息…");
            var (disks, volumes, error) = await Task.Run(() => _service.Load());

            Disks.Clear();
            foreach (var disk in disks) Disks.Add(disk);

            Volumes.Clear();
            foreach (var volume in volumes) Volumes.Add(volume);

            ErrorText = error ?? "";
            SummaryText = DiskHealthService.Summarize(disks, volumes);
            _status($"磁盘健康：{disks.Count} 块物理磁盘，{volumes.Count} 个卷");
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            _status($"读取磁盘健康失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Copy(DiskHealthItem? item)
    {
        if (item is null) return;

        var text = $"""
            {item.Name}
            类型：{item.MediaType} · 容量：{item.SizeText} · 健康：{item.HealthText}
            SMART：{item.SmartText} · 磨损：{item.WearText} · 温度：{item.TemperatureText}
            通电：{item.HoursText} · 读写错误：{item.ErrorText}
            序列号：{item.Serial}
            """;

        try
        {
            Clipboard.SetText(text);
            _status("已复制磁盘信息");
        }
        catch (Exception ex)
        {
            _status("复制失败：" + ex.Message);
        }
    }
}
