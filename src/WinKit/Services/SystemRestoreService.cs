using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinKit.Services;

public sealed class RestorePointItem
{
    public long SequenceNumber { get; init; }
    public string Description { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public int TypeCode { get; init; }

    public string TypeText => TypeCode switch
    {
        0 => "应用安装",
        1 => "应用卸载",
        10 => "驱动安装",
        12 => "设置修改",
        13 => "已取消",
        _ => $"类型 {TypeCode}"
    };
}

/// <summary>
/// 系统还原点管理。
///
/// 只用公开接口：创建走 <c>Checkpoint-Computer</c>、删除走 srclient 的 SRRemoveRestorePoint、
/// 列出走 WMI 的 root\default:SystemRestore、开关系统保护走 Enable/Disable-ComputerRestore。
/// 这些都在管理员权限下才可用——WinKit 主清单本来就是 requireAdministrator。
///
/// 两个必须说清的边界（README 也写了）：
/// 1. 不能跨机器、不能只恢复单个文件；
/// 2. 机器没开「系统保护」就没有还原点可列，只能先开保护再创建。
/// </summary>
public sealed class SystemRestoreService
{
    [DllImport("srclient.dll", SetLastError = true)]
    private static extern int SRRemoveRestorePoint(int dwRPNum);

    private const string ListScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $pts = @(Get-CimInstance -Namespace root/default -ClassName SystemRestore |
            ForEach-Object {
                [pscustomobject]@{
                    seq = [long]$_.SequenceNumber
                    desc = [string]$_.Description
                    time = if ($_.CreationTime) { ([datetime]$_.CreationTime).ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
                    type = [int]$_.RestorePointType
                }
            })
        $cfg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        [pscustomobject]@{
            points = $pts
            disabled = $(if ($null -eq $cfg.DisableSR) { 0 } else { [int]$cfg.DisableSR })
            frequency = $(if ($null -eq $cfg.SystemRestorePointCreationFrequency) { -1 } else { [int]$cfg.SystemRestorePointCreationFrequency })
        } | ConvertTo-Json -Depth 4 -Compress
        """;

    public (List<RestorePointItem> Points, bool ProtectionEnabled, int FrequencyMinutes, string? Error) Load()
    {
        var points = new List<RestorePointItem>();
        var enabled = false;
        var frequency = -1;

        var (code, output, err) = Run(ListScript, TimeSpan.FromSeconds(40));

        if (string.IsNullOrWhiteSpace(output))
        {
            var message = string.IsNullOrWhiteSpace(err) ? "读取还原点失败（PowerShell 无输出）" : err.Trim();
            if (message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("拒绝访问", StringComparison.Ordinal))
            {
                message = "读取还原点需要管理员权限：请以管理员身份运行 WinKit";
            }

            return (points, enabled, frequency, message);
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.TryGetProperty("points", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                {
                    points.Add(new RestorePointItem
                    {
                        SequenceNumber = p.TryGetProperty("seq", out var seq) ? seq.GetInt64() : 0,
                        Description = p.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "",
                        CreatedAt = p.TryGetProperty("time", out var time) ? time.GetString() ?? "" : "",
                        TypeCode = p.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number ? type.GetInt32() : -1
                    });
                }
            }

            // DisableSR = 1 表示被显式关掉；没有这个值说明没关
            var disabled = root.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == 1;
            enabled = !disabled;

            if (root.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.Number)
                frequency = f.GetInt32();
        }
        catch (Exception ex)
        {
            return (points, enabled, frequency, $"解析还原点列表失败：{ex.Message}");
        }

        return (points.OrderByDescending(p => p.SequenceNumber).ToList(), enabled, frequency, null);
    }

    /// <summary>
    /// 创建还原点。系统默认 24 小时内只允许创建一个（除非注册表
    /// SystemRestorePointCreationFrequency 改小），这一限制在 Windows 侧，程序不绕过。
    /// </summary>
    public (bool Ok, string Message) Create(string description)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "WinKit 手动创建" : description.Trim();
        var safe = desc.Replace("'", "''");

        // 用 $$""" 而不是 $"""：脚本里有 PowerShell 变量 $_，单个 $ 会被 C# 当成插值起点；
        // 两个 $ 之后插值必须写 {{…}}，脚本里的 { } $ 全部原样保留。
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Checkpoint-Computer -Description '{{safe}}' -RestorePointType 'MODIFY_SETTINGS'
                'OK'
            } catch {
                'ERR ' + $_.Exception.Message
            }
            """;

        var (_, output, err) = Run(script, TimeSpan.FromSeconds(180));
        var text = (output ?? "").Trim();

        if (text.StartsWith("OK", StringComparison.Ordinal))
            return (true, $"已创建还原点：{desc}");

        var detail = text.StartsWith("ERR ", StringComparison.Ordinal) ? text[4..] : (string.IsNullOrWhiteSpace(text) ? err : text);
        return (false, string.IsNullOrWhiteSpace(detail) ? "创建还原点失败（没有返回信息）" : detail.Trim());
    }

