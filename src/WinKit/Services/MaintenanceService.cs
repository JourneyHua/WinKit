using System.IO;
using System.Text;

namespace WinKit.Services;

public sealed class MaintenanceStatus
{
    public bool TaskExists { get; init; }
    public string TaskName { get; init; } = MaintenanceService.TaskName;
    public string LastRunText { get; init; } = "";
    public string LastResultText { get; init; } = "";
    public string NextRunText { get; init; } = "";
    public bool LastRunFailed { get; init; }
    public string LogPath { get; init; } = MaintenanceService.LogPath;
    public string LastLogLine { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>
/// 定期维护：注册一个可见、可删除的 Windows 计划任务（每周日 03:00），
/// 以 <c>WinKit.exe --maintain</c> 运行「安全清理 + 生成体检报告」，结果写日志。
/// 不装服务、不常驻；删除前会导出任务 XML 备份。
/// </summary>
public sealed class MaintenanceService
{
    public const string TaskName = "WinKit-WeeklyMaintenance";

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "Logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "scheduled.log");

    public static string TaskBackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "TaskBackups");

    public bool TaskExists()
    {
        var (code, _, _) = RunSchtasks($"/Query /TN \"{TaskName}\"", TimeSpan.FromSeconds(15));
        return code == 0;
    }

    public MaintenanceStatus LoadStatus()
    {
        var exists = TaskExists();
        var status = new MaintenanceStatus { TaskExists = exists };

        if (!exists)
        {
            return new MaintenanceStatus
            {
                TaskExists = false,
                LastLogLine = ReadLastLogLine(),
                Note = "尚未注册定期维护任务"
            };
        }

        var (code, output, _) = RunSchtasks($"/Query /TN \"{TaskName}\" /FO LIST /V", TimeSpan.FromSeconds(15));

        string Read(string key)
        {
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

                var idx = trimmed.IndexOf(':');
                if (idx < 0) continue;
                return trimmed[(idx + 1)..].Trim();
            }

            return "";
        }

        var lastRun = Read("上次运行时间");
        if (string.IsNullOrWhiteSpace(lastRun)) lastRun = Read("Last Run Time");

        var lastResult = Read("上次结果");
        if (string.IsNullOrWhiteSpace(lastResult)) lastResult = Read("Last Result");

        var nextRun = Read("下次运行时间");
        if (string.IsNullOrWhiteSpace(nextRun)) nextRun = Read("Next Run Time");

        // 0 = 成功；267011 = 任务尚未运行过；其它非零视为失败
        var failed = lastResult.Length > 0
                     && lastResult != "0"
                     && lastResult != "267011"
                     && lastResult != "1"; // 1 = 正在运行

        return new MaintenanceStatus
        {
            TaskExists = true,
            LastRunText = lastRun,
            LastResultText = lastResult,
            NextRunText = nextRun,
            LastRunFailed = failed,
            LastLogLine = ReadLastLogLine(),
            Note = code == 0 ? "" : "无法读取任务详情（可能需要管理员权限）"
        };
    }

    public (bool Ok, string Message) Register(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return (false, "无法定位 WinKit.exe 路径");

        // /TR 需要整体加引号，路径里有空格时必须再套一层
        var target = $"\\\"{exe}\\\" --maintain";
        var (code, output, err) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"{target}\" /SC WEEKLY /D SUN /ST 03:00 /RL HIGHEST /F",
            TimeSpan.FromSeconds(20));

