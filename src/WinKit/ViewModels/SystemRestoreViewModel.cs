using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

/// <summary>
/// 系统还原点管理：列出、创建、删除、恢复，以及开关系统保护。
/// 所有动作都要管理员权限（WinKit 主清单是 requireAdministrator），
/// 且「恢复到某个还原点」会立刻重启机器，所以那一步必须用户二次确认。
/// </summary>
public sealed class SystemRestoreViewModel : ObservableObject, IModule
{
    private readonly SystemRestoreService _service = new();
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private bool _isBusy;
    private string _summary = "";
    private string _errorText = "";
    private string _newDescription = "";
    private bool _protectionEnabled;
    private int _frequencyMinutes = -1;
    private RestorePointItem? _selectedPoint;

    public SystemRestoreViewModel(Action<string> status, Action<bool>? setBusy = null)
    {
        _status = status;
        _setBusy = setBusy;

        Points = new ObservableCollection<RestorePointItem>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        CreateCommand = new RelayCommand(() => _ = CreateAsync());
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync(p as RestorePointItem));
        RestoreToCommand = new RelayCommand(p => _ = RestoreToAsync(p as RestorePointItem));
        EnableProtectionCommand = new RelayCommand(() => _ = SetProtectionAsync(true));
        DisableProtectionCommand = new RelayCommand(() => _ = SetProtectionAsync(false));
        OpenSystemPropertiesCommand = new RelayCommand(OpenSystemProperties);
        CopyCommand = new RelayCommand(p => Copy(p as RestorePointItem));
    }

    public ObservableCollection<RestorePointItem> Points { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CreateCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RestoreToCommand { get; }
    public RelayCommand EnableProtectionCommand { get; }
    public RelayCommand DisableProtectionCommand { get; }
    public RelayCommand OpenSystemPropertiesCommand { get; }
    public RelayCommand CopyCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            _setBusy?.Invoke(value);
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string ProtectionText => _protectionEnabled
        ? "系统保护：已开启（可以创建还原点）"
        : "系统保护：未开启——没有它就没有还原点可列，先点「开启系统保护」";

    public string FrequencyText => _frequencyMinutes switch
    {
        < 0 => "创建频率：系统默认（24 小时一次）",
        0 => "创建频率：不限制",
        _ => $"创建频率：每 {_frequencyMinutes} 分钟最多一次"
    };

    public bool HasPoints => Points.Count > 0;

    /// <summary>
    /// 系统保护是否开启。界面上是通过 ProtectionText 显示的，这个布尔值给自动化断言用：
    /// 「没有还原点」和「系统保护没开所以本来就没有还原点」是两件事，得能分清。
    /// </summary>
    public bool ProtectionEnabled => _protectionEnabled;

    public bool ShowEmptyState => !IsBusy && Points.Count == 0;

    /// <summary>新建还原点的说明文字，默认带时间戳，方便事后认出来。</summary>
    public string NewDescription
    {
        get => string.IsNullOrWhiteSpace(_newDescription) ? $"WinKit 手动创建 {DateTime.Now:yyyy-MM-dd HH:mm}" : _newDescription;
        set => Set(ref _newDescription, value);
    }

    public RestorePointItem? SelectedPoint
    {
        get => _selectedPoint;
        set => Set(ref _selectedPoint, value);
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

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在读取系统还原点…");
            var (points, enabled, frequency, error) = await Task.Run(() => _service.Load());

            Points.Clear();
            foreach (var point in points) Points.Add(point);

            _protectionEnabled = enabled;
            _frequencyMinutes = frequency;

            OnPropertyChanged(nameof(ProtectionText));
            OnPropertyChanged(nameof(FrequencyText));
            OnPropertyChanged(nameof(HasPoints));
            OnPropertyChanged(nameof(ShowEmptyState));

            ErrorText = error ?? "";

            var latest = Points.FirstOrDefault();
            // 空列表不能只写「没有还原点」：用户看不出是自己机器上真没有、还是程序没读到。
            // 按系统保护状态把原因和下一步说清楚，这也是界面自检要求的「要么有数据、要么说清原因」。
            SummaryText = Points.Count > 0
                ? $"共 {Points.Count} 个还原点，最近一个：{latest?.CreatedAt} {latest?.Description}"
                : _protectionEnabled
                    ? "系统保护已开启，但这台机器上还没有任何还原点——点「创建还原点」可以立刻建一个"
                    : "系统保护没有开启，所以没有任何还原点——先点「开启系统保护」";

            _status($"系统还原：{Points.Count} 个还原点");
        }
        catch (Exception ex)
        {
            SetError($"读取还原点失败：{ex.Message}");
            _status("读取还原点失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateAsync()
    {
        if (IsBusy) return;

        if (!_protectionEnabled)
        {
            SetError("系统保护没开：先点「开启系统保护」，否则创建出来的还原点不会生效");
            return;
        }

        var description = NewDescription.Trim();
        IsBusy = true;

        try
        {
            _status($"正在创建还原点：{description}…");
            var (ok, message) = await Task.Run(() => _service.Create(description));

            if (ok)
            {
                _status(message);
                SetError("");
                _newDescription = "";
                OnPropertyChanged(nameof(NewDescription));
            }
            else
            {
                // 系统默认 24 小时内只允许建一个，这个失败消息要原样给用户看
                SetError($"创建还原点失败：{message}");
                _status("创建还原点失败");
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task DeleteAsync(RestorePointItem? point)
    {
        if (point is null || IsBusy) return;

        var confirm = MessageBox.Show(
            $"确定删除这个还原点吗？\n\n#{point.SequenceNumber} · {point.CreatedAt}\n{point.Description}\n\n" +
            "删除后无法恢复，系统再也回不到这个时间点了。",
            "删除还原点",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => _service.Delete(point));

            if (ok)
            {
                _status(message);
                SetError("");
            }
            else
            {
                SetError(message);
                _status("删除还原点失败");
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private async Task RestoreToAsync(RestorePointItem? point)
    {
        if (point is null || IsBusy) return;

        var confirm = MessageBox.Show(
            $"要把系统恢复到下面这个还原点吗？\n\n#{point.SequenceNumber} · {point.CreatedAt}\n{point.Description}\n\n" +
            "注意：\n" +
            "· 系统会**立即重启**并开始还原，请先保存所有工作；\n" +
            "· 还原只影响系统设置与程序，个人文件（文档、照片）不会被回退；\n" +
            "· 还原过程可能持续十几分钟，中途不要断电。",
            "恢复到还原点",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => _service.RestoreTo(point));

            if (ok)
            {
                _status(message);
                MessageBox.Show("已发起系统还原，机器马上会重启。", "系统还原",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                SetError($"发起系统还原失败：{message}");
                _status("发起系统还原失败");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetProtectionAsync(bool enable)
    {
        if (IsBusy) return;

        if (!enable)
        {
            var confirm = MessageBox.Show(
                "关闭系统保护会**删除该驱动器上已有的全部还原点**，并停止后续自动创建。\n\n确定关闭吗？",
                "关闭系统保护",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK) return;
        }

        IsBusy = true;
        try
        {
            var (ok, message) = await Task.Run(() => _service.SetProtection(enable));

            if (ok)
            {
                _status(message);
                SetError("");
            }
            else
            {
                SetError(message);
                _status(enable ? "开启系统保护失败" : "关闭系统保护失败");
            }
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    private void OpenSystemProperties()
    {
        if (!Native.OpenPath(@"SystemPropertiesProtection.exe"))
            _status("打开系统保护设置失败：找不到 SystemPropertiesProtection.exe");
    }

    private void Copy(RestorePointItem? point)
    {
        if (point is null) return;

        var text = $"#{point.SequenceNumber} · {point.CreatedAt} · {point.TypeText}\n{point.Description}";

        try
        {
            Clipboard.SetText(text);
            _status("已复制还原点信息");
        }
        catch (Exception ex)
        {
            SetError("复制失败：" + ex.Message);
        }
    }
}
