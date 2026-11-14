using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinKit.Services;

public sealed class ReportItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Level { get; init; } = "";
}

public sealed class ReportSection
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Level { get; init; } = "ok";
    public List<ReportItem> Items { get; init; } = new();
}

public sealed class HealthReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public string Machine { get; init; } = Environment.MachineName;
    public string User { get; init; } = Environment.UserName;
    public int Score { get; init; } = 100;
    public string Grade { get; init; } = "A";
    public List<ReportSection> Sections { get; init; } = new();

    public int WarnCount => Sections.SelectMany(s => s.Items).Count(i => i.Level == "warn");
    public int BadCount => Sections.SelectMany(s => s.Items).Count(i => i.Level == "bad");

    public string SummaryText => BadCount == 0 && WarnCount == 0
        ? "全部检查项正常"
        : $"{BadCount} 项异常 · {WarnCount} 项注意";
}

/// <summary>
/// 体检报告：把各模块的快速检查汇总成一份可保存、可比对的报告。
/// 只跑"几秒内能出结果"的只读检查，不做全盘扫描（重复文件/大文件扫描仍需手动触发）。
/// </summary>
public sealed class HealthReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public HealthReport Build(Action<string>? progress = null, CancellationToken token = default)
    {
        var sections = new List<ReportSection>();

        progress?.Invoke("检查系统与硬件…");
        token.ThrowIfCancellationRequested();
        sections.Add(BuildOverview());

        progress?.Invoke("检查磁盘健康…");
        token.ThrowIfCancellationRequested();
        sections.Add(BuildDisks());

        progress?.Invoke("检查事件日志…");
        token.ThrowIfCancellationRequested();
        sections.Add(BuildEvents());

        progress?.Invoke("检查 Windows 更新…");
        token.ThrowIfCancellationRequested();
        sections.Add(BuildUpdates());

        progress?.Invoke("检查启动项与服务…");
        token.ThrowIfCancellationRequested();
        sections.Add(BuildStartup());

        var bad = sections.SelectMany(s => s.Items).Count(i => i.Level == "bad");
        var warn = sections.SelectMany(s => s.Items).Count(i => i.Level == "warn");

        // 每项异常扣 8 分、注意扣 3 分，下限 0
        var score = Math.Max(0, 100 - bad * 8 - warn * 3);

        return new HealthReport
        {
            Sections = sections,
            Score = score,
            Grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : "D"
        };
    }

    private static ReportSection BuildOverview()
    {
        var cards = new SystemOverviewService().Load();
        var items = new List<ReportItem>();

        foreach (var card in cards)
        {
            foreach (var item in card.Items)
            {
                items.Add(new ReportItem
                {
                    Label = $"{card.Title} · {item.Label}",
                    Value = item.Value,
                    Detail = item.Detail,
                    Level = NormalizeLevel(item.Level)
                });
            }
        }

        return new ReportSection
        {
            Title = "系统概览",
            Summary = $"{cards.Count} 组 / {items.Count} 项",
            Level = Worst(items),
            Items = items
        };
    }

    private static ReportSection BuildDisks()
    {
        var (disks, volumes, error) = new DiskHealthService().Load();
        var items = new List<ReportItem>();

        foreach (var disk in disks)
        {
            items.Add(new ReportItem
            {
                Label = $"磁盘 · {disk.Name}",
                Value = $"{disk.SizeText} · {disk.SmartText} · 磨损 {disk.WearText} · {disk.TemperatureText}",
                Detail = $"通电 {disk.HoursText} · 读写错误 {disk.ErrorText}",
                Level = NormalizeLevel(disk.Level)
            });
        }

        if (disks.Count == 0 && error is not null)
        {
            items.Add(new ReportItem
            {
                Label = "磁盘 · SMART",
                Value = "不可用",
                Detail = error,
                Level = ""
            });
        }

        foreach (var volume in volumes)
        {
            items.Add(new ReportItem
            {
                Label = $"卷 · {volume.Name} {volume.Label}",
                Value = volume.UsageText,
                Level = NormalizeLevel(volume.Level)
            });
        }

        return new ReportSection
        {
            Title = "磁盘健康",
            Summary = DiskHealthService.Summarize(disks, volumes),
            Level = Worst(items),
            Items = items
        };
    }

    private static ReportSection BuildEvents()
    {
        var (groups, error) = new EventLogService().Load(days: 7, maxPerLog: 300);
        var items = new List<ReportItem>();

        foreach (var group in groups.Take(10))
        {
            items.Add(new ReportItem
            {
                Label = group.Category,
                Value = $"{group.Provider} #{group.EventId} · {group.CountText}",
                Detail = $"最后出现 {group.LastText}",
                // 事件量大的分类不代表本机有病，只把“蓝屏/应用崩溃/更新失败”当成注意项
                Level = group.Category is "蓝屏 / 意外关机" ? "bad"
                    : group.Category is "应用崩溃" or "更新失败" or "磁盘 / 存储" ? "warn"
                    : ""
            });
        }

        if (groups.Count == 0 && error is not null)
        {
            items.Add(new ReportItem { Label = "事件日志", Value = "读取失败", Detail = error });
        }

        return new ReportSection
        {
            Title = "事件日志（近 7 天）",
            Summary = groups.Count == 0
                ? "没有错误或严重级别事件"
                : $"{groups.Sum(g => g.Count)} 条错误，归并为 {groups.Count} 种",
            Level = Worst(items),
            Items = items
        };
    }

    private static ReportSection BuildUpdates()
    {
        var service = new WindowsUpdateService(new Core.ChangeJournal());
        var state = service.LoadState();
        var items = new List<ReportItem>();

        items.Add(new ReportItem
        {
            Label = "更新暂停",
            Value = state.PausedText,
            Level = state.PausedUntil is not null ? "warn" : "ok"
        });

        items.Add(new ReportItem
        {
            Label = "更新服务",
            Value = $"{state.ServiceStateText}{(state.ServiceRunning ? "（运行中）" : "（未运行）")}",
            Level = state.ServiceRunning ? "ok" : ""
        });

        items.Add(new ReportItem
        {
            Label = "下载缓存",
            Value = state.UpdateCacheText
        });

        items.Add(new ReportItem
        {
            Label = "待重启",
            Value = service.PendingReboot() ? "有挂起的重启" : "无",
            Level = service.PendingReboot() ? "warn" : "ok"
        });

        var policy = service.PolicyNote();
        if (!string.IsNullOrWhiteSpace(policy))
            items.Add(new ReportItem { Label = "组策略", Value = policy! });

        return new ReportSection
        {
            Title = "Windows 更新",
            Summary = state.PausedUntil is null ? "未暂停" : state.PausedText,
            Level = Worst(items),
            Items = items
        };
    }

    private static ReportSection BuildStartup()
    {
        var journal = new Core.ChangeJournal();
        var manager = new StartupServiceManager(journal);
        var items = new List<ReportItem>();

        try
        {
            var startup = manager.LoadStartupEntries();
            var missing = startup.Count(i => !string.IsNullOrWhiteSpace(i.Command) && !CommandExists(i.Command));

            items.Add(new ReportItem
            {
                Label = "开机启动项",
                Value = $"{startup.Count} 项",
                Detail = missing == 0 ? "" : $"{missing} 项指向的命令已不存在",
                Level = missing == 0 ? "ok" : "warn"
            });
        }
        catch (Exception ex)
        {
            items.Add(new ReportItem { Label = "开机启动项", Value = "读取失败", Detail = ex.Message });
        }

        return new ReportSection
        {
            Title = "启动项",
            Summary = items.FirstOrDefault()?.Value ?? "",
            Level = Worst(items),
            Items = items
        };
    }

    private static bool CommandExists(string command)
    {
        try
        {
            var exe = ContextMenuService.ExtractExecutable(command);
            if (string.IsNullOrWhiteSpace(exe)) return true;
            if (exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return true;

            return System.IO.File.Exists(Environment.ExpandEnvironmentVariables(exe));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string NormalizeLevel(string level) =>
        level is "ok" or "warn" or "bad" ? level : "";

    private static string Worst(IEnumerable<ReportItem> items)
    {
        var list = items.ToList();
        if (list.Any(i => i.Level == "bad")) return "bad";
        if (list.Any(i => i.Level == "warn")) return "warn";
        return "ok";
    }

    /// <summary>导出 Markdown：给人看，也能直接贴到 issue / 笔记里。</summary>
    public static string ToMarkdown(HealthReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# WinKit 体检报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 计算机：{report.Machine}（{report.User}）");
        sb.AppendLine($"- 评分：**{report.Score} / 100（{report.Grade} 级）**");
        sb.AppendLine($"- 结论：{report.SummaryText}");
        sb.AppendLine();

        foreach (var section in report.Sections)
        {
            var badge = section.Level switch { "bad" => "❌", "warn" => "⚠️", _ => "✅" };
            sb.AppendLine($"## {badge} {section.Title}");
            sb.AppendLine();
            sb.AppendLine($"> {section.Summary}");
            sb.AppendLine();
            sb.AppendLine("| 检查项 | 结果 | 说明 |");
            sb.AppendLine("|---|---|---|");

            foreach (var item in section.Items)
            {
                var level = item.Level switch { "bad" => "❌", "warn" => "⚠️", "ok" => "✅", _ => "" };
                var value = item.Value.Replace("|", "\\|");
                var detail = item.Detail.Replace("|", "\\|");
                var mark = string.IsNullOrEmpty(level) ? "" : level + " ";
                sb.AppendLine($"| {item.Label} | {mark}{value} | {detail} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("由 WinKit 生成。报告只包含只读检查结果，未做任何修改。");
        return sb.ToString();
    }

    public static string ToJson(HealthReport report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>默认保存位置：%LOCALAPPDATA%\WinKit\Reports。</summary>
    public static string ReportDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "Reports");
}
