using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinKit.Core;

namespace WinKit.Services;

public sealed class StartupEntryItem : ObservableObject
{
    private bool _isEnabled = true;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Command { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string LocationTitle { get; init; } = "";
    public HiveKind Hive { get; init; }
    public string SubKey { get; init; } = "";
    public string ApprovedSubKey { get; init; } = "";
    public string ValueName { get; init; } = "";
    public bool IsStartupFolder { get; init; }
    public string ShortcutPath { get; init; } = "";
    public bool FileExists { get; init; } = true;
    public bool IsMicrosoft { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value == _isEnabled) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<StartupEntryItem, bool>? ToggleRequested { get; set; }

    public string StatusText => !FileExists
        ? "文件不存在（失效）"
        : IsEnabled ? "已启用" : "已禁用";

    public string CopyablePath => IsStartupFolder ? ShortcutPath : RegPath.Of(Hive, SubKey).FullPath;

    public bool HasExecutable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);

    public string SearchBlob => $"{Name} {Publisher} {Command} {LocationTitle}".ToLowerInvariant();
}

public sealed class WinServiceItem : ObservableObject
{
    private int _startType;
    private bool _isRunning;

    public string ServiceName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ImagePath { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string Publisher { get; init; } = "";
    public bool IsMicrosoft { get; init; }
    public string SubKey => $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";
    public string RegistryPath => $@"HKEY_LOCAL_MACHINE\{SubKey}";

    public int StartType
    {
        get => _startType;
        set
        {
            if (Set(ref _startType, value))
                OnPropertyChanged(nameof(StartTypeText));
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(RunToggleText));
            }
        }
    }

    public string StartTypeText => StartType switch
    {
        2 => "自动",
        3 => "手动",
        4 => "禁用",
        _ => $"其他({StartType})"
    };

    public string StateText => IsRunning ? "正在运行" : "已停止";

    public string RunToggleText => IsRunning ? "停止" : "启动";

    public string SearchBlob => $"{ServiceName} {DisplayName} {Publisher} {ImagePath} {Description}".ToLowerInvariant();
}

public sealed class ScheduledTaskItem : ObservableObject
{
    private bool _isEnabled;
    private string _stateText = "";

    public string FullPath => $"{TaskPath}{TaskName}";
    public string TaskPath { get; init; } = "";
    public string TaskName { get; init; } = "";
    public string Author { get; init; } = "";
    public string Command { get; init; } = "";
    public bool IsMicrosoft { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                OnPropertyChanged(nameof(ToggleButtonText));
        }
    }

    public string StateText
    {
        get => _stateText;
        set => Set(ref _stateText, value);
    }

    public string ToggleButtonText => IsEnabled ? "禁用" : "启用";

    public string SearchBlob => $"{TaskPath} {TaskName} {Author} {Command}".ToLowerInvariant();
}

public sealed class StartupServiceManager
{
    private readonly ChangeJournal _journal;

    public StartupServiceManager(ChangeJournal journal) => _journal = journal;

