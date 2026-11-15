using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinKit.Core;

/// <summary>
/// .lnk 解析与已知文件夹（KnownFolder GUID）解析。
/// </summary>
public static class Shortcuts
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>
    /// 把 "{6D809377-6AF0-444B-8957-A3773F02200E}\Python314\python.exe" 这类
    /// 以 KnownFolder GUID 开头的路径解析成真实路径。
    ///
    /// 不解析就会把 37 条托盘记录误判成僵尸文件——用户目录被重定向、Program Files
    /// 被虚拟化时，注册表里存的正是这种相对 GUID 形式。
    /// </summary>
    public static string ResolveKnownFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('{')) return trimmed;

        var close = trimmed.IndexOf('}');
        if (close < 0) return trimmed;

        if (!Guid.TryParse(trimmed[1..close], out var guid)) return trimmed;

        var resolved = GetKnownFolderPath(guid);
        if (string.IsNullOrWhiteSpace(resolved)) return trimmed;

        var tail = trimmed[(close + 1)..].TrimStart('\\');
        return tail.Length == 0 ? resolved : Path.Combine(resolved, tail);
    }

    /// <summary>解析单个 KnownFolder GUID；失败返回 null。</summary>
    public static string? GetKnownFolderPath(Guid guid)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;

            var text = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd('\\');
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>解析 .lnk 的目标路径；CLSID 链接（如「文件资源管理器」）没有目标路径，返回空串。</summary>
    public static string ResolveLinkTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return "";

        object? shell = null;
        object? shortcut = null;

        try
        {
            // 用后期绑定调 WScript.Shell，避免为一个属性引入 Interop 程序集
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return "";

            shell = Activator.CreateInstance(type);
            if (shell is null) return "";

            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });

            if (shortcut is null) return "";

            var target = shortcut.GetType()
                .InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;

            return (target ?? "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            Release(shell);
            Release(shortcut);
        }
    }

    /// <summary>取 .lnk 的备注/描述，用于识别 CLSID 链接指向什么。</summary>
    public static string ResolveLinkDescription(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return "";

        object? shell = null;
        object? shortcut = null;

        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return "";

            shell = Activator.CreateInstance(type);
            if (shell is null) return "";

            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut is null) return "";

            var desc = shortcut.GetType()
                .InvokeMember("Description", BindingFlags.GetProperty, null, shortcut, null) as string;

            return (desc ?? "").Trim();
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            Release(shell);
            Release(shortcut);
        }
    }

    /// <summary>
    /// 目标路径是否还存在。空目标路径（CLSID 链接）视为「未知」而非「失效」，
    /// 否则资源管理器、任务管理器这类系统项会被当成失效项删掉。
    /// </summary>
    public static bool? IsTargetMissing(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;

        var resolved = ResolveKnownFolderPath(targetPath);
        if (string.IsNullOrWhiteSpace(resolved)) return null;

        try
        {
            if (File.Exists(resolved) || Directory.Exists(resolved)) return false;
        }
        catch (Exception)
        {
            return null;
        }

        // File.Exists / Directory.Exists 遇到「无权限」也返回 false。直接采信会把
        // C:\Program Files\WindowsApps 下的应用（Copilot、Outlook 这类）统统判成失效，
        // 用户点「折叠失效项」就把好端端的图标藏了。所以再确认一次父目录能不能枚举：
        //   能枚举且没有这个名字 → 真的不存在
        //   父目录本身就访问不了   → 判不了，返回 null
        var dir = Path.GetDirectoryName(resolved);
        var name = Path.GetFileName(resolved);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;

        try
        {
            // 按名字精确比对，不能拿 name 当搜索模式——文件名里出现 [ ] * ? 时会被当通配符
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // 父目录都没有，目标当然不存在
            return true;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception)
        {
            // 释放失败不影响功能
        }
    }

    /// <summary>展开路径中的环境变量；失败时返回原值。</summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>把 OEM 代码页输出转成正规字符串（pnputil / sc / netsh 在中文系统上不是 UTF-8）。</summary>
    public static string DecodeOem(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try
        {
            return Encoding.GetEncoding(0).GetString(bytes);
        }
        catch (Exception)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
