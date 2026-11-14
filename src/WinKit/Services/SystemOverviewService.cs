using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinKit.Core;

namespace WinKit.Services;

public sealed class OverviewItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>ok / warn / bad / 空（普通信息）</summary>
    public string Level { get; init; } = "";

    public string LevelText => Level switch
    {
        "ok" => "正常",
        "warn" => "注意",
        "bad" => "异常",
        _ => ""
    };
}

public sealed class OverviewCard
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public IReadOnlyList<OverviewItem> Items { get; init; } = Array.Empty<OverviewItem>();
}

/// <summary>
/// 系统概览：只读汇总 OS / CPU / 内存 / 磁盘 / 显卡 / 运行时长 / 待重启 / 安全状态。
/// 全部走注册表、Environment 与 Win32，不联网、不写任何东西。
/// </summary>
public sealed class SystemOverviewService
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public List<OverviewCard> Load()
    {
        return new List<OverviewCard>
        {
            BuildSystemCard(),
            BuildHardwareCard(),
            BuildStorageCard(),
            BuildHealthCard()
        };
    }

    private static OverviewCard BuildSystemCard()
    {
        var product = Reg.GetString(RegPath.Hklm(CurrentVersion), "ProductName") ?? "Windows";
        var display = Reg.GetString(RegPath.Hklm(CurrentVersion), "DisplayVersion") ?? "";
        var build = Reg.GetString(RegPath.Hklm(CurrentVersion), "CurrentBuildNumber") ?? "";
        var ubr = Reg.GetInt(RegPath.Hklm(CurrentVersion), "UBR");
        var edition = Reg.GetString(RegPath.Hklm(CurrentVersion), "EditionID") ?? "";
        var install = Reg.GetString(RegPath.Hklm(CurrentVersion), "InstallDate");

        // Win11 的 ProductName 仍然写着 "Windows 10"，按内部版本号纠正
        if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

        var buildText = string.IsNullOrWhiteSpace(build) ? "" : $"{build}.{ubr ?? 0}";
        var installText = "";
        if (long.TryParse(install, out var installSeconds))
        {
            try
            {
                installText = DateTimeOffset.FromUnixTimeSeconds(installSeconds).LocalDateTime.ToString("yyyy-MM-dd");
            }
            catch (Exception)
            {
                // 时间戳异常时留空
            }
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var bootTime = DateTime.Now - uptime;

        return new OverviewCard
        {
            Title = "系统",
            Subtitle = "版本、计算机与运行时长",
            Items = new List<OverviewItem>
            {
                new() { Label = "操作系统", Value = $"{product} {display}".Trim(), Detail = $"内部版本 {buildText} · {edition}" },
                new() { Label = "计算机名", Value = Environment.MachineName, Detail = Environment.UserDomainName },
                new() { Label = "当前用户", Value = Environment.UserName, Detail = Native.IsElevated() ? "已提权（管理员）" : "未提权（普通用户）", Level = Native.IsElevated() ? "ok" : "warn" },
                new() { Label = "系统安装于", Value = string.IsNullOrWhiteSpace(installText) ? "未知" : installText, Detail = "" },
                new()
                {
                    Label = "已运行",
                    Value = $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分",
                    Detail = $"开机时间 {bootTime:yyyy-MM-dd HH:mm}"
                }
            }
        };
    }

    private static OverviewCard BuildHardwareCard()
    {
        var cpuName = Reg.GetString(RegPath.Hklm(CpuKey), "ProcessorNameString")?.Trim() ?? "未知处理器";
        var cpuMhz = Reg.GetInt(RegPath.Hklm(CpuKey), "~MHz");

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var memOk = GlobalMemoryStatusEx(ref mem);
        var totalRam = memOk ? mem.ullTotalPhys : 0;
        var freeRam = memOk ? mem.ullAvailPhys : 0;
        var usedPercent = totalRam > 0 ? (int)Math.Round((totalRam - freeRam) * 100.0 / totalRam) : 0;

        var gpus = new List<string>();
        foreach (var sub in Reg.SubKeyNames(RegPath.Hklm(DisplayClass)))
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            var name = Reg.GetString(RegPath.Hklm($@"{DisplayClass}\{sub}"), "DriverDesc");
            if (!string.IsNullOrWhiteSpace(name) && !gpus.Contains(name!)) gpus.Add(name!);
        }

        return new OverviewCard
        {
            Title = "硬件",
            Subtitle = "处理器、内存与显卡",
            Items = new List<OverviewItem>
            {
                new()
                {
                    Label = "处理器",
                    Value = cpuName,
                    Detail = $"{Environment.ProcessorCount} 个逻辑处理器" + (cpuMhz is > 0 ? $" · 约 {cpuMhz} MHz" : "")
                },
                new()
                {
                    Label = "内存",
                    Value = $"{ThisPcService.FormatSize((long)totalRam)}（已用 {usedPercent}%）",
                    Detail = $"可用 {ThisPcService.FormatSize((long)freeRam)}",
                    Level = usedPercent >= 90 ? "warn" : "ok"
                },
                new()
                {
                    Label = "显卡",
                    Value = gpus.Count == 0 ? "未识别" : string.Join(" / ", gpus.Take(2)),
                    Detail = gpus.Count > 2 ? $"共 {gpus.Count} 个显示适配器" : ""
                }
            }
        };
    }

    private static OverviewCard BuildStorageCard()
    {
        var items = new List<OverviewItem>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (!drive.IsReady) continue;

            try
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var usedPercent = total > 0 ? (int)Math.Round((total - free) * 100.0 / total) : 0;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;

                items.Add(new OverviewItem
                {
                    Label = $"{drive.Name.TrimEnd('\\')} {label}",
                    Value = $"{ThisPcService.FormatSize(total)} · 已用 {usedPercent}%",
                    Detail = $"可用 {ThisPcService.FormatSize(free)} / 共 {ThisPcService.FormatSize(total)}",
                    Level = usedPercent >= 95 ? "bad" : usedPercent >= 85 ? "warn" : "ok"
                });
            }
            catch (Exception)
            {
                // 单个卷读不到就跳过
            }
        }

        if (items.Count == 0)
            items.Add(new OverviewItem { Label = "磁盘", Value = "未发现可用卷", Level = "warn" });

        return new OverviewCard { Title = "存储", Subtitle = "各卷容量与占用", Items = items };
    }

    private static OverviewCard BuildHealthCard()
    {
        var items = new List<OverviewItem>();

        // 待重启：三个来源任一命中就说明有挂起的重启
        var pending = new List<string>();
        if (Reg.Exists(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")))
            pending.Add("组件服务");
        if (Reg.Exists(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")))
            pending.Add("Windows 更新");
        if (Reg.GetValue(RegPath.Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager"), "PendingFileRenameOperations") is string[] { Length: > 0 })
            pending.Add("文件重命名挂起");

        items.Add(new OverviewItem
        {
            Label = "待重启",
            Value = pending.Count == 0 ? "无" : "有",
            Detail = pending.Count == 0 ? "没有挂起的重启任务" : string.Join("、", pending),
            Level = pending.Count == 0 ? "ok" : "warn"
        });

        // Defender
        var defenderStart = Reg.GetInt(RegPath.Hklm(@"SYSTEM\CurrentControlSet\Services\WinDefend"), "Start");
        var defenderRunning = Process.GetProcessesByName("MsMpEng").Length > 0;
        items.Add(new OverviewItem
        {
            Label = "Microsoft Defender",
            Value = defenderStart is null or 4 ? "已禁用" : defenderRunning ? "运行中" : "已停止",
            Detail = defenderStart is null or 4 ? "服务启动类型为禁用" : "实时保护由系统策略控制",
            Level = defenderStart is null or 4 ? "warn" : defenderRunning ? "ok" : "warn"
        });

        // 防火墙
        var firewallStart = Reg.GetInt(RegPath.Hklm(@"SYSTEM\CurrentControlSet\Services\mpssvc"), "Start");
        items.Add(new OverviewItem
        {
            Label = "Windows 防火墙",
            Value = firewallStart is null or 4 ? "已禁用" : "已启用",
            Detail = firewallStart is null or 4 ? "mpssvc 服务被禁用" : "mpssvc 服务随系统启动",
            Level = firewallStart is null or 4 ? "warn" : "ok"
        });

        // 安全启动
        var secureBoot = Reg.GetInt(RegPath.Hklm(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"), "UEFISecureBootEnabled");
        items.Add(new OverviewItem
        {
            Label = "安全启动",
            Value = secureBoot == 1 ? "已开启" : secureBoot == 0 ? "未开启" : "不支持/未知",
            Detail = secureBoot is null ? "非 UEFI 启动或系统未提供该状态" : "",
            Level = secureBoot == 1 ? "ok" : secureBoot == 0 ? "warn" : ""
        });

        // 系统盘剩余空间告警已在存储卡片里，这里补一条内存压力
        return new OverviewCard { Title = "健康与安全", Subtitle = "重启、防护与启动安全", Items = items };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
