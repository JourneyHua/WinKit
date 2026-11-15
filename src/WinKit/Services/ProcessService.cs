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
                    items.Add(new ProcessItem
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        Description = ReadDescription(process),
                        Company = ReadCompany(process),
                        ExecutablePath = ReadPath(process),
                        WorkingSetBytes = process.WorkingSet64,
                        ThreadCount = process.Threads.Count,
                        StartTimeText = ReadStartTime(process)
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
    {
        if (item.Pid == Environment.ProcessId)
            return (false, "不能结束 WinKit 自己");

        try
        {
            using var process = Process.GetProcessById(item.Pid);
            process.Kill(wholeTree);

            // 等一会儿再回报：没等到就说明它还在（多半是被系统拦住了），这时候说“已结束”就是骗人。
            return process.WaitForExit(3000)
                ? (true, $"已结束 {item.Name}（PID {item.Pid}）")
                : (false, $"已发出结束请求，但 {item.Name} 在 3 秒内没有退出");
        }
        catch (ArgumentException)
        {
            return (false, $"{item.Name}（PID {item.Pid}）已经不存在了");
        }
        catch (Exception ex)
        {
            return (false, $"结束 {item.Name}（PID {item.Pid}）失败：{ex.Message}");
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

    private static string ReadStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static string Summarize(IReadOnlyList<ProcessItem> items)
    {
        if (items.Count == 0) return "没有进程";

        var totalMemory = items.Sum(i => i.WorkingSetBytes);
        return $"共 {items.Count} 个进程，占用内存合计 {ThisPcService.FormatSize(totalMemory)}";
    }
}
