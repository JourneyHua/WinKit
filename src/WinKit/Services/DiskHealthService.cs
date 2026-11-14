using System.IO;
using System.Text.Json;

namespace WinKit.Services;

public sealed class DiskHealthItem
{
    public string Name { get; init; } = "";
    public string MediaType { get; init; } = "";
    public string HealthText { get; init; } = "";
    public long Size { get; init; }
    public string SizeText => ThisPcService.FormatSize(Size);
    public string Serial { get; init; } = "";

    public int? Temperature { get; init; }
    public int? Wear { get; init; }
    public long? PowerOnHours { get; init; }
    public long? ReadErrors { get; init; }
    public long? WriteErrors { get; init; }
    public bool? PredictFailure { get; init; }

    public string TemperatureText => Temperature is > 0 ? $"{Temperature} °C" : "—";
    public string WearText => Wear is >= 0 and <= 100 ? $"{Wear}%" : "—";
    public string HoursText => PowerOnHours is > 0 ? $"{PowerOnHours} 小时" : "—";
    public string ErrorText => ReadErrors is null && WriteErrors is null
        ? "—"
        : $"{ReadErrors ?? 0} / {WriteErrors ?? 0}";

    /// <summary>
    /// 合并列：磨损 / 温度 / 通电 / 读写错误原先占 4 列共 360px，
    /// 把磁盘型号列挤到 97px（1200 窗口）甚至 20px（1000 窗口）。
    /// </summary>
    public string HealthDetail
    {
        get
        {
            var parts = new List<string>();

            if (Wear is >= 0 and <= 100) parts.Add($"磨损 {Wear}%");
            if (Temperature is > 0) parts.Add($"{Temperature} °C");
            if (PowerOnHours is > 0) parts.Add($"{PowerOnHours} 小时");
            if (ReadErrors is not null || WriteErrors is not null)
                parts.Add($"错误 {ReadErrors ?? 0}/{WriteErrors ?? 0}");

            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }
    }

    /// <summary>综合风险：SMART 预测失败或磨损过高才算 warn/bad，读不到数据不算异常。</summary>
    public string Level
    {
        get
        {
            if (PredictFailure == true) return "bad";
            if (Wear is >= 90) return "bad";
            if (Wear is >= 80 || Temperature is >= 70) return "warn";
            if (HealthText.Contains("警告", StringComparison.OrdinalIgnoreCase) ||
                HealthText.Contains("Warning", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (HealthText.Contains("不正常", StringComparison.OrdinalIgnoreCase) ||
                HealthText.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase)) return "bad";
            return "ok";
        }
    }

    public string LevelText => Level switch { "bad" => "异常", "warn" => "注意", "ok" => "正常", _ => "" };

    public string SmartText => PredictFailure is null ? "不可用" : PredictFailure.Value ? "预测将故障" : "未预测故障";
}

public sealed class VolumeHealthItem
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public int UsedPercent => Total <= 0 ? 0 : (int)Math.Round((Total - Free) * 100.0 / Total);
    public string UsageText => $"{ThisPcService.FormatSize(Total - Free)} / {ThisPcService.FormatSize(Total)}（已用 {UsedPercent}%）";
    public string Level => UsedPercent >= 95 ? "bad" : UsedPercent >= 85 ? "warn" : "ok";
}

