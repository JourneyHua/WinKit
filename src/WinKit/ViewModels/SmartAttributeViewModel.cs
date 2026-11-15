using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class SmartAttributeViewModel : ObservableObject, IModule
{
    private readonly SmartAttributeService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private bool _isBusy;
    private string _summary = "";
    private string _errorText = "";
    private SmartDiskItem? _selectedDisk;
    private string? _lastExportPath;

    public SmartAttributeViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;
        Disks = new ObservableCollection<SmartDiskItem>();
        Attributes = new ObservableCollection<SmartAttributeItem>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        ExportCommand = new RelayCommand(Export);
        OpenExportFolderCommand = new RelayCommand(OpenExportFolder);
        CopyCommand = new RelayCommand(p => Copy(p as SmartAttributeItem));
    }

    public ObservableCollection<SmartDiskItem> Disks { get; }
    public ObservableCollection<SmartAttributeItem> Attributes { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenExportFolderCommand { get; }
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

    public SmartDiskItem? SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            if (!Set(ref _selectedDisk, value)) return;
            ShowSelectedDisk();
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

    public bool ShowEmptyState => !IsBusy && Attributes.Count == 0;

    public void SetError(string? message) => ErrorText = message ?? "";

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在读取 SMART 原始属性（需要管理员权限）…");
            var (disks, error) = await Task.Run(() => _service.Load());

            var previous = _selectedDisk?.Index;

            Disks.Clear();
            foreach (var disk in disks) Disks.Add(disk);

            SelectedDisk = disks.FirstOrDefault(d => d.Index == previous)
                           ?? disks.FirstOrDefault(d => d.Attributes.Count > 0)
                           ?? disks.FirstOrDefault();

            ErrorText = error ?? "";
            if (disks.Count == 0)
            {
                // 一块盘都读不到时 SelectedDisk 为 null，ShowSelectedDisk 不会被调用，
                // 摘要就会停在空白上——原因在错误条里，但摘要也得说一句，否则页面看起来像没加载。
                SummaryText = "没有可显示的磁盘属性";
                _status("没有读到物理磁盘");
            }
            else
            {
                _status($"SMART 属性：{disks.Count} 块盘，共 {disks.Sum(d => d.Attributes.Count)} 条属性");
            }
        }
        catch (Exception ex)
        {
            SetError($"读取 SMART 属性失败：{ex.Message}");
            _status("读取 SMART 属性失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowSelectedDisk()
    {
        Attributes.Clear();
        foreach (var attribute in _selectedDisk?.Attributes ?? new List<SmartAttributeItem>())
            Attributes.Add(attribute);

        if (_selectedDisk is not null && Attributes.Count == 0 && _selectedDisk.Error is { Length: > 0 } reason)
            SetError(reason);

        UpdateSummary();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void UpdateSummary()
    {
        if (_selectedDisk is null)
        {
            SummaryText = Disks.Count == 0 ? "还没有读取 SMART" : "请选择一块磁盘";
            return;
        }

        var warn = Attributes.Count(a => a.Level != "ok");
        SummaryText = $"{_selectedDisk.DisplayName} · 预测：{_selectedDisk.PredictText} · "
                      + (Attributes.Count == 0
                          ? "没有可读属性"
                          : $"共 {Attributes.Count} 条属性" + (warn > 0 ? $"，{warn} 条需要注意" : "，全部正常"));
    }

    /// <summary>
    /// 导出当前选中磁盘的属性表为 CSV。CSV 不写 BOM 时 Excel 打开中文全是乱码，
    /// 所以这里用 UTF-8 with BOM；文件落在 Exports 目录，导出后直接打开该目录。
    /// </summary>
    private void Export()
    {
        if (_selectedDisk is null || Attributes.Count == 0)
        {
            SetError("没有可导出的属性表：先刷新，再选一块能读出 SMART 的磁盘");
            return;
        }

        try
        {
            Directory.CreateDirectory(SmartAttributeService.ExportDirectory);

            var path = Path.Combine(
                SmartAttributeService.ExportDirectory,
                $"smart-{_selectedDisk.Index}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var builder = new StringBuilder();
            builder.AppendLine("属性ID,属性名称,当前值,最差值,阈值,原始值,判定");
            foreach (var item in Attributes)
            {
                builder.AppendLine(string.Join(',',
                    item.Id, Quote(item.Name), item.Value, item.Worst, item.Threshold,
                    Quote(item.RawText), Quote(item.LevelText)));
            }

            builder.AppendLine();
            builder.AppendLine(Quote($"磁盘,{_selectedDisk.DisplayName},预测,{_selectedDisk.PredictText},导出时间,{DateTime.Now:yyyy-MM-dd HH:mm:ss}"));

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            _lastExportPath = path;

            _status($"已导出 SMART 属性：{path}");
            Native.OpenPath(SmartAttributeService.ExportDirectory);
        }
        catch (Exception ex)
        {
            SetError($"导出失败：{ex.Message}");
        }
    }

    private void OpenExportFolder()
    {
        try
        {
            // 导出过就定位到那一个文件，没导出过就打开目录——两种情况下用户想看的都是「东西在哪」
            if (_lastExportPath is { Length: > 0 } path && File.Exists(path))
            {
                if (Native.RevealInExplorer(path)) return;
            }

            Directory.CreateDirectory(SmartAttributeService.ExportDirectory);
            if (!Native.OpenPath(SmartAttributeService.ExportDirectory))
                SetError("打开导出目录失败");
        }
        catch (Exception ex)
        {
            SetError($"打开导出目录失败：{ex.Message}");
        }
    }

    private void Copy(SmartAttributeItem? item)
    {
        if (item is null) return;

        var text = $"{item.Name}（ID {item.Id}）：当前 {item.Value}，最差 {item.Worst}，阈值 {item.Threshold}，原始值 {item.RawText}，{item.LevelText}";

        try
        {
            Clipboard.SetText(text);
            _status("已复制属性");
        }
        catch (Exception ex)
        {
            SetError("复制失败：" + ex.Message);
        }
    }

    private static string Quote(string value)
        => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
