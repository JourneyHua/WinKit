using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace WinKit.Services;

public sealed class MaintenanceStatus
{
    public bool Known { get; init; }
    public bool AccessDenied { get; init; }
    public bool TaskExists { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsRunning { get; init; }
    public DateTime? LastRunAt { get; init; }
    public string LastRunText => LastRunAt?.ToString("g") ?? "";
    public string LastResultText { get; init; } = "";
    public string NextRunText { get; init; } = "";
    public string LastOutcome { get; init; } = "";
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

    private const int TaskQueued = 2, TaskRunning = 4;
    private const int SchedulerNotRun = 0x41303;
    private const int TaskNotFound = unchecked((int)0x80070002);

    public MaintenanceStatus LoadStatus()
    {
        try
        {
            return WithTask<MaintenanceStatus>((_, taskObject) =>
            {
                var lastOutcome = LastOutcomeText();
                if (taskObject is null) return new MaintenanceStatus { Known = true, LastOutcome = lastOutcome };

                dynamic task = taskObject;
                int state = task.State;
                bool enabled = task.Enabled;
                DateTime last = task.LastRunTime;
                DateTime next = task.NextRunTime;
                int result = task.LastTaskResult;
                return new MaintenanceStatus
                {
                    Known = true, TaskExists = true, IsEnabled = enabled,
                    IsRunning = state is TaskQueued or TaskRunning,
                    LastRunAt = result == SchedulerNotRun || last.Year < 2000 ? null : last,
                    NextRunText = enabled && next.Year >= 2000 ? next.ToString("g") : "",
                    LastResultText = result switch
                    {
                        SchedulerNotRun => "",
                        0 => "已完成",
                        0x41301 => "正在运行",
                        _ => $"未成功（0x{result:X8}）"
                    },
                    LastOutcome = lastOutcome
                };
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005))
        {
            return new MaintenanceStatus { AccessDenied = true, Note = "Windows 拒绝读取维护安排。请以管理员身份重新启动后查看。" };
        }
        catch (Exception ex)
        {
            // 查询失败不等于任务不存在，避免给出覆盖或误清理入口。
            return new MaintenanceStatus { Note = $"未能读取维护状态。{ex.Message}" };
        }
    }

    private static T WithTask<T>(Func<object, object?, T> action)
    {
        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows 任务计划程序不可用。");
        dynamic scheduler = Activator.CreateInstance(schedulerType)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder(@"\");
        object? task;
        try { task = (object)folder.GetTask(TaskName); }
        // COM interop can translate ERROR_FILE_NOT_FOUND into FileNotFoundException rather than COMException.
        catch (Exception ex) when (ex.HResult == TaskNotFound) { task = null; }
        return action((object)folder, task);
    }

    public (bool Ok, string Message) Register(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return (false, "无法定位 WinKit.exe 路径");
        var before = LoadStatus();
        if (!before.Known) return (false, before.Note);
        if (before.TaskExists) return (false, "维护安排已变化，未覆盖现有任务。");

        // /TR 需要整体加引号，路径里有空格时必须再套一层。
        var target = $"\\\"{exe}\\\" --maintain";
        var (code, output, err) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"{target}\" /SC WEEKLY /D SUN /ST 03:00 /RL HIGHEST",
            TimeSpan.FromSeconds(20));

        if (code != 0)
        {
            var text = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
            if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase))
                return (false, "启用每周维护需要管理员权限。");
            return (false, string.IsNullOrWhiteSpace(text) ? "Windows 未能创建维护任务。" : text);
        }

        var state = LoadStatus();
        return state.Known && state.TaskExists && state.IsEnabled
            ? (true, "已启用每周维护。")
            : (false, "Windows 已接受创建请求，但暂未确认维护已启用。请刷新维护状态。");
    }