/// <summary>
/// 磁盘健康：SMART 预测失败位、SSD 磨损度、温度、通电时长、读写错误、各卷占用。
/// 数据源全部需要管理员权限（WinKit 本身以管理员运行）；拿不到时如实显示“不可用”，不猜。
/// </summary>
public sealed class DiskHealthService
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $disks = @(Get-PhysicalDisk | ForEach-Object {
            $r = $_ | Get-StorageReliabilityCounter
            [pscustomobject]@{
                name = [string]$_.FriendlyName
                media = [string]$_.MediaType
                health = [string]$_.HealthStatus
                size = [long]$_.Size
                serial = [string]$_.SerialNumber
                temp = if ($r) { [int]$r.Temperature } else { $null }
                wear = if ($r) { [int]$r.Wear } else { $null }
                hours = if ($r) { [long]$r.PowerOnHours } else { $null }
                readErr = if ($r) { [long]$r.ReadErrorsTotal } else { $null }
                writeErr = if ($r) { [long]$r.WriteErrorsTotal } else { $null }
            }
        })
        $smart = @(Get-CimInstance -Namespace root/wmi -ClassName MSStorageDriver_FailurePredictStatus |
            ForEach-Object { [pscustomobject]@{ instance = [string]$_.InstanceName; predict = [bool]$_.PredictFailure } })
        [pscustomobject]@{ disks = $disks; smart = $smart } | ConvertTo-Json -Depth 5 -Compress
        """;

    public (List<DiskHealthItem> Disks, List<VolumeHealthItem> Volumes, string? Error) Load()
    {
        var disks = new List<DiskHealthItem>();
        var volumes = new List<VolumeHealthItem>();
        string? error = null;

        // 卷占用不需要管理员，先取，保证至少有一部分信息
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (!drive.IsReady) continue;

            try
            {
                volumes.Add(new VolumeHealthItem
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel,
                    Total = drive.TotalSize,
                    Free = drive.AvailableFreeSpace
                });
            }
            catch (Exception)
            {
                // 单个卷读不到就跳过
            }
        }

        try
        {
            var escaped = Script.Replace("\"", "\\\"");
            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{escaped}\"";
            var (code, output, err) = AppManagerService
                .RunProcessAsync("powershell.exe", args, TimeSpan.FromSeconds(40))
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(output))
            {
                error = string.IsNullOrWhiteSpace(err)
                    ? "读取磁盘健康失败（PowerShell 无输出）"
                    : err.Trim();
                return (disks, volumes, error);
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            var smartFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("smart", out var smart) && smart.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in smart.EnumerateArray())
                {
                    if (!s.TryGetProperty("predict", out var predict) || !predict.GetBoolean()) continue;

                    var instance = s.TryGetProperty("instance", out var inst) ? inst.GetString() ?? "" : "";
                    smartFailures.Add(instance);
                }
            }

            if (root.TryGetProperty("disks", out var diskArray) && diskArray.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var d in diskArray.EnumerateArray())
                {
                    var name = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                    bool? predictFailure = null;
                    if (smartFailures.Count > 0)
                    {
                        // InstanceName 形如 SCSI\Disk&Ven_...\_0，用序号做匹配
                        predictFailure = smartFailures.Any(x => x.Contains($"_{index}", StringComparison.OrdinalIgnoreCase))
                                         || smartFailures.Count > index;
                    }

                    disks.Add(new DiskHealthItem
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? $"磁盘 {index}" : name,
                        MediaType = d.TryGetProperty("media", out var media) ? media.GetString() ?? "" : "",
                        HealthText = d.TryGetProperty("health", out var h) ? h.GetString() ?? "" : "",
                        Size = d.TryGetProperty("size", out var size) && size.TryGetInt64(out var sv) ? sv : 0,
                        Serial = d.TryGetProperty("serial", out var serial) ? serial.GetString() ?? "" : "",
                        Temperature = ReadInt(d, "temp"),
                        Wear = ReadInt(d, "wear"),
                        PowerOnHours = ReadLong(d, "hours"),
                        ReadErrors = ReadLong(d, "readErr"),
                        WriteErrors = ReadLong(d, "writeErr"),
                        PredictFailure = predictFailure
                    });

                    index++;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return (disks, volumes, error);
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.TryGetInt32(out var v) ? v : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.TryGetInt64(out var v) ? v : null;
    }

    public static string Summarize(IReadOnlyList<DiskHealthItem> disks, IReadOnlyList<VolumeHealthItem> volumes)
    {
        var bad = disks.Count(d => d.Level == "bad");
        var warn = disks.Count(d => d.Level == "warn");
        var full = volumes.Count(v => v.Level is "bad" or "warn");

        var parts = new List<string>();

        parts.Add(disks.Count == 0 ? "未能读取 SMART 信息" : $"{disks.Count} 块物理磁盘");
        if (bad > 0) parts.Add($"{bad} 块异常");
        if (warn > 0) parts.Add($"{warn} 块需注意");
        if (full > 0) parts.Add($"{full} 个卷空间紧张");

        return string.Join(" · ", parts);
    }
}
