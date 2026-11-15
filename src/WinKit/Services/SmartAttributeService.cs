using System.IO;
using System.Text.Json;

namespace WinKit.Services;

/// <summary>一条 SMART 属性。值都是 0~255 的归一化分数，低才是坏；原始值(raw)是厂商定义的计数，看它才知道到底发生了多少次。</summary>
public sealed class SmartAttributeItem
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Value { get; init; }
    public int Worst { get; init; }
    public int Threshold { get; init; }
    public string RawText { get; init; } = "";
    public string Level { get; init; } = "ok";

    public string LevelText => Level switch
    {
        "bad" => "临界",
        "warn" => "注意",
        _ => "正常"
    };
}

/// <summary>一块物理磁盘的 SMART 明细。Attributes 为空时 Error 会说清原因（多半是控制器不透传）。</summary>
public sealed class SmartDiskItem
{
    public int Index { get; init; }
    public string Model { get; init; } = "";
    public string PredictText { get; init; } = "不可用";
    public List<SmartAttributeItem> Attributes { get; init; } = new();
    public string? Error { get; init; }

    public string DisplayName => $"{Model}（磁盘 {Index}）";

    public string SummaryText => Attributes.Count == 0
        ? Error ?? "这块盘没有可读的 SMART 属性"
        : $"{Attributes.Count} 条属性，其中 {Attributes.Count(a => a.Level != "ok")} 条需要注意";
}

