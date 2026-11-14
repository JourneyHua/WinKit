using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace WinKit.Services;

public sealed class EventLogGroup
{
    public string LogName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string EventId { get; init; } = "";
    public string LevelText { get; init; } = "";
    public int Count { get; init; }
    public string CountText => $"{Count} 次";
    public DateTime FirstTime { get; init; }
    public DateTime LastTime { get; init; }
    public string FirstText => FirstTime == default ? "" : FirstTime.ToString("MM-dd HH:mm");
    public string LastText => LastTime == default ? "" : LastTime.ToString("MM-dd HH:mm");
    public string Sample { get; init; } = "";
    public string Category { get; init; } = "";
}

/// <summary>
/// 事件日志诊断：用系统自带 wevtutil 拉取近 N 天的“错误/严重”事件，按 来源+事件ID 归并。
/// 不引入 EventLog NuGet 包，也不需要管理员权限（System / Application 日志普通用户可读）。
/// </summary>
public sealed class EventLogService
{
    private static readonly string[] Logs = { "System", "Application" };

    public (List<EventLogGroup> Groups, string? Error) Load(int days = 7, int maxPerLog = 400)
    {
        var all = new List<EventLogGroup>();
        var errors = new List<string>();

        foreach (var log in Logs)
        {
            var (entries, error) = Query(log, days, maxPerLog);
            if (error is not null) errors.Add($"{log}：{error}");
            all.AddRange(Group(entries));
        }

        var ordered = all
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastTime)
            .ToList();

        return (ordered, errors.Count == 0 ? null : string.Join("；", errors));
    }

    private static IEnumerable<EventLogGroup> Group(List<RawEvent> entries)
    {
        return entries
            .GroupBy(e => (e.LogName, e.Provider, e.EventId, e.Level))
            .Select(g =>
            {
                var first = g.Min(e => e.Time);
                var last = g.Max(e => e.Time);
                var sample = g.OrderByDescending(e => e.Time).First().Message;

                return new EventLogGroup
                {
                    LogName = g.Key.LogName,
                    Provider = g.Key.Provider,
                    EventId = g.Key.EventId,
                    LevelText = g.Key.Level == 1 ? "严重" : "错误",
                    Count = g.Count(),
                    FirstTime = first,
                    LastTime = last,
                    Sample = sample,
                    Category = Categorize(g.Key.Provider, g.Key.EventId)
                };
            });
    }

    /// <summary>把事件来源归到用户能看懂的中文分类（公开供自测覆盖）。</summary>
    public static string Categorize(string provider, string eventId)
    {
        var p = provider.ToLowerInvariant();

        if (p.Contains("bugcheck") || p.Contains("wer-systemerror") || (eventId == "1001" && p.Contains("wer")))
            return "蓝屏 / 意外关机";
        if (p.Contains("application error") || p.Contains("application hang") || p.Contains(".net runtime")
            || p.Contains("sidebyside") || eventId is "1000" or "1002" or "1026")
            return "应用崩溃";
        if (p.Contains("windowsupdate") || p.Contains("wuauclt") || p.Contains("update orchestrator"))
            return "更新失败";
        if (p.Contains("disk") || p.Contains("ntfs") || p.Contains("volmgr") || p.Contains("volsnap")
            || p.Contains("storahci") || p.Contains("stornvme"))
            return "磁盘 / 存储";
        if (p.Contains("kernel-power") || p.Contains("kernel-boot") || p.Contains("kernel-general"))
            return "电源 / 启动";
        if (p.Contains("netwtw") || p.Contains("tcpip") || p.Contains("dns") || p.Contains("dhcp")
            || p.Contains("ndis") || p.Contains("wlan"))
            return "网络";
        if (p.Contains("defender") || p.Contains("security") || p.Contains("firewall") || p.Contains("lsa"))
            return "安全";
        if (p.Contains("service control manager"))
            return "服务启动失败";
        if (p.Contains("capi2") || p.Contains("schannel") || p.Contains("crypt32"))
            return "证书 / 加密";
        if (p.Contains("distributedcom") || p.Contains("dcom"))
            return "COM 组件";
        if (p.Contains("perflib") || p.Contains("perflogs"))
            return "性能计数器";

        return "其它";
    }

    private static (List<RawEvent> Entries, string? Error) Query(string logName, int days, int max)
    {
        var entries = new List<RawEvent>();
        var windowMs = (long)TimeSpan.FromDays(Math.Max(1, days)).TotalMilliseconds;
        var query = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {windowMs}]]]";

        // wevtutil 的输出是连续拼接的 <Event> 片段（无根节点、带 BOM），
        // 统一走 AppManagerService.RunProcessAsync：它并发读两个流 + 超时杀进程。
        var arguments = $"qe {logName} /q:\"{query.Replace("\"", "'")}\" /f:xml /c:{max} /rd:true";

        try
        {
            var (code, output, error) = AppManagerService
                .RunProcessAsync("wevtutil.exe", arguments, TimeSpan.FromSeconds(30))
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(output))
                return (entries, string.IsNullOrWhiteSpace(error) || code == 0 ? null : error.Trim());

            // wevtutil 输出的是连续拼接的 <Event> 片段，没有根节点，需要自己包一层
            var xml = output.TrimStart('\uFEFF', '\r', '\n', ' ');
            var doc = XDocument.Parse("<Events>" + xml + "</Events>", LoadOptions.PreserveWhitespace);

            // 每个 <Event> 自带默认命名空间，不能按 XNamespace.None 匹配，统一按 LocalName 取
            foreach (var ev in doc.Root!.Elements().Where(e => e.Name.LocalName == "Event"))
            {
                var system = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
                if (system is null) continue;

                var provider = system.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider")?.Attribute("Name")?.Value ?? "未知来源";
                var eventId = system.Elements().FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value ?? "";
                var levelText = system.Elements().FirstOrDefault(e => e.Name.LocalName == "Level")?.Value;
                var level = int.TryParse(levelText, out var lv) ? lv : 2;
                var timeText = system.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;

                var time = DateTime.TryParse(timeText, out var parsed) ? parsed.ToLocalTime() : DateTime.MinValue;

                var data = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData")?
                    .Elements()
                    .Where(d => d.Name.LocalName == "Data")
                    .Select(d => d.Value?.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList() ?? new List<string?>();

                var message = data.Count == 0
                    ? ""
                    : string.Join(" | ", data.Take(4).Select(v => v!.Length > 160 ? v[..160] + "…" : v));

                entries.Add(new RawEvent
                {
                    LogName = logName,
                    Provider = provider,
                    EventId = eventId,
                    Level = level,
                    Time = time,
                    Message = message
                });
            }

            return (entries, null);
        }
        catch (Exception ex)
        {
            return (entries, ex.Message);
        }
    }

    private sealed class RawEvent
    {
        public string LogName { get; init; } = "";
        public string Provider { get; init; } = "";
        public string EventId { get; init; } = "";
        public int Level { get; init; }
        public DateTime Time { get; init; }
        public string Message { get; init; } = "";
    }
}