    public (bool Ok, string Message) Delete() => RunExistingTaskAction((folderObject, taskObject) =>
    {
        dynamic folder = folderObject;
        dynamic task = taskObject;
        int state = task.State;
        if (state is TaskQueued or TaskRunning) return (false, "维护正在运行，等完成后再关闭每周维护。");

        return BackupThenDelete(() => (string)task.Xml, TaskBackupDirectory, () => folder.DeleteTask(TaskName, 0));
    });

    internal static (bool Ok, string Message) BackupThenDelete(Func<string> readXml, string directory, Action delete)
    {
        try
        {
            var doc = XDocument.Parse(readXml().TrimStart('﻿'), LoadOptions.PreserveWhitespace);
            if (doc.Root?.Name != XName.Get("Task", "http://schemas.microsoft.com/windows/2004/02/mit/task"))
                return (false, "Windows 未提供有效的任务备份；维护安排未更改。");

            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{TaskName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.xml");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var settings = new System.Xml.XmlWriterSettings { Encoding = Encoding.Unicode, Indent = false };
                using (var writer = System.Xml.XmlWriter.Create(file, settings)) doc.Save(writer);
                file.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex)
        {
            return (false, $"未能保存任务备份，维护安排未更改。{ex.Message}");
        }

        delete();
        return (true, "已关闭每周维护，原安排已备份。");
    }

    public (bool Ok, string Message) Enable() => RunExistingTaskAction((_, taskObject) =>
    {
        dynamic task = taskObject;
        if (!TargetsCurrentWinKit((string)task.Xml))
            return (false, "已有安排未指向当前 WinKit。请在 Windows 任务计划程序中确认原安排；未启用任务。");
        task.Enabled = true;
        return (true, "已恢复 Windows 中的维护安排。");
    });

    public (bool Ok, string Message) RunNow() => RunExistingTaskAction((_, taskObject) =>
    {
        dynamic task = taskObject;
        if (!(bool)task.Enabled) return (false, "每周维护已被停用，未启动维护。");
        if (!TargetsCurrentWinKit((string)task.Xml))
            return (false, "维护任务未指向当前 WinKit。请在 Windows 任务计划程序中核对，未运行任务。");
        int state = task.State;
        if (state is TaskQueued or TaskRunning) return (false, "维护已经在运行，无需再启动。");
        task.Run(null);
        return (true, "已请求立即维护，正在等待结果…");
    });

    internal static bool TargetsCurrentWinKit(string xml, string? processPath = null)
    {
        var executable = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var actions = XDocument.Parse(xml.TrimStart('﻿')).Root?.Element(ns + "Actions")?.Elements().ToArray();
            if (actions is not [var action] || action.Name != ns + "Exec") return false;
            var command = ((string?)action.Element(ns + "Command"))?.Trim().Trim('"');
            var arguments = ((string?)action.Element(ns + "Arguments"))?.Trim();
            return !string.IsNullOrWhiteSpace(command) && Path.IsPathFullyQualified(command)
                && string.Equals(Path.GetFullPath(command), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
                && arguments == "--maintain";
        }
        catch (Exception) { return false; }
    }

    private static (bool Ok, string Message) RunExistingTaskAction(Func<object, object, (bool, string)> action)
    {
        try
        {
            return WithTask<(bool, string)>((folder, task) => task is not null
                ? action(folder, task)
                : (false, "维护安排已变化，未找到该任务。"));
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Windows 拒绝更改维护安排。请以管理员身份重新启动 WinKit 后重试。");
        }
        catch (Exception ex)
        {
            return (false, $"Windows 未能完成维护操作：{ex.Message}");
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
            var (freed, count, skipped) = cleaner.CleanTargetsAsync(targets, minAgeMinutes: 120)
                .GetAwaiter()
                .GetResult();

            log.AppendLine($"  清理：{targets.Count} 类目标，删除 {count} 个文件，释放 {ThisPcService.FormatSize(freed)}（仅 2 小时前的文件），跳过 {skipped} 项");
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
