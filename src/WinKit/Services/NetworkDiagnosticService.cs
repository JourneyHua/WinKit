using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinKit.Core;

namespace WinKit.Services;

public sealed class NetworkAdapterInfo
{
    public string Name { get; init; } = "";
    public string TypeText { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string SpeedText { get; init; } = "";
    public string Ipv4Text { get; init; } = "";
    public string GatewayText { get; init; } = "";
    public string DnsText { get; init; } = "";
    public bool IsUp { get; init; }
}

public sealed class PingResult
{
    public string Target { get; init; } = "";
    public string Label { get; init; } = "";
    public int Sent { get; init; }
    public int Received { get; init; }
    public long MinMs { get; init; }
    public long AvgMs { get; init; }
    public long MaxMs { get; init; }
    public string LossText => Sent == 0 ? "" : $"{Sent - Received}/{Sent} 丢包";
    public string LatencyText => Received == 0 ? "无响应" : $"{AvgMs} ms（{MinMs}~{MaxMs}）";
    public string Level => Received == 0 ? "bad" : AvgMs > 150 ? "warn" : "ok";
}

public sealed class TcpResult
{
    public string Label { get; init; } = "";
    public string Target { get; init; } = "";
    public int Port { get; init; }
    public bool Ok { get; init; }
    public long ElapsedMs { get; init; }
    public string ResultText => Ok ? $"{ElapsedMs} ms" : "连接失败";
    public string Level => !Ok ? "bad" : ElapsedMs > 800 ? "warn" : "ok";
}

public sealed class DnsResult
{
    public string Domain { get; init; } = "";
    public string Addresses { get; init; } = "";
    public long ElapsedMs { get; init; }
    public bool Ok { get; init; }
    public string ResultText => Ok ? $"{ElapsedMs} ms" : "解析失败";
    public string Level => !Ok ? "bad" : ElapsedMs > 500 ? "warn" : "ok";
}

public sealed class WifiInfo
{
    public string Ssid { get; init; } = "";
    public string State { get; init; } = "";
    public string Signal { get; init; } = "";
    public string RadioType { get; init; } = "";
    public string Channel { get; init; } = "";
    public string ReceiveRate { get; init; } = "";
    public string TransmitRate { get; init; } = "";
    public bool Connected { get; init; }
}

public sealed class ProxyInfo
{
    public bool Enabled { get; init; }
    public string Server { get; init; } = "";
    public string AutoConfigUrl { get; init; } = "";
    public string Bypass { get; init; } = "";
    public string StatusText => Enabled ? "已启用" : "未启用";
}

/// <summary>
/// 网络诊断：网卡/DNS/网关、延迟与丢包、DNS 解析耗时、Wi-Fi 信号、系统代理。
/// 只有本模块会主动发包（Ping / DNS 解析），其余模块仍然全程不联网。
/// </summary>
public sealed class NetworkDiagnosticService
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private static readonly string[] FilterDriverMarkers =
    {
        "-QoS Packet Scheduler", "-WFP ", "-Native WiFi Filter", "-Virtual WiFi Filter",
        "-Hyper-V Virtual Switch Extension", "-Microsoft Kernel Debug Network Adapter"
    };

    public List<NetworkAdapterInfo> LoadAdapters()
    {
        var list = new List<NetworkAdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus == OperationalStatus.NotPresent) continue;

