using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinKit.Core;

namespace WinKit.Services;

public sealed class FileAssocItem
{
    public string Extension { get; init; } = "";
    public string ProgId { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string Command { get; init; } = "";
    public string IconPath { get; init; } = "";
    public HiveKind Hive { get; init; }
    public bool IsUserChoice { get; init; }
    public bool IsSystemDefault { get; init; }
    public string SourceText => IsUserChoice ? "用户指定" : Hive == HiveKind.CurrentUser ? "当前用户" : "系统默认";

    public string DisplayText => string.IsNullOrWhiteSpace(FriendlyName) ? ProgId : FriendlyName;

    public string SearchBlob =>
        $"{Extension} {ProgId} {FriendlyName} {Command}".ToLowerInvariant();
}

/// <summary>
/// 默认打开方式管理。
/// 读：.ext → UserChoice（用户指定）→ 各类 Classes 根下的 ProgID → 打开命令。
/// 写：只做两件安全的事 —— ① 用系统官方 UI 打开（推荐，Windows 8+ 有 UserChoice 哈希保护，直接写注册表会被系统判定篡改并重置）；
/// ② 清理失效的 ProgID（指向的 exe 已不存在），带快照可撤销。
/// </summary>
public sealed class FileAssocService
{
    private const string UserChoiceRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    private const string ClassesRoot = @"Software\Classes";

    private readonly ChangeJournal _journal;

    public FileAssocService(ChangeJournal journal) => _journal = journal;

    public List<FileAssocItem> Load()
    {
        var list = new List<FileAssocItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 从「设置 → 默认应用」的登记处枚举，覆盖面比遍历 Classes 更贴近用户认知
        var extSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            foreach (var ext in Reg.SubKeyNames(RegPath.Of(hive, ClassesRoot)))
            {
                if (!ext.StartsWith('.')) continue;
                extSet.Add(ext);
            }
        }

        foreach (var ext in extSet)
        {
            if (!seen.Add(ext)) continue;

            var (progId, isUserChoice, hive) = ResolveProgId(ext);
            if (string.IsNullOrWhiteSpace(progId)) continue;

            var progIdPath = RegPath.Of(hive, $@"{ClassesRoot}\{progId}");
            var friendly = Reg.GetString(progIdPath, "") ?? "";
            var command = Reg.GetString(RegPath.Of(hive, $@"{ClassesRoot}\{progId}\shell\open\command"), "") ?? "";
            var icon = Reg.GetString(RegPath.Of(hive, $@"{ClassesRoot}\{progId}\DefaultIcon"), "") ?? "";

            // 有些系统应用把友好名放在 Application 子键下
            if (string.IsNullOrWhiteSpace(friendly))
                friendly = Reg.GetString(RegPath.Of(hive, $@"{ClassesRoot}\{progId}\Application"), "ApplicationName") ?? "";

            var exe = ContextMenuService.ExtractExecutable(command);
            var exeMissing = !string.IsNullOrWhiteSpace(exe) && !File.Exists(Environment.ExpandEnvironmentVariables(exe));

            list.Add(new FileAssocItem
            {
                Extension = ext,
                ProgId = progId,
                FriendlyName = ContextMenuService.CleanDisplayName(friendly),
                Command = command,
                IconPath = icon,
                Hive = hive,
                IsUserChoice = isUserChoice,
                IsSystemDefault = hive == HiveKind.LocalMachine
            });
        }

        return list.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string ProgId, bool IsUserChoice, HiveKind Hive) ResolveProgId(string ext)
    {
        // 1. 用户在设置里指定过 → UserChoice
        var choicePath = RegPath.Hkcu($@"{UserChoiceRoot}\{ext}\UserChoice");
        var choice = Reg.GetString(choicePath, "ProgId");
        if (!string.IsNullOrWhiteSpace(choice))
            return (choice!, true, HiveKind.CurrentUser);

        // 2. 关联缓存（部分系统版本）
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var openWith = Reg.GetString(
                RegPath.Of(hive, $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\OpenWithProgids"), "");
            if (!string.IsNullOrWhiteSpace(openWith)) return (openWith!, false, hive);
        }

        // 3. Classes 默认值
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var value = Reg.GetString(RegPath.Of(hive, $@"{ClassesRoot}\{ext}"));
            if (!string.IsNullOrWhiteSpace(value)) return (value!, false, hive);
        }

        return ("", false, HiveKind.CurrentUser);
    }

    /// <summary>打开系统官方的「按文件类型选择默认应用」界面。</summary>
    public static (bool Ok, string Message) OpenDefaultAppsSettings(string? extension = null)
    {
        try
        {
            var target = string.IsNullOrWhiteSpace(extension)
                ? "ms-settings:defaultapps"
                : $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(extension)}";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });

            return (true, "已打开系统默认应用设置");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>用系统 OpenWith 对话框让用户为某个扩展名挑选程序（等价于资源管理器右键「打开方式」）。</summary>
    public static (bool Ok, string Message) ShowOpenWith(string extension)
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), "winkit-probe" + (extension.StartsWith('.') ? extension : "." + extension));
            if (!File.Exists(probe))
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
            }

            var result = ShellExecute(IntPtr.Zero, "openas", probe, null, null, 1);
            return result > 32
                ? (true, "已打开「打开方式」对话框")
                : (false, $"无法打开对话框（错误码 {result}）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 清理失效的程序关联：ProgID 的打开命令指向的文件已不存在。
    /// 只删「当前用户」下指向该 ProgID 的登记，不动 HKLM，且带快照。
    /// </summary>
    public int CleanInvalid(Action<string>? progress = null)
    {
        var removed = 0;
        var tracked = new List<(HiveKind Hive, string SubKey)>();

        var targets = new List<(string Ext, string ProgId)>();

        foreach (var ext in Reg.SubKeyNames(RegPath.Hkcu(ClassesRoot)))
        {
            if (!ext.StartsWith('.')) continue;

            var (progId, isUserChoice, hive) = ResolveProgId(ext);
            if (string.IsNullOrWhiteSpace(progId) || isUserChoice) continue;

            var command = Reg.GetString(RegPath.Of(hive, $@"{ClassesRoot}\{progId}\shell\open\command"), "");
            if (string.IsNullOrWhiteSpace(command)) continue;

            var exe = ContextMenuService.ExtractExecutable(command);
            if (string.IsNullOrWhiteSpace(exe)) continue;

            if (!File.Exists(Environment.ExpandEnvironmentVariables(exe)))
                targets.Add((ext, progId));
        }

        if (targets.Count == 0)
        {
            progress?.Invoke("没有发现失效的默认打开方式");
            return 0;
        }

        foreach (var (ext, _) in targets)
            tracked.Add((HiveKind.CurrentUser, $@"{ClassesRoot}\{ext}"));

        _journal.Record($"清理 {targets.Count} 个失效的默认打开方式", tracked, () =>
        {
            foreach (var (ext, _) in targets)
            {
                Reg.DeleteValue(RegPath.Hkcu($@"{ClassesRoot}\{ext}"), "");
            }
        });

        removed = targets.Count;
        progress?.Invoke($"已清理 {removed} 个失效关联");
        return removed;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecute(
        IntPtr hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);
}
