using System.Diagnostics;

namespace WinKit.Services;

/// <summary>进程列表里的一行。字段都是枚举时一次抓好的快照，之后不再回查——进程可能已经退出。</summary>
public sealed class ProcessItem
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Company { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public long WorkingSetBytes { get; init; }
    public int ThreadCount { get; init; }
    public string StartTimeText { get; init; } = "";
    public long? StartTimeUtcTicks { get; init; }

    public string MemoryText => ThisPcService.FormatSize(WorkingSetBytes);

    /// <summary>按名字/描述/公司/路径/PID 一起做一次小写包含匹配，筛选框只写一段字符串就够用。</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;

        var key = keyword.Trim();
        if (int.TryParse(key, out var pid)) return Pid == pid;

        return Name.Contains(key, StringComparison.OrdinalIgnoreCase)
               || Description.Contains(key, StringComparison.OrdinalIgnoreCase)
               || Company.Contains(key, StringComparison.OrdinalIgnoreCase)
               || ExecutablePath.Contains(key, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 进程枚举与结束。
///
/// 只用 System.Diagnostics.Process，不碰 WMI：枚举几百个进程时 WMI 慢一个数量级，
/// 而这里要的信息（名字、内存、线程数、启动时间、映像路径）Process 全都有。
/// 读取某些系统进程的 MainModule 会抛权限异常，这很正常，跳过即可。
/// </summary>
public sealed class ProcessService
{
    public (List<ProcessItem> Items, string? Error) Load()
    {
        var items = new List<ProcessItem>();
        var denied = 0;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startTime = ReadStartTime(process);
                    items.Add(new ProcessItem
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        Description = ReadDescription(process),
                        Company = ReadCompany(process),
                        ExecutablePath = ReadPath(process),
                        WorkingSetBytes = process.WorkingSet64,
                        ThreadCount = process.Threads.Count,
                        StartTimeText = startTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        StartTimeUtcTicks = startTime?.Ticks
                    });
                }
                catch (Exception)
                {
                    // 进程恰好在枚举途中退出，或受保护进程拒绝读取——都不是错误。
                    denied++;
                }
            }
        }

        var list = items.OrderByDescending(i => i.WorkingSetBytes).ToList();
        var error = denied > 0 ? $"有 {denied} 个进程的信息读不到（权限不足或已退出），已跳过" : null;
        return (list, error);
    }

    public (bool Ok, string Message) Kill(ProcessItem item, bool wholeTree)
        => TerminateProcess(item.Pid, item.StartTimeUtcTicks, wholeTree);

    /// <summary>expectedStartTimeUtcTicks 必须来自查询快照，不能在执行结束时临时捕获。</summary>
    public static (bool Ok, string Message) TerminateProcess(
        int processId, long? expectedStartTimeUtcTicks, bool wholeTree = false)
    {
        if (processId == Environment.ProcessId)
            return (false, "不能结束 WinKit 自己");
        if (processId <= 4)
            return (false, "无效 PID 或系统保留进程，不能结束");
        if (expectedStartTimeUtcTicks is null or <= 0)
            return (false, "无法确认进程启动身份，请刷新查询后重试");

        try
        {
            using var process = Process.GetProcessById(processId);
            // 先持有该实例的句柄；身份核对和终止使用同一进程实例，避免 PID 再次复用。
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks.Value)
                return (false, $"PID {processId} 的进程身份已变化，已拒绝结束，请刷新查询后重试");
            if (process.HasExited)
                return (false, $"PID {processId} 已退出，请刷新查询");
            process.Kill(wholeTree);
            return process.WaitForExit(3000)
                ? (true, $"已结束进程（PID {processId}）")
                : (false, $"已发出结束请求，但 PID {processId} 在 3 秒内没有退出，请刷新确认");
        }
        catch (ArgumentException)
        {
            return (false, $"PID {processId} 已不存在，请刷新查询");
        }
        catch (Exception ex)
        {
            return (false, $"无法确认或结束 PID {processId}：{ex.Message}。请刷新查询后重试");
        }
    }

    private static string ReadDescription(Process process)
    {
        try
        {
            return process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } text
                ? text
                : process.ProcessName;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string ReadCompany(Process process)
    {
        try
        {
            return process.MainModule?.FileVersionInfo.CompanyName ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string ReadPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static DateTime? ReadStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception) { return null; }
    }

    public static string Summarize(IReadOnlyList<ProcessItem> items)
    {
        if (items.Count == 0) return "没有进程";

        var totalMemory = items.Sum(i => i.WorkingSetBytes);
        return $"共 {items.Count} 个进程，占用内存合计 {ThisPcService.FormatSize(totalMemory)}";
    }
}
