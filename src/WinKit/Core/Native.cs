using System.Diagnostics;
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
