using System.Diagnostics;
using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class WindowsUpdateInfo
{
    public string Title { get; init; } = "";
    public string Kb { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string SeverityText { get; init; } = "";
    public string CategoryText { get; init; } = "";
    public bool IsInstalled { get; init; }
    public DateTime? InstalledOn { get; init; }
    public string InstalledText => InstalledOn is null ? "" : InstalledOn.Value.ToString("yyyy-MM-dd");
}

public sealed class WindowsUpdateState
{
    public bool PauseSupported { get; init; }
    public DateTime? PausedUntil { get; init; }
    public string PausedText => PausedUntil is null ? "未暂停" : $"暂停至 {PausedUntil.Value:yyyy-MM-dd}";
    public string UpdateCachePath { get; init; } = "";
    public long UpdateCacheBytes { get; init; }
    public string UpdateCacheText => ThisPcService.FormatSize(UpdateCacheBytes);
    public string ServiceStateText { get; init; } = "";
    public bool ServiceRunning { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Windows 更新管理：只读列出最近补丁/待安装更新、暂停与恢复、清理下载缓存、重启更新服务。
/// 全部走系统接口（Microsoft.Update.Session + USO 注册表 + net/wuauserv），不下载第三方补丁。
/// </summary>
public sealed class WindowsUpdateService
{
    private const string UsoKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string UsoKeyHkcu = @"Software\Microsoft\WindowsUpdate\UX\Settings";
    private const string WuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string WuKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";
    private const string DownloadFolder = @"C:\Windows\SoftwareDistribution\Download";

    private readonly ChangeJournal _journal;

    public WindowsUpdateService(ChangeJournal journal) => _journal = journal;

    public WindowsUpdateState LoadState()
    {
        var pauseStart = Reg.GetString(RegPath.Hklm(UsoKey), "PauseUpdatesExpiryTime");
        DateTime? pausedUntil = null;
        if (DateTime.TryParse(pauseStart, out var parsed) && parsed > DateTime.Now)
            pausedUntil = parsed;

        var cacheBytes = 0L;
        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                foreach (var file in new DirectoryInfo(DownloadFolder).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    cacheBytes += file.Length;
                }
            }
        }
        catch (Exception)
        {
            // 无权限时按 0 处理
        }

        var running = false;
        try
        {
            running = Process.GetProcessesByName("wuauserv").Length > 0;
        }
        catch (Exception)
        {
            // 忽略
        }

        var serviceText = "未知";
        try
        {
            using var key = Reg.Open(RegPath.Hklm(@"SYSTEM\CurrentControlSet\Services\wuauserv"), false);
            var start = key?.GetValue("Start") is int s ? s : -1;
            serviceText = start switch
            {
                0 => "引导启动",
                1 => "系统启动",
                2 => "自动（触发式）",
                3 => "手动",
                4 => "已禁用",
                _ => "未知"
            };
        }
        catch (Exception)
        {
            // 忽略
        }

        return new WindowsUpdateState
        {
            PauseSupported = Reg.Exists(RegPath.Hklm(UsoKey)) || Reg.Exists(RegPath.Hkcu(UsoKeyHkcu)),
            PausedUntil = pausedUntil,
            UpdateCachePath = DownloadFolder,
            UpdateCacheBytes = cacheBytes,
            ServiceStateText = serviceText,
            ServiceRunning = running,
            Note = running ? "" : "wuauserv 未运行，检查更新时会由系统按需拉起"
        };
    }

    /// <summary>
    /// 列出已安装的补丁。优先读本地卸载登记项（快、不需要提权），
    /// 读不到时再退回 Microsoft.Update.Session（慢，且非管理员会拒绝访问）。
    /// </summary>
    public (List<WindowsUpdateInfo> Installed, string? Error) LoadInstalled(int take = 40)
    {
        var fromRegistry = LoadInstalledFromRegistry(take);
        if (fromRegistry.Count > 0) return (fromRegistry, null);

        return LoadInstalledFromCom(take);
    }

    private static List<WindowsUpdateInfo> LoadInstalledFromRegistry(int take)
    {
        var list = new List<WindowsUpdateInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Win11 的补丁登记在 CBS 包仓库里（Uninstall 里通常已经没有 KB 条目）
        const string cbs = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";
        foreach (var sub in Reg.SubKeyNames(RegPath.Hklm(cbs)))
        {
            var match = System.Text.RegularExpressions.Regex.Match(sub, @"KB\d{6,8}");
            if (!match.Success) continue;
            if (!seen.Add(match.Value)) continue;

            var path = RegPath.Hklm($@"{cbs}\{sub}");
            var state = Reg.GetInt(path, "CurrentState") ?? 0;

            // 0x70 = 已安装；其它状态（已取代/暂存）不列
            if ((state & 0x70) != 0x70) continue;

            DateTime? installed = null;
            var high = Reg.GetInt(path, "InstallTimeHigh");
            var low = Reg.GetInt(path, "InstallTimeLow");
            if (high is not null && low is not null)
            {
                try
                {
                    var fileTime = ((long)(uint)high.Value << 32) | (uint)low.Value;
                    if (fileTime > 0) installed = DateTime.FromFileTime(fileTime);
                }
                catch (Exception)
                {
                    // 时间戳异常时留空
                }
            }

            var releaseType = Reg.GetString(path, "ReleaseType") ?? "";

            list.Add(new WindowsUpdateInfo
            {
                Title = $"{match.Value} 系统更新包",
                Kb = match.Value,
                CategoryText = string.IsNullOrWhiteSpace(releaseType) ? "已安装补丁" : releaseType,
                IsInstalled = true,
                InstalledOn = installed
            });
        }

        // 兜底：老式 Uninstall 登记项
        foreach (var (hive, root) in new[]
                 {
                     (HiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (HiveKind.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            foreach (var sub in Reg.SubKeyNames(RegPath.Of(hive, root)))
            {
                var path = RegPath.Of(hive, $@"{root}\{sub}");
                var name = Reg.GetString(path, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var kbMatch = System.Text.RegularExpressions.Regex.Match(name!, @"KB\d{6,8}");
                var looksLikePatch = kbMatch.Success
                                     || name!.Contains("Update for", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                                     || name.Contains("Security Update", StringComparison.OrdinalIgnoreCase);
                if (!looksLikePatch) continue;
                if (!seen.Add(kbMatch.Success ? kbMatch.Value : name!)) continue;

                DateTime? installed = null;
                var raw = Reg.GetString(path, "InstallDate");
                if (raw is { Length: 8 } && DateTime.TryParseExact(raw, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    installed = parsed;

                list.Add(new WindowsUpdateInfo
                {
                    Title = name!,
                    Kb = kbMatch.Success ? kbMatch.Value : "",
                    CategoryText = "已安装补丁",
                    IsInstalled = true,
                    InstalledOn = installed
                });
            }
        }

        return list
            .OrderByDescending(i => i.InstalledOn ?? DateTime.MinValue)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .Take(take)
            .ToList();
    }

    private static (List<WindowsUpdateInfo> Installed, string? Error) LoadInstalledFromCom(int take)
    {
        var list = new List<WindowsUpdateInfo>();

        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type is null) return (list, "系统未提供 Microsoft.Update.Session 接口");

            dynamic session = Activator.CreateInstance(type)!;
            dynamic searcher = session.CreateUpdateSearcher();

            // 只查本地已装历史，IsInstalled=1 是完全离线的查询
            dynamic result = searcher.Search("IsInstalled=1 and IsHidden=0");
            dynamic updates = result.Updates;

            var count = (int)updates.Count;
            for (var i = 0; i < count && i < take * 4; i++)
            {
                dynamic update = updates.Item(i);
                string title;
                try { title = (string)update.Title; } catch (Exception) { continue; }
                if (string.IsNullOrWhiteSpace(title)) continue;

                var category = "";
                try
                {
                    var cats = update.Categories;
                    var catCount = (int)cats.Count;
                    var names = new List<string>();
                    for (var c = 0; c < catCount; c++) names.Add((string)cats.Item(c).Name);
                    category = string.Join(" / ", names);
                }
                catch (Exception)
                {
                    // 忽略
                }

                DateTime? installedOn = null;
                try
                {
                    var raw = update.LastDeploymentChangeTime;
                    if (raw is DateTime dt) installedOn = dt;
                }
                catch (Exception)
                {
                    // 忽略
                }

                var kb = "";
                try
                {
                    dynamic kbs = update.KBArticleIDs;
                    var kbCount = (int)kbs.Count;
                    var ids = new List<string>();
                    for (var k = 0; k < kbCount; k++) ids.Add("KB" + kbs.Item(k));
                    kb = string.Join(", ", ids);
                }
                catch (Exception)
                {
                    // 忽略
                }

                long size = 0;
                try { size = Convert.ToInt64(update.MaxDownloadSize); } catch (Exception) { }

                list.Add(new WindowsUpdateInfo
                {
                    Title = title,
                    Kb = kb,
                    SizeText = size > 0 ? ThisPcService.FormatSize(size) : "",
                    CategoryText = category,
                    IsInstalled = true,
                    InstalledOn = installedOn
                });

                if (list.Count >= take) break;
            }
        }
        catch (Exception ex)
        {
            return (list, ex.Message);
        }

        return (list.OrderByDescending(i => i.InstalledOn ?? DateTime.MinValue).ToList(), null);
    }

    /// <summary>暂停更新（系统允许上限 35 天，这里按 7 / 14 / 35 天三档）。</summary>
    public void Pause(int days)
    {
        var now = DateTime.Now;
        var until = now.AddDays(days);

        _journal.Record($"暂停 Windows 更新 {days} 天", new[]
        {
            (HiveKind.LocalMachine, UsoKey)
        }, () =>
        {
            Reg.SetString(RegPath.Hklm(UsoKey), "PauseUpdatesStartTime", now.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            Reg.SetString(RegPath.Hklm(UsoKey), "PauseUpdatesExpiryTime", until.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        });
    }

    public void Resume()
    {
        _journal.Record("恢复 Windows 更新", new[]
        {
            (HiveKind.LocalMachine, UsoKey)
        }, () =>
        {
            Reg.DeleteValue(RegPath.Hklm(UsoKey), "PauseUpdatesStartTime");
            Reg.DeleteValue(RegPath.Hklm(UsoKey), "PauseUpdatesExpiryTime");
        });
    }

    /// <summary>清理下载缓存（SoftwareDistribution\Download），返回释放的字节数。</summary>
    public (long Freed, int Count, string? Error) CleanCache(Action<string>? progress = null)
    {
        long freed = 0;
        var count = 0;

        if (!Directory.Exists(DownloadFolder))
            return (0, 0, "更新下载缓存目录不存在");

        try
        {
            progress?.Invoke("停止 Windows 更新服务…");
            RunService("wuauserv", "stop");

            foreach (var file in new DirectoryInfo(DownloadFolder).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = file.Length;
                    file.Delete();
                    freed += size;
                    count++;
                }
                catch (Exception)
                {
                    // 被占用的文件跳过
                }
            }
        }
        catch (Exception ex)
        {
            return (freed, count, ex.Message);
        }
        finally
        {
            progress?.Invoke("重新启动 Windows 更新服务…");
            RunService("wuauserv", "start");
        }

        return (freed, count, null);
    }

    public (bool Ok, string Message) RunService(string name, string verb)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(verb);
            psi.ArgumentList.Add(name);

            using var process = Process.Start(psi);
            if (process is null) return (false, "无法调用 sc.exe");

            process.WaitForExit(15000);
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            return process.ExitCode == 0
                ? (true, $"已{(verb == "stop" ? "停止" : "启动")} {name}")
                : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>是否存在被策略托管的更新配置（企业环境会覆盖暂停开关）。</summary>
    public string? PolicyNote()
    {
        if (Reg.Exists(RegPath.Hklm(WuPolicyKey)))
            return "检测到组策略配置（SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate），部分开关可能被策略接管";
        return null;
    }

    public bool PendingReboot()
        => Reg.Exists(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
           || Reg.Exists(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"));
}