            // 过滤驱动会以“网卡名-QoS Packet Scheduler-0000”这种伪接口出现在枚举里，没有地址也没有网关
            if (FilterDriverMarkers.Any(m => nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;

            var props = nic.GetIPProperties();
            var ipv4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();

            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString())
                .ToList();

            var dns = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            // 既没有 IPv4 也没有网关的条目对诊断没有意义
            if (ipv4.Count == 0 && gateways.Count == 0) continue;

            var speed = nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mbps" : "";

            list.Add(new NetworkAdapterInfo
            {
                Name = nic.Name,
                TypeText = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "无线",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "有线",
                    NetworkInterfaceType.Ppp => "拨号",
                    NetworkInterfaceType.Tunnel => "隧道",
                    _ => nic.NetworkInterfaceType.ToString()
                },
                StatusText = nic.OperationalStatus switch
                {
                    OperationalStatus.Up => "已连接",
                    OperationalStatus.Down => "未连接",
                    _ => nic.OperationalStatus.ToString()
                },
                SpeedText = speed,
                Ipv4Text = ipv4.Count == 0 ? "—" : string.Join(", ", ipv4),
                GatewayText = gateways.Count == 0 ? "—" : string.Join(", ", gateways),
                DnsText = dns.Count == 0 ? "—" : string.Join(", ", dns),
                IsUp = nic.OperationalStatus == OperationalStatus.Up
            });
        }

        return list
            .OrderByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<List<PingResult>> PingAsync(IReadOnlyList<(string Label, string Host)> targets, int count = 4)
    {
        var results = new List<PingResult>();

        foreach (var (label, host) in targets)
        {
            var times = new List<long>();
            var received = 0;

            try
            {
                using var ping = new Ping();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(host, 1500);
                        if (reply.Status == IPStatus.Success)
                        {
                            received++;
                            times.Add(reply.RoundtripTime);
                        }
                    }
                    catch (Exception)
                    {
                        // 单次失败按丢包计
                    }
                }
            }
            catch (Exception)
            {
                // 目标不可达
            }

            results.Add(new PingResult
            {
                Target = host,
                Label = label,
                Sent = count,
                Received = received,
                MinMs = times.Count == 0 ? 0 : times.Min(),
                MaxMs = times.Count == 0 ? 0 : times.Max(),
                AvgMs = times.Count == 0 ? 0 : (long)times.Average()
            });
        }

        return results;
    }

    public async Task<List<TcpResult>> TcpCheckAsync(IReadOnlyList<(string Label, string Host, int Port)> targets, int timeoutMs = 3000)
    {
        var results = new List<TcpResult>();

        foreach (var (label, host, port) in targets)
        {
            var watch = Stopwatch.StartNew();
            var ok = false;

            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(timeoutMs);
                await client.ConnectAsync(host, port, cts.Token);
                ok = client.Connected;
            }
            catch (Exception)
            {
                ok = false;
            }

            watch.Stop();
            results.Add(new TcpResult
            {
                Label = label,
                Target = host,
                Port = port,
                Ok = ok,
                ElapsedMs = watch.ElapsedMilliseconds
            });
        }

        return results;
    }

    public async Task<List<DnsResult>> DnsBenchmarkAsync(IReadOnlyList<string> domains)
    {
        var results = new List<DnsResult>();

        foreach (var domain in domains)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(domain);
                watch.Stop();

                var text = string.Join(", ", addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Take(3));

                results.Add(new DnsResult
                {
                    Domain = domain,
                    Addresses = string.IsNullOrWhiteSpace(text) ? "无 IPv4 记录" : text,
                    ElapsedMs = watch.ElapsedMilliseconds,
                    Ok = true
                });
            }
            catch (Exception)
            {
                watch.Stop();
                results.Add(new DnsResult { Domain = domain, ElapsedMs = watch.ElapsedMilliseconds, Ok = false });
            }
        }

        return results;
    }

    public WifiInfo LoadWifi()
    {
        var info = new WifiInfo();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // netsh 在中文 Windows 上按 OEM 代码页（CP936）输出，用 UTF-8 解会把
                // “状态/信号/无线电类型”全变成替换字符，后面的键名匹配永远失败
                StandardOutputEncoding = AppManagerService.GetOemEncoding(),
                StandardErrorEncoding = AppManagerService.GetOemEncoding()
            };
            psi.ArgumentList.Add("wlan");
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add("interfaces");

            using var process = Process.Start(psi);
            if (process is null) return info;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("error 5", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
            {
                // netsh wlan 需要管理员权限；未提权时明确告诉用户，而不是显示一片空白
                return new WifiInfo
                {
                    State = output.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
                            || output.Contains("error 5", StringComparison.OrdinalIgnoreCase)
                        ? "需要管理员权限才能读取 Wi-Fi 详情"
                        : "未检测到无线网卡"
                };
            }

            string? Read(string key)
            {
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                    var idx = trimmed.IndexOf(':');
                    if (idx < 0) continue;
                    return trimmed[(idx + 1)..].Trim();
                }

                return null;
            }

            var state = Read("状态") ?? Read("State") ?? "";
            var ssid = Read("SSID") ?? "";
            var signal = Read("信号") ?? Read("Signal") ?? "";
            var radio = Read("无线电类型") ?? Read("Radio type") ?? "";
            var channel = Read("信道") ?? Read("Channel") ?? "";
            var receive = Read("接收速率") ?? Read("Receive rate") ?? "";
            var transmit = Read("传输速率") ?? Read("Transmit rate") ?? "";

            return new WifiInfo
            {
                Ssid = ssid,
                State = state,
                Signal = signal,
                RadioType = radio,
                Channel = channel,
                ReceiveRate = receive,
                TransmitRate = transmit,
                Connected = state.Contains("已连接", StringComparison.OrdinalIgnoreCase)
                            || state.Contains("connected", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception)
        {
            return info;
        }
    }

    public ProxyInfo LoadProxy()
    {
        var path = RegPath.Hkcu(InternetSettings);
        var enabled = (Reg.GetInt(path, "ProxyEnable") ?? 0) == 1;

        return new ProxyInfo
        {
            Enabled = enabled,
            Server = Reg.GetString(path, "ProxyServer") ?? "",
            AutoConfigUrl = Reg.GetString(path, "AutoConfigURL") ?? "",
            Bypass = Reg.GetString(path, "ProxyOverride") ?? ""
        };
    }

    /// <summary>
    /// 执行网络修复命令。复用 AppManagerService.RunProcessAsync：
    /// 它并发读两个流并在超时后杀进程，避免 UI 线程被 netsh/ipconfig 长时间占住。
    /// </summary>
    public async Task<(bool Ok, string Message)> RunAsync(string fileName, params string[] args)
    {
        var arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        var (code, output, err) = await AppManagerService
            .RunProcessAsync(fileName, arguments, TimeSpan.FromSeconds(30), AppManagerService.GetOemEncoding());

        var text = string.Join("\n", new[] { output, err }.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();

        return code == 0
            ? (true, string.IsNullOrWhiteSpace(text) ? "完成" : text)
            : (false, string.IsNullOrWhiteSpace(text) ? $"{fileName} 返回失败（错误码 {code}）" : text);
    }
}
