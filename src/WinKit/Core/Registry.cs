using Microsoft.Win32;

namespace WinKit.Core;

public enum HiveKind
{
    CurrentUser,
    LocalMachine
}

public readonly record struct RegPath(HiveKind Hive, string SubKey)
{
    public string FullPath => Prefix + SubKey;

    private string Prefix => Hive == HiveKind.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\";

    public override string ToString() => FullPath;

    public static RegPath Hkcu(string subKey) => new(HiveKind.CurrentUser, subKey);

    public static RegPath Hklm(string subKey) => new(HiveKind.LocalMachine, subKey);

    public static RegPath Of(HiveKind hive, string subKey) => new(hive, subKey);
}

public static class Reg
{
    private static RegistryHive ToRegistryHive(HiveKind hive)
        => hive == HiveKind.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    public static RegistryKey? Open(RegPath path, bool writable)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), RegistryView.Default);
            return baseKey.OpenSubKey(path.SubKey, writable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool Exists(RegPath path)
    {
        using var key = Open(path, false);
        return key is not null;
    }

    public static RegistryKey? Create(RegPath path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), RegistryView.Default);
        return baseKey.CreateSubKey(path.SubKey, true);
    }

    public static void DeleteTree(RegPath path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToRegistryHive(path.Hive), RegistryView.Default);
            baseKey.DeleteSubKeyTree(path.SubKey, false);
        }
        catch (Exception)
        {
            // 权限不足或已被删除，交给上层判断
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
            long l => (int)l,
            string s when int.TryParse(s, out var p) => p,
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
