using Microsoft.Win32;

namespace WinKit.Core;

public enum HiveKind
{
    CurrentUser,
    LocalMachine
}

/// <summary>
/// 注册表视图。x64 进程默认只看 64 位视图，32 位软件写到 WOW6432Node 的登记
/// （CLSID / Uninstall / Classes）在 Default 下读不到，也不会进快照。
/// </summary>
public enum RegViewKind
{
    /// <summary>跟随进程位数（x64 进程等于 64 位视图）。</summary>
    Default,
    /// <summary>强制 64 位视图。</summary>
    X64,
    /// <summary>强制 32 位视图（WOW6432Node）。</summary>
    X86
}

public readonly record struct RegPath(HiveKind Hive, string SubKey, RegViewKind View = RegViewKind.Default)
{
    public string FullPath => Prefix + SubKey;

    private string Prefix => Hive == HiveKind.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\";

    /// <summary>给界面和日志用：同一路径在不同视图下指向不同的键。</summary>
    public string DisplayPath => View switch
    {
        RegViewKind.X86 => FullPath + "（32 位视图）",
        RegViewKind.X64 => FullPath + "（64 位视图）",
        _ => FullPath
    };

    public override string ToString() => FullPath;

    public static RegPath Hkcu(string subKey) => new(HiveKind.CurrentUser, subKey);

    public static RegPath Hklm(string subKey) => new(HiveKind.LocalMachine, subKey);

    public static RegPath Of(HiveKind hive, string subKey) => new(hive, subKey);

    /// <summary>用指定视图打开同一路径。</summary>
    public RegPath WithView(RegViewKind view) => this with { View = view };
}

public static class Reg
{
    /// <summary>打开注册表键的结果：必须区分“不存在”和“存在但没权限”。</summary>
    public enum OpenStatus
    {
        Ok,
        NotFound,
        Denied
    }

    private static RegistryHive ToRegistryHive(HiveKind hive)
        => hive == HiveKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static RegistryView ToRegistryView(RegViewKind view) => view switch
    {
        RegViewKind.X64 => RegistryView.Registry64,
        RegViewKind.X86 => RegistryView.Registry32,
        _ => RegistryView.Default
    };

    public static RegistryKey? Open(RegPath path, bool writable)
        => TryOpen(path, writable).Key;

    /// <summary>
    /// 区分三态。把“拒绝访问”当成“不存在”会让快照记 Existed=false，
    /// 撤销时就会对一个真实存在的键执行删树——这是不可接受的数据丢失路径。
    /// </summary>
    public static (RegistryKey? Key, OpenStatus Status) TryOpen(RegPath path, bool writable)
    {
        RegistryKey? baseKey = null;

        try
        {
            baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), ToRegistryView(path.View));
            var key = baseKey.OpenSubKey(path.SubKey, writable);
            if (key is not null) return (key, OpenStatus.Ok);

            return (null, OpenStatus.NotFound);
        }
        catch (System.Security.SecurityException)
        {
            return (null, OpenStatus.Denied);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, OpenStatus.Denied);
        }
        catch (Exception)
        {
            // 键路径非法等异常：按“没读到”处理，但上层必须据此拒绝做破坏性还原
            return (null, OpenStatus.Denied);
        }
        finally
        {
            baseKey?.Dispose();
        }
    }

    public static bool Exists(RegPath path)
    {
        using var key = Open(path, false);
        return key is not null;
    }

    public static RegistryKey? Create(RegPath path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), ToRegistryView(path.View));
        return baseKey.CreateSubKey(path.SubKey, true);
    }

    /// <summary>删除整棵子树；返回是否确实删掉（或本来就不存在）。失败时不再静默。</summary>
    public static bool DeleteTree(RegPath path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), ToRegistryView(path.View));
            baseKey.DeleteSubKeyTree(path.SubKey, false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? GetString(RegPath path, string name = "")
    {
        using var key = Open(path, false);
        return key?.GetValue(name) as string ?? key?.GetValue(name)?.ToString();
    }

    /// <summary>读取字符串值，可选是否展开环境变量。</summary>
    public static string? GetString(RegPath path, string name, bool expand)
    {
        using var key = Open(path, false);
        var options = expand
            ? RegistryValueOptions.None
            : RegistryValueOptions.DoNotExpandEnvironmentNames;
        return key?.GetValue(name, null, options) as string;
    }

    public static object? GetValue(RegPath path, string name)
    {
        using var key = Open(path, false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static int? GetInt(RegPath path, string name)
    {
        var value = GetValue(path, name);
        return value switch
        {
            int i => i,
            // QWORD 直接 (int) 截断会静默变号/丢高位，超出 int 范围时按“读不到”处理
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            long => null,
            string s when int.TryParse(s, out var p) => p,
            _ => null
        };
    }

    /// <summary>需要 64 位值时用这个，避免经 GetInt 丢精度。</summary>
    public static long? GetLong(RegPath path, string name)
    {
        var value = GetValue(path, name);
        return value switch
        {
            long l => l,
            int i => i,
            string s when long.TryParse(s, out var p) => p,
            _ => null
        };
    }

    public static void SetString(RegPath path, string name, string value)
    {
        using var key = Create(path);
        key?.SetValue(name, value, RegistryValueKind.String);
    }

    public static void SetExpandString(RegPath path, string name, string value)
    {
        using var key = Create(path);
        key?.SetValue(name, value, RegistryValueKind.ExpandString);
    }

    public static void SetInt(RegPath path, string name, int value)
    {
        using var key = Create(path);
        key?.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void SetDword(RegPath path, string name, int value) => SetInt(path, name, value);

    public static void DeleteValue(RegPath path, string name)
    {
        using var key = Open(path, true);
        key?.DeleteValue(name, false);
    }

    public static IReadOnlyList<string> SubKeyNames(RegPath path)
    {
        using var key = Open(path, false);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public static IReadOnlyList<string> ValueNames(RegPath path)
    {
        using var key = Open(path, false);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public static bool HasValue(RegPath path, string name)
    {
        using var key = Open(path, false);
        if (key is null) return false;
        return key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }
}
