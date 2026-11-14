using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinKit.Services;

public sealed class FileLocker
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string AppName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string AppTypeText { get; init; } = "";
    public string StartTimeText { get; init; } = "";
    public bool CanKill { get; init; } = true;
    public string Note { get; init; } = "";
}

/// <summary>
/// 用 Windows Restart Manager 查“谁占着这个文件/文件夹”。
/// 这是系统自带能力（安装程序判断文件占用用的就是它），比遍历句柄安全得多。
/// </summary>
public sealed class FileLockService
{
    private const int ErrorMoreData = 234;
    private const int ErrorSuccess = 0;

    public (List<FileLocker> Lockers, string? Error) FindLockers(string path)
    {
        var (rmLockers, rmError) = QueryRestartManager(path);
        if (rmError is null) return (rmLockers, null);

        // Restart Manager 在受限会话里连不上（错误码 29）：退回到“已加载模块”扫描，
        // 至少能定位把 DLL/EXE 装进进程的那些程序，并说明文件是否真的被锁。
        var fallback = ScanLoadedModules(path);
        var sharing = ProbeExclusiveOpen(path);

        var note = rmError;
        if (fallback.Count == 0)
            note += sharing ? "\n文件当前确实被占用（无法独占打开）。" : "\n文件当前可以独占打开，可能并未被占用。";

        return (fallback, note);
    }

    private static bool ProbeExclusiveOpen(string path)
    {
        if (Directory.Exists(path)) return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<FileLocker> ScanLoadedModules(string path)
    {
        var results = new List<FileLocker>();
        var target = path;

        // 只有 DLL/EXE 这类会作为模块被加载的文件才能用这招
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".ocx", StringComparison.OrdinalIgnoreCase))
            return results;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id is 0 or 4) continue;

                ProcessModuleCollection modules;
                try
                {
                    modules = process.Modules;
                }
                catch (Exception)
                {
                    // 32 位进程 / 权限不足读不到模块列表
                    continue;
                }

                var hit = false;
                for (var i = 0; i < modules.Count; i++)
                {
                    var file = modules[i].FileName;
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    if (!file.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                    hit = true;
                    break;
                }

                if (!hit) continue;

                var exePath = "";
                try { exePath = process.MainModule?.FileName ?? ""; } catch (Exception) { }

                results.Add(new FileLocker
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    AppName = process.ProcessName,
                    ExecutablePath = exePath,
                    AppTypeText = "已加载该模块",
                    StartTimeText = SafeStartTime(process),
                    CanKill = process.Id > 4,
                    Note = "由已加载模块扫描得出"
                });
            }
            catch (Exception)
            {
                // 进程退出 / 无权访问
            }
            finally
            {
                process.Dispose();
            }
        }

        return results.OrderBy(l => l.ProcessName, StringComparer.CurrentCulture).ToList();
    }

    private static string SafeStartTime(Process process)
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

    private (List<FileLocker> Lockers, string? Error) QueryRestartManager(string path)
    {
        var lockers = new List<FileLocker>();

        if (string.IsNullOrWhiteSpace(path))
            return (lockers, "请先选择或输入文件路径");

        if (!File.Exists(path) && !Directory.Exists(path))
            return (lockers, "路径不存在");

        var key = new StringBuilder(33);
        var result = RmStartSession(out var session, 0, key);
        if (result != ErrorSuccess)
        {
            // 29 = ERROR_WRITE_FAULT：RPC 连不上 Restart Manager 服务（受限会话/沙箱里常见）
            var hint = result == 29
                ? "无法连接 Restart Manager 服务（错误码 29）。受限会话或沙箱环境会出现该错误，请在正常桌面会话中以管理员身份运行。"
                : $"无法启动 Restart Manager 会话（错误码 {result}）";
            return (lockers, hint);
        }

        try
        {
            var files = new[] { path };
            result = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (result != ErrorSuccess)
                return (lockers, $"登记资源失败（错误码 {result}）");

            uint needed = 0;
            uint count = 0;
            RM_PROCESS_INFO[]? infos = null;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                result = RmGetList(session, out needed, ref count, infos, out _);
                if (result == ErrorSuccess) break;
                if (result != ErrorMoreData) return (lockers, $"查询占用进程失败（错误码 {result}）");

                count = needed;
                infos = new RM_PROCESS_INFO[count];
            }

            if (result != ErrorSuccess || infos is null)
            {
                // 重试耗尽仍拿不到：说明进程列表一直在变，不能当成“没有占用”
                return (lockers, $"查询占用进程失败（错误码 {result}），结果不完整，请重试");
            }

            for (var i = 0; i < count && i < infos.Length; i++)
            {
                var info = infos[i];
                var pid = info.Process.dwProcessId;
                if (pid <= 0) continue;

                var name = info.strAppName;
                var exePath = "";
                var startText = "";
                var canKill = true;
                var note = "";

                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
                    try { exePath = process.MainModule?.FileName ?? ""; } catch (Exception) { }
                    try { startText = process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch (Exception) { }

                    if (pid is 0 or 4)
                    {
                        canKill = false;
                        note = "系统关键进程，不能结束";
                    }
                }
                catch (Exception)
                {
                    canKill = false;
                    note = "进程已退出或无权访问";
                }

                lockers.Add(new FileLocker
                {
                    ProcessId = pid,
                    ProcessName = string.IsNullOrWhiteSpace(name) ? $"PID {pid}" : name,
                    AppName = info.strAppName,
                    ExecutablePath = exePath,
                    AppTypeText = info.ApplicationType switch
                    {
                        RM_APP_TYPE.RmMainWindow => "桌面程序",
                        RM_APP_TYPE.RmService => "后台服务",
                        RM_APP_TYPE.RmExplorer => "资源管理器",
                        RM_APP_TYPE.RmConsole => "控制台程序",
                        RM_APP_TYPE.RmCritical => "系统关键进程",
                        _ => "其它"
                    },
                    StartTimeText = startText,
                    CanKill = canKill,
                    Note = note
                });
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return (lockers.OrderBy(l => l.ProcessName, StringComparer.CurrentCulture).ToList(), null);
    }

    public (bool Ok, string Message) Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            process.Kill();
            process.WaitForExit(3000);
            return (true, $"已结束 {name}（PID {processId}）");
        }
        catch (Exception ex)
        {
            return (false, $"结束失败：{ex.Message}");
        }
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        out uint lpdwRebootReasons);
}
