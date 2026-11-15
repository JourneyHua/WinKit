using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WinKit.Core;

public static class Native
{
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int outBufSize, IntPtr reserved);

    public static void NotifyAssociationsChanged()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 通知失败不影响已完成的注册表改动
        }
    }

    public static void NotifyFolderChanged(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>
    /// 解析 "@C:\Windows\system32\shell32.dll,-1234" 这类间接字符串，失败时返回 null。
    /// </summary>
    public static string? ResolveIndirectString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith('@')) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var hr = SHLoadIndirectString(raw, buffer, buffer.Capacity, IntPtr.Zero);
            if (hr != 0) return null;
            var text = buffer.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static int RestartExplorer()
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
                killed++;
            }
            catch (Exception)
            {
                // 忽略单个进程失败
            }
        }

        Thread.Sleep(600);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // 系统一般会自动重启外壳
        }

        return killed;
    }

    public static bool OpenInRegedit(string? rawRegistryPath)
    {
        if (string.IsNullOrWhiteSpace(rawRegistryPath)) return false;

        var path = rawRegistryPath.Trim();
        var hkeyIdx = path.IndexOf("HKEY_", StringComparison.OrdinalIgnoreCase);
        if (hkeyIdx > 0) path = path[hkeyIdx..];

        if (path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
            path = @"HKEY_CURRENT_USER\" + path[5..];
        else if (path.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            path = @"HKEY_LOCAL_MACHINE\" + path[5..];

        if (!path.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            Reg.SetString(RegPath.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"), "LastKey", path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "regedit.exe",
                Arguments = "-m",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 调一个 shell 动词（pintohome / unpinfromhome / unpinfromtaskbar …）。
    ///
    /// fMask 必须是 SEE_MASK_INVOKEIDLIST (0x0C)：这些动词只注册了
    /// CommandStateHandler，注册表里没有对应的 command 子键，直接 ShellExecute
    /// 会返回错误码 1155（没有关联的应用）。走 IContextMenu 才找得到处理器。
    ///
    /// 注意：这个函数不判断动词是否"真的生效了"。取消固定这类操作 ShellExecuteEx
    /// 返回成功但系统可能没执行，调用方必须自己复查结果（比如 .lnk 是否真的消失）。
    /// </summary>
    public static void RunShellVerb(string path, string verb)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST,
            lpVerb = verb,
            lpFile = path,
            nShow = 1
        };

        if (!ShellExecuteEx(ref info))
        {
            var code = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"执行“{verb}”失败（错误码 {code}）");
        }
    }

    private const int SEE_MASK_INVOKEIDLIST = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>
    /// 用系统默认关联打开一个文件 / 目录 / 协议（ms-settings:、ms-settings:storagesense 这类）。
    /// 返回是否成功调起——调用方据此决定要不要提示失败，别自己再包一层 try/catch。
    /// </summary>
    public static bool OpenPath(string? target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = target.Trim(),
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;
            if (!string.IsNullOrWhiteSpace(workingDirectory)) info.WorkingDirectory = workingDirectory;

            Process.Start(info);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 在资源管理器里定位一个路径：文件就选中它，目录就打开它。
    /// 路径不存在时返回 false 且什么都不做（不擅自打开父目录，免得用户以为定位错了）。
    /// </summary>
    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var full = path.Trim();

        try
        {
            if (File.Exists(full))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{full}\"",
                    UseShellExecute = true
                });
                return true;
            }

            if (Directory.Exists(full))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = full,
                    UseShellExecute = true
                });
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- 提权后接收拖放 ----------

    private const int WM_DROPFILES = 0x0233;
    private const int WM_COPYDATA = 0x004A;
    private const int WM_COPYGLOBALDATA = 0x0049;
    private const int MSGFLT_ALLOW = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, int message, int action, IntPtr changeFilterStruct);

    /// <summary>
    /// 允许低完整性级别的进程（资源管理器、桌面）把文件拖进本窗口。
    ///
    /// WinKit 的发布清单是 requireAdministrator，进程以高完整性级别运行；而资源管理器
    /// 是普通权限。UIPI 会直接丢掉跨完整性级别的拖放消息，表现就是「拖进去毫无反应、
    /// 连禁止光标都不出现」，没有任何异常可抓。只有拖放这一个通道受影响，
    /// 所以只放行拖放必需的三个消息，不是整体降低窗口的消息过滤级别。
    ///
    /// 必须在窗口创建之后（有 HWND 之后）调用；调用失败不影响其它功能。
    /// 返回 Win32 错误码，0 表示三个消息都放行成功——失败时把码打出来，
    /// 否则「拖放没反应」这种问题在日志里查不到任何线索。
    /// </summary>
    public static int AllowDragDropFromLowerIntegrity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 87;

        try
        {
            if (!ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero))
                return Marshal.GetLastWin32Error();
            if (!ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero))
                return Marshal.GetLastWin32Error();
            if (!ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero))
                return Marshal.GetLastWin32Error();
            return 0;
        }
        catch (Exception)
        {
            return Marshal.GetLastWin32Error();
        }
    }

    /// <summary>
    /// 写入类操作前的权限闸门。返回 null 表示可以继续；否则返回给用户看的说明。
    /// 主程序以 asInvoker 启动，所以「没权限」是正常状态，必须给出可执行的下一步，
    /// 而不是让 UnauthorizedAccessException 冒到界面上变成一句「拒绝访问」。
    /// </summary>
    public static string? RequireAdmin(string action)
        => IsElevated()
            ? null
            : $"「{action}」需要管理员权限。请先点窗口顶部的「以管理员身份重新启动」，再执行一次。";

    public static bool RestartAsAdmin()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