        if (code != 0)
        {
            var text = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();

            // 以最高权限运行的任务必须由管理员创建，给出可执行的提示而不是原始英文错误
            if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "创建计划任务被拒绝：需要以管理员身份运行 WinKit 才能注册「以最高权限运行」的定期维护任务。");
            }

            return (false, text);
        }

        // schtasks 偶尔返回 0 但任务并未落地，必须复查
        if (!TaskExists())
            return (false, "schtasks 报告成功，但查询不到该任务，请重试或以管理员身份运行。");

        return (true, "已注册每周维护任务（每周日 03:00 运行：安全清理 + 生成体检报告）");
    }

    public (bool Ok, string Message) Delete(bool exportBackup = true)
    {
        string? backupPath = null;

        if (exportBackup)
        {
            try
            {
                var (code, xml, _) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML", TimeSpan.FromSeconds(15));
                if (code == 0 && !string.IsNullOrWhiteSpace(xml))
                {
                    Directory.CreateDirectory(TaskBackupDirectory);
                    backupPath = Path.Combine(TaskBackupDirectory, $"{TaskName}_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
                    File.WriteAllText(backupPath, xml, new UTF8Encoding(true));
                }
            }
            catch (Exception)
            {
                // 备份失败不阻断删除，但会如实报告
            }
        }

        var (delCode, output, err) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", TimeSpan.FromSeconds(15));

        if (delCode != 0)
            return (false, string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());

        return (true, backupPath is null
            ? "已删除定期维护任务（未能导出 XML 备份）"
            : $"已删除定期维护任务，任务 XML 已备份到：{backupPath}");
    }

    public (bool Ok, string Message) RunNow()
    {
        var (code, output, err) = RunSchtasks($"/Run /TN \"{TaskName}\"", TimeSpan.FromSeconds(15));
        return code == 0
            ? (true, "已触发定期维护任务，稍后可在日志里查看结果")
            : (false, string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
    }

    /// <summary>维护任务写入的一行结果；界面打开时用它提示“上次有失败”。</summary>
    public static string ReadLastLogLine()
    {
        try
        {
            if (!File.Exists(LogPath)) return "";

            var lines = File.ReadAllLines(LogPath);
            return lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>追加一条维护日志（每次运行一个块，便于事后追）。</summary>
    public static void AppendLog(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, text + Environment.NewLine, new UTF8Encoding(true));
        }
        catch (Exception)
        {
            // 日志失败不影响维护本身
        }
    }

    private static (int Code, string Output, string Error) RunSchtasks(string arguments, TimeSpan timeout)
    {
        // 必须放到线程池上跑：这些方法是同步的、且会被 UI 线程直接调用，
        // 而 RunProcessAsync 内部 await 的续体会切回 UI dispatcher，直接 GetResult 会死锁。
        var task = Task.Run(() => AppManagerService.RunProcessAsync(
            "schtasks.exe", arguments, timeout, AppManagerService.GetOemEncoding()));

        var (code, output, err) = task.GetAwaiter().GetResult();
        return (code, output, err ?? "");
    }

    /// <summary>
    /// 无人值守入口（<c>WinKit.exe --maintain</c>）：安全清理 + 生成体检报告 + 写日志。
    /// 只做默认勾选的清理项，且不碰注册表；任何一步失败都记进日志并返回非零退出码。
    /// </summary>
    public static int RunMaintenance()
    {
        var log = new StringBuilder();
        log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 开始定期维护（PID {Environment.ProcessId}）");

        var failures = 0;

        try
        {
            var cleaner = new SystemCleanService();
            var targets = cleaner.BuildTargets()
                .Where(t => t.DefaultSelected && !t.IsRecycleBin)
                .ToList();

            // 无人值守时只删「已经存在 2 小时以上」的临时文件：
            // 凌晨跑时可能有其它进程（构建、安装、沙箱）正在用 %TEMP% 下刚建的目录，
            // 不看年龄会把活跃会话的数据一起删掉。
            var (freed, count) = cleaner.CleanTargetsAsync(targets, minAgeMinutes: 120)
                .GetAwaiter()
                .GetResult();

            log.AppendLine($"  清理：{targets.Count} 类目标，删除 {count} 个文件，释放 {ThisPcService.FormatSize(freed)}（仅 2 小时前的文件）");
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine($"  [FAILED] 清理失败：{ex.Message}");
        }

        try
        {
            var report = new HealthReportService().Build();
            Directory.CreateDirectory(HealthReportService.ReportDirectory);

            var baseName = Path.Combine(HealthReportService.ReportDirectory,
                $"report-{DateTime.Now:yyyyMMdd-HHmmss}");
            File.WriteAllText(baseName + ".md", HealthReportService.ToMarkdown(report), new UTF8Encoding(true));
            File.WriteAllText(baseName + ".json", HealthReportService.ToJson(report), new UTF8Encoding(true));

            log.AppendLine($"  体检：{report.Score}/100（{report.Grade} 级），{report.SummaryText}");
            log.AppendLine($"  报告：{baseName}.md");

            if (report.BadCount > 0) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine($"  [FAILED] 体检报告失败：{ex.Message}");
        }

        log.AppendLine(failures == 0 ? "  结果：成功" : $"  结果：{failures} 项失败");
        AppendLog(log.ToString());

        return failures == 0 ? 0 : 1;
    }

    /// <summary>最近一次运行的结论行（形如「结果：成功」），用于界面提示。</summary>
    public static string LastOutcomeText()
    {
        try
        {
            if (!File.Exists(LogPath)) return "";

            var lines = File.ReadAllLines(LogPath);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].TrimStart().StartsWith("结果：", StringComparison.Ordinal))
                    return lines[i].Trim();
            }
        }
        catch (Exception)
        {
            // 忽略
        }

        return "";
    }
}
