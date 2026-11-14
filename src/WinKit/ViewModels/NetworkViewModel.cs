using System.Collections.ObjectModel;
using System.Windows;
using WinKit.Core;
using WinKit.Services;

namespace WinKit.ViewModels;

public sealed class NetworkViewModel : ObservableObject, IModule
{
    private readonly NetworkDiagnosticService _service = new();
    private readonly Action<string> _status;
    private bool _isBusy;
    private string _summary = "";
    private WifiInfo _wifi = new();
    private ProxyInfo _proxy = new();

    public NetworkViewModel(Action<string> status)
    {
        _status = status;

        Adapters = new ObservableCollection<NetworkAdapterInfo>();
        Pings = new ObservableCollection<PingResult>();
        TcpResults = new ObservableCollection<TcpResult>();
        DnsResults = new ObservableCollection<DnsResult>();

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        FlushDnsCommand = new RelayCommand(FlushDns);
        ResetWinsockCommand = new RelayCommand(ResetWinsock);
        ResetTcpCommand = new RelayCommand(ResetTcp);
        OpenNetworkSettingsCommand = new RelayCommand(() => Open("ms-settings:network"));
        OpenHostsCommand = new RelayCommand(OpenHosts);
        CopyDnsCommand = new RelayCommand(CopyDns);
    }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; }
    public ObservableCollection<PingResult> Pings { get; }
    public ObservableCollection<TcpResult> TcpResults { get; }
    public ObservableCollection<DnsResult> DnsResults { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand FlushDnsCommand { get; }
    public RelayCommand ResetWinsockCommand { get; }
    public RelayCommand ResetTcpCommand { get; }
    public RelayCommand OpenNetworkSettingsCommand { get; }
    public RelayCommand OpenHostsCommand { get; }
    public RelayCommand CopyDnsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string SummaryText
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public WifiInfo Wifi
    {
        get => _wifi;
        private set => Set(ref _wifi, value);
    }

    public ProxyInfo Proxy
    {
        get => _proxy;
        private set => Set(ref _proxy, value);
    }

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;

        try
        {
            _status("正在检测网络…");

            var adapters = await Task.Run(() => _service.LoadAdapters());
            Adapters.Clear();
            foreach (var adapter in adapters) Adapters.Add(adapter);

            Wifi = await Task.Run(() => _service.LoadWifi());
            Proxy = _service.LoadProxy();

            var gateway = adapters
                .Where(a => a.IsUp)
                .Select(a => a.GatewayText)
                .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g) && g != "—");

            var targets = new List<(string Label, string Host)>();
            if (!string.IsNullOrWhiteSpace(gateway))
                targets.Add(("网关", gateway!.Split(',')[0].Trim()));
            targets.Add(("阿里 DNS", "223.5.5.5"));
            targets.Add(("Google DNS", "8.8.8.8"));

            var pings = await _service.PingAsync(targets);
            Pings.Clear();
            foreach (var ping in pings) Pings.Add(ping);

            // ICMP 常被防火墙/代理拦掉，补一组 TCP 443 连接测试，结论更可靠
            var tcp = await _service.TcpCheckAsync(new[]
            {
                ("百度 443", "www.baidu.com", 443),
                ("微软 443", "www.microsoft.com", 443),
                ("GitHub 443", "github.com", 443)
            });
            TcpResults.Clear();
            foreach (var item in tcp) TcpResults.Add(item);

            var dns = await _service.DnsBenchmarkAsync(new[] { "www.baidu.com", "www.microsoft.com", "github.com" });
            DnsResults.Clear();
            foreach (var item in dns) DnsResults.Add(item);

            var upCount = adapters.Count(a => a.IsUp);
            var badPing = pings.Count(p => p.Received == 0);
            var badTcp = tcp.Count(t => !t.Ok);
            var slowDns = dns.Count(d => !d.Ok || d.ElapsedMs > 500);

            SummaryText = $"{upCount} 个网卡已连接 · " +
                          (badTcp == 0 ? "外网连通正常" : $"{badTcp}/{tcp.Count} 个站点连不上") +
                          (badPing == 0 ? " · ICMP 正常" : " · ICMP 被拦截或无响应") +
                          (slowDns == 0 ? " · DNS 解析正常" : $" · {slowDns} 个域名解析慢或失败");

            _status("网络诊断完成");
        }
        catch (Exception ex)
        {
            _status($"网络诊断失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FlushDns()
    {
        var (ok, message) = _service.Run("ipconfig.exe", "/flushdns");
        _status(ok ? "已刷新 DNS 解析缓存" : $"刷新失败：{message}");
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ResetWinsock()
    {
        var confirm = MessageBox.Show(
            "将执行 netsh winsock reset 重置 Winsock 目录。\n\n" +
            "这会移除第三方 LSP（部分 VPN/杀毒的网络组件），需要重启电脑才完全生效。确定继续吗？",
            "重置 Winsock", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = _service.Run("netsh.exe", "winsock", "reset");
        _status(ok ? "Winsock 已重置，重启后生效" : $"重置失败：{message}");
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ResetTcp()
    {
        var confirm = MessageBox.Show(
            "将执行 netsh int ip reset 重置 TCP/IP 协议栈。\n\n" +
            "需要重启电脑才完全生效。确定继续吗？",
            "重置 TCP/IP", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        var (ok, message) = _service.Run("netsh.exe", "int", "ip", "reset");
        _status(ok ? "TCP/IP 已重置，重启后生效" : $"重置失败：{message}");
        if (!ok) MessageBox.Show(message, "WinKit", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenHosts()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = @"C:\Windows\System32\drivers\etc\hosts",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开 hosts 失败：{ex.Message}");
        }
    }

    private void CopyDns()
    {
        var text = string.Join("\n", Adapters
            .Where(a => a.IsUp)
            .Select(a => $"{a.Name}：{a.DnsText}"));

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Clipboard.SetText(text);
            _status("已复制 DNS 服务器列表");
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _status($"打开失败：{ex.Message}");
        }
    }
}
