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