    public List<StartupEntryItem> LoadStartupEntries()
    {
        var list = new List<StartupEntryItem>();

        var regSources = new (HiveKind Hive, string SubKey, string ApprovedSubKey, string Title)[]
        {
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "当前用户 Run"),
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "当前用户 RunOnce"),
            (HiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "系统全局 Run (x64)"),
            (HiveKind.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "系统全局 Run (x86)")
        };

        foreach (var (hive, subKey, approvedSubKey, title) in regSources)
        {
            var path = RegPath.Of(hive, subKey);
            foreach (var name in Reg.ValueNames(path))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var cmd = (Reg.GetString(path, name) ?? "").Trim();
                if (string.IsNullOrWhiteSpace(cmd)) continue;

                var exe = ContextMenuService.ExtractExecutable(Environment.ExpandEnvironmentVariables(cmd)) ?? "";
                var exists = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
                var (publisher, isMs) = InspectBinaryPublisher(exe);
                var enabled = ReadStartupApprovedState(hive, approvedSubKey, name);

                list.Add(new StartupEntryItem
                {
                    Id = $"reg:{hive}:{subKey}:{name}",
                    Name = name,
                    Publisher = publisher,
                    Command = cmd,
                    ExecutablePath = exe,
                    LocationTitle = title,
                    Hive = hive,
                    SubKey = subKey,
                    ApprovedSubKey = approvedSubKey,
                    ValueName = name,
                    FileExists = exists,
                    IsMicrosoft = isMs,
                    IsEnabled = enabled
                });
            }
        }

        // 读取开始菜单「启动」文件夹
        var folderSources = new (Environment.SpecialFolder Folder, HiveKind Hive, string ApprovedSubKey, string Title)[]
        {
            (Environment.SpecialFolder.Startup, HiveKind.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "用户启动文件夹"),
            (Environment.SpecialFolder.CommonStartup, HiveKind.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", "系统公共启动文件夹")
        };

        foreach (var (special, hive, approvedSubKey, title) in folderSources)
        {
            try
            {
                var dir = Environment.GetFolderPath(special);
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir))
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    var enabled = ReadStartupApprovedState(hive, approvedSubKey, fileName);
                    var (publisher, isMs) = InspectBinaryPublisher(file);

                    list.Add(new StartupEntryItem
                    {
                        Id = $"folder:{file}",
                        Name = Path.GetFileNameWithoutExtension(file),
                        Publisher = publisher,
                        Command = file,
                        ExecutablePath = file,
                        LocationTitle = title,
                        Hive = hive,
                        SubKey = approvedSubKey,
                        ApprovedSubKey = approvedSubKey,
                        ValueName = fileName,
                        IsStartupFolder = true,
                        ShortcutPath = file,
                        FileExists = File.Exists(file),
                        IsMicrosoft = isMs,
                        IsEnabled = enabled
                    });
                }
            }
            catch (Exception) { }
        }

        return list
            .OrderByDescending(i => !i.FileExists)
            .ThenByDescending(i => i.IsEnabled)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool ReadStartupApprovedState(HiveKind hive, string approvedSubKey, string valueName)
    {
        try
        {
            using var root = hive == HiveKind.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.OpenSubKey(approvedSubKey, false);
            if (key?.GetValue(valueName) is byte[] bytes && bytes.Length >= 4)
            {
                // Windows Task Manager 规范：首字节偶数(0x02/0x06)表示启用，奇数(0x03/0x01)表示禁用
                return (bytes[0] & 1) == 0;
            }
        }
        catch (Exception) { }
        return true;
    }

    public void SetStartupEnabled(StartupEntryItem item, bool enabled)
    {
        var tracked = new[] { (item.Hive, item.ApprovedSubKey) };
        _journal.Record($"{(enabled ? "启用" : "禁用")}开机启动项：{item.Name}", tracked, () =>
        {
            using var root = item.Hive == HiveKind.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.CreateSubKey(item.ApprovedSubKey, true);
            var data = new byte[12];
            data[0] = enabled ? (byte)0x02 : (byte)0x03;
            if (!enabled)
            {
                var fileTime = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                Array.Copy(fileTime, 0, data, 4, 8);
            }
            key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
        });
    }

    public void DeleteStartupItem(StartupEntryItem item)
    {
        if (item.IsStartupFolder)
        {
            _journal.Record(
                $"删除启动文件夹项：{item.Name}",
                new[] { (item.Hive, item.ApprovedSubKey) },
                () =>
                {
                    if (File.Exists(item.ShortcutPath))
                        File.Delete(item.ShortcutPath);
                    Reg.DeleteValue(RegPath.Of(item.Hive, item.ApprovedSubKey), item.ValueName);
                },
                new[] { item.ShortcutPath });
            return;
        }

        _journal.Record(
            $"删除注册表启动项：{item.Name}",
            new[] { (item.Hive, item.SubKey), (item.Hive, item.ApprovedSubKey) },
            () =>
            {
                Reg.DeleteValue(RegPath.Of(item.Hive, item.SubKey), item.ValueName);
                Reg.DeleteValue(RegPath.Of(item.Hive, item.ApprovedSubKey), item.ValueName);
            });
    }

    public async Task<List<WinServiceItem>> LoadServicesAsync()
    {
        var runningSet = await GetRunningServiceNamesAsync();
        return await Task.Run(() =>
        {
            var list = new List<WinServiceItem>();
            var servicesRoot = RegPath.Hklm(@"SYSTEM\CurrentControlSet\Services");

            foreach (var svcName in Reg.SubKeyNames(servicesRoot))
            {
                var p = RegPath.Hklm($@"SYSTEM\CurrentControlSet\Services\{svcName}");
                var type = Reg.GetInt(p, "Type") ?? 0;
                // 仅保留 Win32 服务 (0x10 OwnProcess / 0x20 ShareProcess，忽略内核驱动 0x01/0x02)
                var baseType = type & 0xFF;
                if (baseType != 0x10 && baseType != 0x20) continue;

                var start = Reg.GetInt(p, "Start") ?? 3;
                if (start is < 2 or > 4) continue;

                var imagePath = (Reg.GetString(p, "ImagePath") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(imagePath)) continue;

                var rawDisplay = Reg.GetString(p, "DisplayName");
                var display = Native.ResolveIndirectString(rawDisplay);
                if (string.IsNullOrWhiteSpace(display))
                {
                    display = !string.IsNullOrWhiteSpace(rawDisplay) && !rawDisplay!.StartsWith('@')
                        ? rawDisplay.Trim()
                        : svcName;
                }

                var rawDesc = Reg.GetString(p, "Description");
                var desc = Native.ResolveIndirectString(rawDesc);
                if (string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(rawDesc) && !rawDesc!.StartsWith('@'))
                    desc = rawDesc.Trim();

                var expandedImg = Environment.ExpandEnvironmentVariables(imagePath);
                var exe = ContextMenuService.ExtractExecutable(expandedImg) ?? "";
                var (publisher, isMs) = InspectServicePublisher(exe, expandedImg);

                list.Add(new WinServiceItem
                {
                    ServiceName = svcName,
                    DisplayName = display!,
                    Description = desc ?? "",
                    ImagePath = imagePath,
                    ExecutablePath = exe,
                    Publisher = publisher,
                    IsMicrosoft = isMs,
                    StartType = start,
                    IsRunning = runningSet.Contains(svcName)
                });
            }

            return list
                .OrderByDescending(s => s.IsRunning)
                .ThenBy(s => s.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        });
    }

    private static async Task<HashSet<string>> GetRunningServiceNamesAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (_, stdout, _) = await AppManagerService.RunProcessAsync("sc.exe", "query type= service state= active", TimeSpan.FromSeconds(10));
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                var name = trimmed["SERVICE_NAME:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
            }
        }
        return set;
    }

    public void SetServiceStartType(WinServiceItem item, int newStartType)
    {
        var typeLabel = newStartType switch { 2 => "自动", 3 => "手动", 4 => "禁用", _ => newStartType.ToString() };
        _journal.Record(
            $"设置服务「{item.DisplayName}」启动类型为：{typeLabel}",
            new[] { (HiveKind.LocalMachine, item.SubKey) },
            () =>
            {
                Reg.SetDword(RegPath.Hklm(item.SubKey), "Start", newStartType);
            });
        item.StartType = newStartType;
    }

    public async Task<(bool Success, string Message)> StartOrStopServiceAsync(WinServiceItem item, bool start)
    {
        var verb = start ? "start" : "stop";
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "sc.exe", $"{verb} \"{item.ServiceName}\"", TimeSpan.FromSeconds(25));

        // 只认退出码：sc 失败时也会把 `STATE : 4 RUNNING` 打到 stdout，
        // 按子串判定会把“停止失败”报成已停止，界面与实际状态相反。
        if (code == 0)
        {
            item.IsRunning = start;
            return (true, $"已{(start ? "启动" : "停止")}服务：{item.DisplayName}");
        }

        var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
        return (false, detail.Length == 0 ? $"sc {verb} 返回失败（错误码 {code}）" : detail);
    }

    public async Task<List<ScheduledTaskItem>> LoadScheduledTasksAsync()
    {
        const string script = """
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object {
                $act = ''
                if ($_.Actions -and $_.Actions.Count -gt 0) {
                    $act = ($_.Actions[0].Execute + ' ' + $_.Actions[0].Arguments).Trim()
                }
                [PSCustomObject]@{
                    TaskPath = [string]$_.TaskPath
                    TaskName = [string]$_.TaskName
                    State    = [string]$_.State
                    Author   = [string]$_.Author
                    Command  = [string]$act
                }
            }
            if ($tasks) { ConvertTo-Json -InputObject @($tasks) -Compress } else { '[]' }
            """;

        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var (code, stdout, _) = await AppManagerService.RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            TimeSpan.FromSeconds(25));

        var list = new List<ScheduledTaskItem>();
        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return list;

        try
        {
            var jsonStart = stdout.IndexOf('[');
            var jsonEnd = stdout.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd <= jsonStart) return list;

            using var doc = JsonDocument.Parse(stdout.Substring(jsonStart, jsonEnd - jsonStart + 1));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var path = el.TryGetProperty("TaskPath", out var p) ? p.GetString() ?? "" : "";
                var name = el.TryGetProperty("TaskName", out var n) ? n.GetString() ?? "" : "";
                var state = el.TryGetProperty("State", out var s) ? s.GetString() ?? "" : "";
                var author = el.TryGetProperty("Author", out var a) ? a.GetString() ?? "" : "";
                var cmd = el.TryGetProperty("Command", out var c) ? c.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(name)) continue;

                var isMs = path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
                           || author.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
                var isEnabled = !state.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                                && !state.Equals("1", StringComparison.OrdinalIgnoreCase);

                list.Add(new ScheduledTaskItem
                {
                    TaskPath = path,
                    TaskName = name,
                    Author = string.IsNullOrWhiteSpace(author) ? "未知" : author,
                    Command = cmd,
                    IsMicrosoft = isMs,
                    IsEnabled = isEnabled,
                    StateText = isEnabled ? "已启用" : "已禁用"
                });
            }
        }
        catch (Exception) { }

        return list
            .OrderByDescending(t => t.IsEnabled)
            .ThenBy(t => t.TaskName, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<(bool Success, string Message)> SetTaskEnabledAsync(ScheduledTaskItem task, bool enable)
    {
        var flag = enable ? "/ENABLE" : "/DISABLE";

        // 计划任务的定义文件在 C:\Windows\System32\Tasks\<任务路径>，schtasks 改的就是它。
        // 不把它纳入快照，用户点“撤销上一步”时任务状态不会回滚，与界面承诺不符。
        var taskFile = ResolveTaskDefinitionFile(task.FullPath);
        var title = $"{(enable ? "启用" : "禁用")}计划任务：{task.TaskName}";

        try
        {
            if (taskFile is not null)
            {
                await _journal.RecordAsync(
                    title,
                    Array.Empty<(HiveKind, string)>(),
                    async () =>
                    {
                        var (code, output, err) = await AppManagerService.RunProcessAsync(
                            "schtasks.exe", $"/Change /TN \"{task.FullPath}\" {flag}", TimeSpan.FromSeconds(15));

                        if (code != 0)
                            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
                    },
                    new[] { taskFile });
            }
            else
            {
                // 读不到定义文件（受保护任务/权限不足）时仍然执行，但要如实说明不可撤销
                var (code, output, err) = await AppManagerService.RunProcessAsync(
                    "schtasks.exe", $"/Change /TN \"{task.FullPath}\" {flag}", TimeSpan.FromSeconds(15));

                if (code != 0)
                    return (false, string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
            }

            task.IsEnabled = enable;
            task.StateText = enable ? "已启用" : "已禁用";
            return (true, $"已{(enable ? "启用" : "禁用")}计划任务：{task.TaskName}"
                          + (taskFile is null ? "（读不到任务定义文件，此项无法撤销）" : ""));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>把任务路径映射到 System32\Tasks 下的定义文件；拿不到时返回 null。</summary>
    private static string? ResolveTaskDefinitionFile(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        try
        {
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var file = Path.Combine(winDir, "System32", "Tasks", fullPath.TrimStart('\\'));
            return File.Exists(file) ? file : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message)> RunTaskNowAsync(ScheduledTaskItem task)
    {
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "schtasks.exe", $"/Run /TN \"{task.FullPath}\"", TimeSpan.FromSeconds(15));

        return code == 0
            ? (true, $"已触发立即运行：{task.TaskName}")
            : (false, string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
    }

    private static (string Publisher, bool IsMicrosoft) InspectBinaryPublisher(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return ("未知", false);

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var company = info.CompanyName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(company))
                company = Path.GetFileNameWithoutExtension(exePath);

            var isMs = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                       || exePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
            return (company, isMs);
        }
        catch (Exception)
        {
            return ("未知", false);
        }
    }

    private static (string Publisher, bool IsMicrosoft) InspectServicePublisher(string exePath, string expandedImg)
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(winDir, "System32");
        var sysWow64 = Path.Combine(winDir, "SysWOW64");

        // 只按“文件名 + 所在目录”双重判定：伪装成 svchost.exe 的第三方服务
        // （例如 C:\Users\Public\svchost.exe -k netsvcs）不能被当成微软组件，
        // 否则“仅显示第三方”过滤会把它藏起来，正好帮了恶意软件的忙。
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var inSystemDir = exePath.StartsWith(system32 + "\\", StringComparison.OrdinalIgnoreCase)
                              || exePath.StartsWith(sysWow64 + "\\", StringComparison.OrdinalIgnoreCase);

            if (inSystemDir &&
                (Path.GetFileName(exePath).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase)
                 || File.Exists(exePath)))
            {
                return ("Microsoft Windows", true);
            }

            return InspectBinaryPublisher(exePath);
        }

        // 解析不出 exe 路径时（ImagePath 用变量/引号异常），退回原始 ImagePath 判定
        if (expandedImg.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase)
            && expandedImg.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ("Microsoft Windows", true);
        }

        return ("未知", false);
    }
}
