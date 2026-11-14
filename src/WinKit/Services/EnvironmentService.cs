using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinKit.Core;

namespace WinKit.Services;

public enum EnvScope
{
    User,
    Machine
}

public sealed class EnvVarItem
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public EnvScope Scope { get; init; }
    public bool IsPath { get; init; }

    /// <summary>PATH 展开成多行展示，其它变量原样。</summary>
    public string DisplayValue => IsPath ? Value.Replace(";", "\n") : Value;

    public string ScopeText => Scope == EnvScope.User ? "当前用户" : "系统";
    public string SearchBlob => $"{Name} {Value}".ToLowerInvariant();
}

public sealed class PathEntry
{
    public string Raw { get; init; } = "";
    public string Expanded { get; init; } = "";
    public bool Exists { get; init; }
    public int Index { get; set; }

    public string StatusText => Exists ? "存在" : "目录不存在";
    public string Level => Exists ? "ok" : "warn";
}

/// <summary>
/// 环境变量与 PATH 可视化编辑。
/// 所有写入都走快照；写完后广播 WM_SETTINGCHANGE，新开的进程立刻生效（已开的进程仍需重启取新值）。
/// PATH 只改字符串内容，不删除任何目录。
/// </summary>
public sealed class EnvironmentService
{
    private const string UserEnvKey = @"Environment";
    private const string MachineEnvKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private readonly ChangeJournal _journal;

    public EnvironmentService(ChangeJournal journal) => _journal = journal;

    public List<EnvVarItem> LoadAll()
    {
        var list = new List<EnvVarItem>();

        foreach (var (scope, path) in new[]
                 {
                     (EnvScope.User, RegPath.Hkcu(UserEnvKey)),
                     (EnvScope.Machine, RegPath.Hklm(MachineEnvKey))
                 })
        {
            foreach (var name in Reg.ValueNames(path))
            {
                if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase) && list.Any(v => v.IsPath))
                    continue; // 用户 PATH 与系统 PATH 合并展示一次即可

                var raw = Reg.GetString(path, name, expand: false) ?? "";
                list.Add(new EnvVarItem
                {
                    Name = name,
                    Value = raw,
                    Scope = scope,
                    IsPath = string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        // PATH 单独合并：用户 + 系统，按实际生效顺序（用户在前）
        return list
            .OrderByDescending(v => v.IsPath)
            .ThenBy(v => v.Scope)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>读取某个作用域的 PATH 原始值（不展开环境变量，避免把 %SystemRoot% 变成绝对路径写回）。</summary>
    public string GetRawPath(EnvScope scope)
    {
        var path = scope == EnvScope.User ? RegPath.Hkcu(UserEnvKey) : RegPath.Hklm(MachineEnvKey);
        return Reg.GetString(path, "Path", expand: false) ?? "";
    }

    public string GetRawPath(EnvScope scope, RegViewKind view)
    {
        var path = (scope == EnvScope.User ? RegPath.Hkcu(UserEnvKey) : RegPath.Hklm(MachineEnvKey)).WithView(view);
        return Reg.GetString(path, "Path", expand: false) ?? "";
    }

    public List<PathEntry> ParsePath(string raw)
    {
        var list = new List<PathEntry>();
        var index = 0;

        foreach (var segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;

            var expanded = Environment.ExpandEnvironmentVariables(trimmed);

            list.Add(new PathEntry
            {
                Raw = trimmed,
                Expanded = expanded,
                Exists = SafeDirectoryExists(expanded),
                Index = index++
            });
        }

        return list;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>用新的 PATH 内容覆盖（去重、去空），带快照。</summary>
    public void SetPath(EnvScope scope, IEnumerable<string> segments)
    {
        var cleaned = segments
            .Select(s => (s ?? "").Trim().Trim('"'))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var next = string.Join(";", cleaned);
        var subKey = scope == EnvScope.User ? UserEnvKey : MachineEnvKey;
        var hive = scope == EnvScope.User ? HiveKind.CurrentUser : HiveKind.LocalMachine;
        var path = RegPath.Of(hive, subKey);

        var originalKind = Reg.HasValue(path, "Path")
            ? Reg.Open(path, false)?.GetValueKind("Path") ?? RegistryValueKind.ExpandString
            : RegistryValueKind.ExpandString;

        _journal.Record($"修改{(scope == EnvScope.User ? "用户" : "系统")} PATH",
            new[] { (hive, subKey) },
            () =>
            {
                // 保持原有值类型：系统 PATH 一般是 REG_EXPAND_SZ，写成 REG_SZ 会让 %SystemRoot% 不再展开
                using var key = Reg.Create(path);
                key?.SetValue("Path", next, originalKind);
            });

        BroadcastChange();
    }

    public void SetVariable(EnvScope scope, string name, string value, string? oldName = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("变量名不能为空", nameof(name));

        var subKey = scope == EnvScope.User ? UserEnvKey : MachineEnvKey;
        var hive = scope == EnvScope.User ? HiveKind.CurrentUser : HiveKind.LocalMachine;
        var path = RegPath.Of(hive, subKey);

        _journal.Record($"{(string.IsNullOrEmpty(oldName) ? "新增" : "修改")}环境变量：{name}",
            new[] { (hive, subKey) },
            () =>
            {
                using var key = Reg.Create(path);
                if (key is null) throw new InvalidOperationException("无法写入环境变量键");

                if (!string.IsNullOrEmpty(oldName) &&
                    !string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(oldName, false);
                }

                key.SetValue(name, value, RegistryValueKind.ExpandString);
            });

        BroadcastChange();
    }

    public void DeleteVariable(EnvScope scope, string name)
    {
        var subKey = scope == EnvScope.User ? UserEnvKey : MachineEnvKey;
        var hive = scope == EnvScope.User ? HiveKind.CurrentUser : HiveKind.LocalMachine;

        _journal.Record($"删除环境变量：{name}", new[] { (hive, subKey) },
            () => Reg.DeleteValue(RegPath.Of(hive, subKey), name));

        BroadcastChange();
    }

    /// <summary>通知系统环境变量已变化：新开的进程（含资源管理器启动的）会拿到新值。</summary>
    public static void BroadcastChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */,
                IntPtr.Zero, "Environment",
                SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 3000, out _);
        }
        catch (Exception)
        {
            // 广播失败不影响已写入的注册表
        }
    }

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0000,
        SMTO_ABORTIFHUNG = 0x0002
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);
}