/// <summary>
/// 读 SMART 原始属性表。
///
/// 走 root\wmi 的 MSStorageDriver_FailurePredictData/Thresholds：Windows 把控制器透传上来的
/// 512 字节原样丢在这里，需要自己按 ATA 的属性块格式解析（从偏移 2 开始，每 12 字节一条）。
/// 这条路只有管理员能用，且只对 ATA/AHCI 直连盘有效——USB 外置盘、RAID 卡后面的盘、
/// 虚拟盘基本都读不到，那就是「不支持」，不是程序出错。
/// </summary>
public sealed class SmartAttributeService
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $disks = @(Get-CimInstance -ClassName Win32_DiskDrive | ForEach-Object {
            [pscustomobject]@{ index = [int]$_.Index; model = [string]$_.Model; pnp = [string]$_.PNPDeviceID }
        })
        $status = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus | ForEach-Object {
            [pscustomobject]@{ inst = [string]$_.InstanceName; fail = [bool]$_.PredictFailure }
        })
        $data = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictData | ForEach-Object {
            $bytes = $_.VendorSpecific
            [pscustomobject]@{
                inst = [string]$_.InstanceName
                bytes = $(if ($null -eq $bytes) { '' } else { [Convert]::ToBase64String($bytes) })
            }
        })
        $thresholds = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictThresholds | ForEach-Object {
            $bytes = $_.VendorSpecific
            [pscustomobject]@{
                inst = [string]$_.InstanceName
                bytes = $(if ($null -eq $bytes) { '' } else { [Convert]::ToBase64String($bytes) })
            }
        })
        [pscustomobject]@{ disks = $disks; status = $status; data = $data; thresholds = $thresholds } |
            ConvertTo-Json -Depth 6 -Compress
        """;

    /// <summary>导出目录：和体检报告分开，免得「导出 CSV」混进 Markdown 报告里。</summary>
    public static string ExportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKit", "Exports");

    /// <summary>
    /// 把「读不到」翻译成人话。WMI 的失败信息有三种长相：真的抛在 stderr 里、
    /// 被 SilentlyContinue 吞掉只剩空数组、或者干脆退出码非零。三种都要落到一句用户能懂的提示上。
    /// </summary>
    private static string ExplainFailure(int code, string? err)
    {
        var detail = err?.Trim() ?? "";
        if (detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("拒绝访问", StringComparison.Ordinal)
            || detail.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            return "读取 SMART 原始属性需要管理员权限：请以管理员身份运行 WinKit";
        }

        if (detail.Length == 0 || code == 0)
        {
            return "没有读到任何物理磁盘的 SMART 数据：多半是权限不足（WMI 存储类被挡），"
                + "请以管理员身份运行 WinKit；也可能是这台机器的存储控制器不提供该接口";
        }

        return $"powershell 退出码 {code}：{detail}";
    }

    public (List<SmartDiskItem> Disks, string? Error) Load()
    {
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{Script.Replace("\"", "\\\"")}\"";
        var (code, output, err) = AppManagerService
            .RunProcessAsync("powershell.exe", args, TimeSpan.FromSeconds(40))
            .GetAwaiter()
            .GetResult();

        if (string.IsNullOrWhiteSpace(output))
        {
            return (new List<SmartDiskItem>(), ExplainFailure(code, err));
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            var statusMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Enumerate(root, "status"))
            {
                var name = ReadString(item, "inst");
                if (name.Length > 0) statusMap[name] = ReadBool(item, "fail");
            }

            var dataMap = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Enumerate(root, "data"))
            {
                var name = ReadString(item, "inst");
                var bytes = DecodeBase64(ReadString(item, "bytes"));
                if (name.Length > 0 && bytes.Length > 0) dataMap[name] = bytes;
            }

            var thresholdMap = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Enumerate(root, "thresholds"))
            {
                var name = ReadString(item, "inst");
                var bytes = DecodeBase64(ReadString(item, "bytes"));
                if (name.Length > 0 && bytes.Length > 0) thresholdMap[name] = bytes;
            }

            var disks = new List<SmartDiskItem>();
            foreach (var disk in Enumerate(root, "disks"))
            {
                var index = ReadInt(disk, "index");
                var model = ReadString(disk, "model");
                var pnp = ReadString(disk, "pnp");

                var instance = MatchInstance(pnp, dataMap.Keys);
                var attributes = instance is null || !dataMap.TryGetValue(instance, out var block)
                    ? new List<SmartAttributeItem>()
                    : Parse(block, thresholdMap.TryGetValue(instance, out var limit) ? limit : null);

                disks.Add(new SmartDiskItem
                {
                    Index = index,
                    Model = string.IsNullOrWhiteSpace(model) ? $"磁盘 {index}" : model.Trim(),
                    PredictText = instance is not null && statusMap.TryGetValue(instance, out var fail)
                        ? (fail ? "预测将故障" : "未预测故障")
                        : "不可用",
                    Attributes = attributes,
                    Error = attributes.Count > 0
                        ? null
                        : "这块盘的控制器没有透传 SMART 原始数据（USB 外置、RAID、虚拟盘常见），只能看「磁盘健康」页的汇总结论"
                });
            }

            if (disks.Count == 0)
            {
                // 脚本里 $ErrorActionPreference = 'SilentlyContinue'，WMI 被权限挡住时
                // 既不抛错也不写 stderr，只剩一个空数组——这种情况必须自己补一句人话，
                // 否则界面会安静地显示「0 块盘」，看起来像没实现功能。
                return (new List<SmartDiskItem>(), ExplainFailure(code, err));
            }

            return (disks.OrderBy(d => d.Index).ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<SmartDiskItem>(), $"解析 SMART 数据失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按 ATA 的属性块格式解析：偏移 0~1 是版本号，之后每 12 字节一条属性——
    /// [0] 属性 ID、[1..2] 状态标志、[3] 当前值、[4] 最差值、[5..10] 原始值(小端)、[11] 保留。
    /// 阈值块结构相同，就阈值放在 [1]。ID 为 0 的是填充，跳过。
    /// </summary>
    public static List<SmartAttributeItem> Parse(byte[] data, byte[]? thresholds)
    {
        var limits = new Dictionary<int, int>();
        if (thresholds is { Length: > 13 })
        {
            for (var offset = 2; offset + 12 <= thresholds.Length; offset += 12)
            {
                var id = thresholds[offset];
                if (id == 0) continue;
                limits[id] = thresholds[offset + 1];
            }
        }

        var result = new List<SmartAttributeItem>();
        if (data.Length < 14) return result;

        for (var offset = 2; offset + 12 <= data.Length; offset += 12)
        {
            var id = data[offset];
            if (id == 0) continue;

            var value = data[offset + 3];
            var worst = data[offset + 4];

            // 原始值是 6 字节小端：偏移 +5 的那个字节是最低位，所以按位左移拼起来
            long raw = 0;
            for (var i = 0; i < 6; i++) raw |= (long)data[offset + 5 + i] << (8 * i);

            var threshold = limits.TryGetValue(id, out var limit) ? limit : 0;
            result.Add(new SmartAttributeItem
            {
                Id = id,
                Name = NameOf(id),
                Value = value,
                Worst = worst,
                Threshold = threshold,
                RawText = FormatRaw(id, raw, value, data[offset + 5]),
                Level = LevelOf(id, value, threshold, raw)
            });
        }

        return result;
    }

    private static string LevelOf(int id, int value, int threshold, long raw)
    {
        // 归一化值跌破阈值是「厂家自己说该换了」
        if (threshold > 0 && value <= threshold) return value <= Math.Max(1, threshold / 2) ? "bad" : "warn";

        // 这几项原始值不为 0 就代表已经发生过坏道/不可纠正错误，哪怕归一化值还很高也值得看一眼
        return id switch
        {
            5 or 197 or 198 when raw > 0 => "warn",
            187 or 199 when raw > 0 => "warn",
            _ => "ok"
        };
    }

    private static string FormatRaw(int id, long raw, int value, byte lowByte) => id switch
    {
        9 => $"{raw & 0xFFFFFFFF} 小时",
        194 or 190 => $"{lowByte} °C",
        5 or 197 or 198 or 199 or 187 => $"{raw} 个",
        231 or 177 or 173 or 233 => $"剩余 {value}%",
        _ => raw.ToString()
    };

    private static string NameOf(int id) => id switch
    {
        1 => "底层读取错误率",
        2 => "吞吐性能",
        3 => "主轴起转时间",
        4 => "启停次数",
        5 => "重映射扇区数",
        7 => "寻道错误率",
        8 => "寻道时间性能",
        9 => "通电时间",
        10 => "主轴重试次数",
        12 => "通电次数",
        13 => "软读取错误率",
        22 => "氦气液位",
        184 => "端到端错误",
        187 => "报告不可纠正错误",
        188 => "超时命令数",
        190 => "气流温度",
        191 => "冲击错误率",
        192 => "紧急断电次数",
        193 => "磁头加载卸载次数",
        194 => "温度",
        196 => "重分配事件数",
        197 => "当前待映射扇区",
        198 => "脱机不可纠正扇区",
        199 => "UDMA CRC 错误",
        231 => "SSD 剩余寿命",
        241 => "主机写入总量",
        242 => "主机读取总量",
        _ => $"属性 {id}"
    };

    private static string? MatchInstance(string pnp, IEnumerable<string> instances)
    {
        if (string.IsNullOrWhiteSpace(pnp)) return null;

        var trimmed = pnp.Trim();
        foreach (var instance in instances)
        {
            // WMI 的实例名是 PNPDeviceID 加上 "_0" 之类的后缀，前缀比对即可
            if (instance.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) return instance;
        }

        // 有些控制器会改写大小写或路径分隔符，退一步做包含匹配
        foreach (var instance in instances)
        {
            if (instance.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return instance;
        }

        return null;
    }

    private static byte[] DecodeBase64(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) yield break;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) yield return item;
                break;
            // ConvertTo-Json 遇到单元素数组会退化成一个对象，这里吃掉这个坑
            case JsonValueKind.Object:
                yield return value;
                break;
        }
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : -1;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