    /// <summary>删除还原点。用 srclient 的 SRRemoveRestorePoint，返回 0 才算成功。</summary>
    public (bool Ok, string Message) Delete(RestorePointItem point)
    {
        try
        {
            var rc = SRRemoveRestorePoint((int)point.SequenceNumber);
            return rc == 0
                ? (true, $"已删除还原点 #{point.SequenceNumber}")
                : (false, $"删除还原点失败（SRRemoveRestorePoint 返回 {rc}）");
        }
        catch (Exception ex)
        {
            return (false, $"删除还原点异常：{ex.Message}");
        }
    }

    /// <summary>开关系统保护。关闭会把该驱动器上的还原点一并清掉，所以调用方必须先确认。</summary>
    public (bool Ok, string Message) SetProtection(bool enable)
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var cmdlet = enable ? "Enable-ComputerRestore" : "Disable-ComputerRestore";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                {{cmdlet}} -Drive '{{drive}}'
                'OK'
            } catch {
                'ERR ' + $_.Exception.Message
            }
            """;

        var (_, output, err) = Run(script, TimeSpan.FromSeconds(120));
        var text = (output ?? "").Trim();

        if (text.StartsWith("OK", StringComparison.Ordinal))
            return (true, enable ? $"已开启系统保护：{drive}" : $"已关闭系统保护：{drive}");

        var detail = text.StartsWith("ERR ", StringComparison.Ordinal) ? text[4..] : (string.IsNullOrWhiteSpace(text) ? err : text);
        return (false, string.IsNullOrWhiteSpace(detail) ? $"{cmdlet} 执行失败（没有返回信息）" : detail.Trim());
    }

    /// <summary>
    /// 恢复到指定还原点。系统会立即重启，所以调用方必须已经拿到用户确认。
    /// </summary>
    public (bool Ok, string Message) RestoreTo(RestorePointItem point)
    {
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Restore-Computer -RestorePoint {{point.SequenceNumber}} -Confirm:$false
                'OK'
            } catch {
                'ERR ' + $_.Exception.Message
            }
            """;

        var (_, output, err) = Run(script, TimeSpan.FromSeconds(120));
        var text = (output ?? "").Trim();

        if (text.StartsWith("OK", StringComparison.Ordinal))
            return (true, "已发起系统还原，机器即将重启");

        var detail = text.StartsWith("ERR ", StringComparison.Ordinal) ? text[4..] : (string.IsNullOrWhiteSpace(text) ? err : text);
        return (false, string.IsNullOrWhiteSpace(detail) ? "发起系统还原失败（没有返回信息）" : detail.Trim());
    }

    /// <summary>
    /// 跑一段 PowerShell。走 -Command 而不是临时脚本文件：这类系统操作脚本很短，
    /// 落盘反而多一个「脚本被杀软拦下」的失败面。
    /// </summary>
    private static (int Code, string Output, string? Error) Run(string script, TimeSpan timeout)
    {
        var escaped = script.Replace("\"", "\\\"");
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{escaped}\"";

        try
        {
            return AppManagerService
                .RunProcessAsync("powershell.exe", args, timeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
