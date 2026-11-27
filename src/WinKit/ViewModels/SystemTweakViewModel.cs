using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class SystemTweakViewModel : ObservableObject, IModule
{
    private readonly SystemTweakService _service;
    private readonly Action<string> _status;
    private readonly Action<bool>? _setBusy;

    private List<PortProcessItem> _allPorts = new();
    private string _subView = "tweaks"; // tweaks | ports | panels
    private string _portSearchText = "";
    private bool _onlyListeningPorts = true;
    private bool _isBusy;
    private string _portSummaryText = "";

    public SystemTweakViewModel(ChangeJournal journal, Action<string> status, Action<bool>? setBusy = null)
    {
        _service = new SystemTweakService(journal);
        _status = status;
        _setBusy = setBusy;

        Tweaks = new ObservableCollection<TweakSwitchItem>();
        Ports = new ObservableCollection<PortProcessItem>();
        Shortcuts = new ObservableCollection<AdminPanelShortcut>(_service.GetAdminShortcuts());

        ShowTweaksViewCommand = new RelayCommand(() => SubView = "tweaks");
        ShowPortsViewCommand = new RelayCommand(() => SubView = "ports");
        ShowPanelsViewCommand = new RelayCommand(() => SubView = "panels");

        RefreshCommand = new RelayCommand(RefreshCurrent);
        OpenTweakRegeditCommand = new RelayCommand(p => OpenTweakRegedit(p as TweakSwitchItem));

        RefreshPortsCommand = new RelayCommand(() => _ = LoadPortsAsync());
        KillPortProcessCommand = new RelayCommand(p => KillPortProcess(p as PortProcessItem));
        OpenPortProcessFolderCommand = new RelayCommand(p => OpenPortProcessFolder(p as PortProcessItem));
        CopyPortInfoCommand = new RelayCommand(p => CopyPortInfo(p as PortProcessItem));

        FlushDnsCommand = new RelayCommand(() => _ = RunNetRepairAsync("刷新 DNS 缓存", "ipconfig.exe", "/flushdns"));
        ResetWinsockCommand = new RelayCommand(() => _ = RunNetRepairAsync(
            "重置 Winsock 目录", "netsh.exe", "winsock reset",
            "将执行 netsh winsock reset 重置 Winsock 目录。\n\n" +
            "这会移除第三方 LSP（部分 VPN/杀毒的网络组件），需要重启电脑才完全生效。确定继续吗？"));
        ResetTcpIpCommand = new RelayCommand(() => _ = RunNetRepairAsync(
            "重置 TCP/IP 协议栈", "netsh.exe", "int ip reset",
            "将执行 netsh int ip reset 重置 TCP/IP 协议栈。\n\n" +
            "需要重启电脑才完全生效。确定继续吗？"));
        EditHostsCommand = new RelayCommand(EditHosts);

        OpenShortcutCommand = new RelayCommand(p => OpenShortcut(p as AdminPanelShortcut));
    }

    public ObservableCollection<TweakSwitchItem> Tweaks { get; }
    public ObservableCollection<PortProcessItem> Ports { get; }
    public ObservableCollection<AdminPanelShortcut> Shortcuts { get; }

    public RelayCommand ShowTweaksViewCommand { get; }
    public RelayCommand ShowPortsViewCommand { get; }
    public RelayCommand ShowPanelsViewCommand { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenTweakRegeditCommand { get; }

    public RelayCommand RefreshPortsCommand { get; }
    public RelayCommand KillPortProcessCommand { get; }
    public RelayCommand OpenPortProcessFolderCommand { get; }
    public RelayCommand CopyPortInfoCommand { get; }

    public RelayCommand FlushDnsCommand { get; }
    public RelayCommand ResetWinsockCommand { get; }
    public RelayCommand ResetTcpIpCommand { get; }
    public RelayCommand EditHostsCommand { get; }

    public RelayCommand OpenShortcutCommand { get; }

    public string SubView
    {
        get => _subView;
        set
        {
            if (!Set(ref _subView, value)) return;
            OnPropertyChanged(nameof(ShowTweaksView));
            OnPropertyChanged(nameof(ShowPortsView));
            OnPropertyChanged(nameof(ShowPanelsView));

            if (value == "ports" && _allPorts.Count == 0)
            {
                _ = LoadPortsAsync();
            }
        }
    }

    public bool ShowTweaksView => _subView == "tweaks";
    public bool ShowPortsView => _subView == "ports";
    public bool ShowPanelsView => _subView == "panels";

    public string PortSearchText
    {
        get => _portSearchText;
        set
        {
            if (Set(ref _portSearchText, value)) ApplyPortFilter();
        }
    }

    public bool OnlyListeningPorts
    {
        get => _onlyListeningPorts;
        set
        {
            if (Set(ref _onlyListeningPorts, value)) ApplyPortFilter();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) _setBusy?.Invoke(value);
        }
    }

    public string PortSummaryText
    {
        get => _portSummaryText;
        private set => Set(ref _portSummaryText, value);
    }

    public void Refresh()
    {
        LoadTweaks();
    }

    private void RefreshCurrent()
    {
        if (ShowPortsView) _ = LoadPortsAsync();
        else LoadTweaks();
    }

    private void LoadTweaks()
    {
        try
        {
            Tweaks.Clear();
            foreach (var t in _service.LoadTweaks())
            {
                t.ToggleRequested = OnTweakToggled;
                Tweaks.Add(t);
            }
            _status($"已加载 {Tweaks.Count} 项系统调优开关");
        }
        catch (Exception ex)
        {
            _status($"加载系统调优开关失败：{ex.Message}");
        }
    }

    private void OnTweakToggled(TweakSwitchItem item, bool isOn)
    {
        if (Native.RequireAdmin("修改系统设置") is { } adminNeeded)
        {
            // 开关已经被界面拨过去了，这里要拨回来，并且必须摘掉回调再写，否则会又触发一次
            item.ToggleRequested = null;
            item.IsOn = !isOn;
            item.ToggleRequested = OnTweakToggled;
            SetError(adminNeeded);
            return;
        }

        try
        {
            _service.SetTweak(item, isOn);
            _status($"{(isOn ? "已开启" : "已关闭")}：{item.Title}");
        }
        catch (Exception ex)
        {
            item.ToggleRequested = null;
            item.IsOn = !isOn;
            item.ToggleRequested = OnTweakToggled;
            SetError($"设置失败：{ex.Message}");
        }
    }

    private void OpenTweakRegedit(TweakSwitchItem? item)
    {
        if (item is null) return;
        if (Native.OpenInRegedit(item.RegistryPath))
            _status($"已在注册表编辑器中定位：{item.ValueName}");
    }

    private async Task LoadPortsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在扫描本机 TCP/UDP 端口占用与监听进程…");
            _allPorts = await _service.LoadPortTableAsync();
            ApplyPortFilter();
            _status($"端口扫描完成：共检测 {_allPorts.Count} 条连接/监听记录");
        }
        catch (Exception ex)
        {
            _status($"端口扫描失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyPortFilter()
    {
        Ports.Clear();
        var kw = _portSearchText.Trim().ToLowerInvariant();

        var query = _allPorts
            .Where(p => !_onlyListeningPorts || p.State == "LISTENING" || p.Protocol == "UDP")
            .Where(p => kw.Length == 0 || p.SearchBlob.Contains(kw));

        foreach (var item in query) Ports.Add(item);

        var listeningCount = _allPorts.Count(p => p.State == "LISTENING");
        PortSummaryText = $"共捕获 {_allPorts.Count} 条端口记录（TCP LISTENING {listeningCount} 个）· 当前显示 {Ports.Count} 条";
    }

    private void KillPortProcess(PortProcessItem? item)
    {
        if (item is null || !item.CanKill) return;

        var confirm = MessageBox.Show(
            $"确定结束占用端口 {item.LocalPort} ({item.Protocol}) 的进程吗？\n\n" +
            $"进程名：{item.ProcessName} (PID: {item.Pid})\n" +
            $"路径：{item.ExecutablePath}",
            "结束占用端口的进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            using var proc = Process.GetProcessById(item.Pid);
            proc.Kill(true);
            _status($"已结束进程：{item.ProcessName} (PID: {item.Pid})");
            _ = LoadPortsAsync();
        }
        catch (Exception ex)
        {
            SetError($"结束进程失败：{ex.Message}");
        }
    }

    private void OpenPortProcessFolder(PortProcessItem? item)
    {
        if (item is null || !item.HasExecutable) return;
        try
        {
            Native.RevealInExplorer(item.ExecutablePath);
        }
        catch (Exception) { }
    }

    private void CopyPortInfo(PortProcessItem? item)
    {
        if (item is null) return;
        var text = $"{item.Protocol} {item.LocalAddress}:{item.LocalPort} | PID:{item.Pid} ({item.ProcessName}) | {item.ExecutablePath}";
        try
        {
            Clipboard.SetText(text);
            _status($"已复制端口信息：{item.LocalPort}");
        }
        catch (Exception) { }
    }

    private async Task RunNetRepairAsync(string title, string fileName, string args, string? confirmation = null)
    {
        if (IsBusy) return;
        if (Native.RequireAdmin(title) is { } adminNeeded)
        {
            SetError(adminNeeded);
            _status(adminNeeded);
            return;
        }

        if (confirmation is not null && MessageBox.Show(confirmation, title,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        IsBusy = true;
        SetError("");
        try
        {
            _status($"正在执行：{title}…");
            var (ok, msg) = await _service.RunNetworkRepairCommandAsync(title, fileName, args);
            _status(msg);
            if (!ok) SetError(msg);
            MessageBox.Show(msg, title, MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetError($"{title} 失败：{ex.Message}");
            _status(ErrorText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void EditHosts()
    {
        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

        try
        {
            Native.OpenPath("notepad.exe", $"\"{hostsPath}\"");
            _status($"已在记事本中打开：{hostsPath}");
        }
        catch (Exception ex)
        {
            SetError($"打开 hosts 文件失败：{ex.Message}");
        }
    }

    private void OpenShortcut(AdminPanelShortcut? shortcut)
    {
        if (shortcut is null) return;
        try
        {
            Native.OpenPath(shortcut.FileName, shortcut.Arguments);
            _status($"已打开：{shortcut.Title} ({shortcut.CommandLabel})");
        }
        catch (Exception ex)
        {
            SetError($"打开「{shortcut.Title}」失败：{ex.Message}");
        }
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
