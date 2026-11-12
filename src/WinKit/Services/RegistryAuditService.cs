using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public enum RegistryCategory
{
    Startup,
    ContextMenu,
    FileAssoc,
    UninstallInfo,
    InvalidClsid,
    Policies,
    PathEnv
}

public enum RiskLevel
{
    Safe,
    Caution,
    Danger
}

public sealed class RegistryItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public HiveKind Hive { get; init; }
    public string SubKey { get; init; } = "";
    public string? ValueName { get; init; }
    public string? EntryText { get; init; }
    public RiskLevel Risk { get; init; } = RiskLevel.Caution;
    public bool IsInvalid { get; init; }
    public bool CanRemove { get; init; }

    public string RiskText => Risk switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Caution => "谨慎",
        _ => "危险"
    };

    public string StatusText => IsInvalid ? "可疑" : "正常";
}

public sealed class RegistryAuditResult
{
    public List<RegistryItem> Items { get; init; } = new();
    public int Scanned { get; init; }
    public bool Truncated { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// 注册表体检：按分类定向扫描，不做全表遍历。
/// 只有“可证明无效”的条目（指向的文件/目录已不存在）才允许删除。
/// </summary>
public sealed class RegistryAuditService
{
    private const int MaxItems = 3000;
    private const int MaxClsidScan = 15000;

    private readonly ChangeJournal _journal;

    public RegistryAuditService(ChangeJournal journal) => _journal = journal;

    public RegistryAuditResult Load(RegistryCategory category) => category switch
    {
        RegistryCategory.Startup => LoadStartup(),
        RegistryCategory.ContextMenu => LoadContextMenuSummary(),
        RegistryCategory.FileAssoc => LoadFileAssoc(),
        RegistryCategory.UninstallInfo => LoadUninstall(),
        RegistryCategory.InvalidClsid => LoadInvalidClsid(),
        RegistryCategory.Policies => LoadPolicies(),
        RegistryCategory.PathEnv => LoadPath(),
        _ => new RegistryAuditResult()
    };

    private static string Expand(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));

    private static bool FileMissing(string? path)
    {
        var expanded = Expand(path);
        if (string.IsNullOrWhiteSpace(expanded)) return true;
        if (expanded.Contains(',')) expanded = expanded.Split(',')[0].Trim(); // 处理 "@dll,-123" 之类
        try
        {
            return !File.Exists(expanded) && !Directory.Exists(expanded);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- 1. 启动项 ----------

    private RegistryAuditResult LoadStartup()
    {
        var items = new List<RegistryItem>();
        var scanned = 0;

        var locations = new (HiveKind Hive, string Path, string Label)[]
        {
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "当前用户 Run"),
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "当前用户 RunOnce"),
            (HiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "所有用户 Run"),
            (HiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "所有用户 RunOnce"),
            (HiveKind.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "32 位 Run"),
            (HiveKind.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "32 位 RunOnce")
        };

        foreach (var (hive, path, label) in locations)
        {
            var regPath = RegPath.Of(hive, path);
            using var key = Reg.Open(regPath, false);
            if (key is null) continue;

            foreach (var name in key.GetValueNames())
            {
                scanned++;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var command = key.GetValue(name)?.ToString() ?? "";
                var exe = ContextMenuService.ExtractExecutable(command);
                var missing = string.IsNullOrWhiteSpace(exe) || FileMissing(exe);

                items.Add(new RegistryItem
                {
                    Name = name,
                    Detail = string.IsNullOrWhiteSpace(command) ? "（启动值为空）" : command,
                    KeyPath = $"{label} · {regPath.FullPath}",
                    Hive = hive,
                    SubKey = path,
                    ValueName = name,
                    Risk = RiskLevel.Caution,
                    IsInvalid = missing,
                    CanRemove = missing
                });
            }
        }

        // Winlogon 外壳项：只读展示
        foreach (var valueName in new[] { "Shell", "Userinit" })
        {
            var path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            var raw = Reg.GetString(RegPath.Hklm(path), valueName);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            items.Add(new RegistryItem
            {
                Name = $"Winlogon {valueName}",
                Detail = raw,
                KeyPath = "登录外壳 · HKEY_LOCAL_MACHINE\\" + path,
                Hive = HiveKind.LocalMachine,
                SubKey = path,
                ValueName = valueName,
                Risk = RiskLevel.Danger,
                IsInvalid = false,
                CanRemove = false
            });
        }

        return new RegistryAuditResult { Items = items, Scanned = scanned };
    }

    // ---------- 2. 右键菜单（汇总，指向专用模块） ----------

    private RegistryAuditResult LoadContextMenuSummary()
    {
        var service = new ContextMenuService(_journal);
        var items = new List<RegistryItem>();
        var scanned = 0;

        foreach (var domain in MenuDomain.All)
        {
            List<MenuEntry> entries;
            try
            {
                entries = service.Load(domain.Kind);
            }
            catch (Exception)
            {
                continue;
            }

            scanned += entries.Count;
            items.Add(new RegistryItem
            {
                Name = domain.Title,
                Detail = $"{entries.Count} 项（第三方 {entries.Count(e => !e.IsSystem)}）· 请到「右键菜单」页管理",
                KeyPath = domain.Description,
                Risk = RiskLevel.Safe,
                IsInvalid = false,
                CanRemove = false
            });
        }

        return new RegistryAuditResult
        {
            Items = items,
            Scanned = scanned,
            Note = "这里只做汇总。禁用/删除请到「右键菜单」页，那里有域筛选和快照撤销。"
        };
    }

    // ---------- 3. 文件关联 ----------

    private RegistryAuditResult LoadFileAssoc()
    {
        var items = new List<RegistryItem>();
        var scanned = 0;
        var truncated = false;

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var classesRoot = RegPath.Of(hive, @"Software\Classes");
            foreach (var ext in Reg.SubKeyNames(classesRoot))
            {
                if (!ext.StartsWith('.')) continue;

                scanned++;
                if (scanned > MaxItems) { truncated = true; break; }

                var progId = Reg.GetString(RegPath.Of(hive, $@"Software\Classes\{ext}")) ?? "";
                if (string.IsNullOrWhiteSpace(progId)) continue;

                var commandPath = $@"Software\Classes\{progId}\shell\open\command";
                var command = Reg.GetString(RegPath.Of(hive, commandPath))
                              ?? Reg.GetString(RegPath.Hklm(commandPath))
                              ?? "";

                if (string.IsNullOrWhiteSpace(command)) continue;

                var exe = ContextMenuService.ExtractExecutable(command);
                var missing = FileMissing(exe);

                items.Add(new RegistryItem
                {
                    Name = ext,
                    Detail = $"{progId} → {exe}",
                    KeyPath = RegPath.Of(hive, $@"Software\Classes\{ext}").FullPath,
                    Hive = hive,
                    SubKey = $@"Software\Classes\{ext}",
                    ValueName = null,
                    Risk = RiskLevel.Caution,
                    IsInvalid = missing,
                    CanRemove = false // 删关联风险高，只报告
                });
            }
        }

        return new RegistryAuditResult { Items = items, Scanned = scanned, Truncated = truncated };
    }

    // ---------- 4. 卸载信息 ----------

    private RegistryAuditResult LoadUninstall()
    {
        var service = new UninstallService();
        var entries = service.LoadAll();
        var items = new List<RegistryItem>();

        foreach (var entry in entries)
        {
            var exe = ContextMenuService.ExtractExecutable(entry.UninstallString);
            var uninstallMissing = !string.IsNullOrWhiteSpace(exe) && FileMissing(exe);
            var locationMissing = !string.IsNullOrWhiteSpace(entry.InstallLocation) && FileMissing(entry.InstallLocation);
            var invalid = uninstallMissing;

            var (hive, subKey) = ContextMenuService.ParseHivePath(entry.RegistryPath);

            items.Add(new RegistryItem
            {
                Name = entry.DisplayText,
                Detail = locationMissing
                    ? $"安装目录已不存在：{entry.InstallLocation}"
                    : entry.UninstallString,
                KeyPath = entry.RegistryPath,
                Hive = hive,
                SubKey = subKey,
                ValueName = null,
                Risk = RiskLevel.Safe,
                IsInvalid = invalid,
                CanRemove = invalid
            });
        }

        return new RegistryAuditResult { Items = items, Scanned = entries.Count };
    }

    // ---------- 5. 失效残留（CLSID 指向的模块不存在） ----------

    private RegistryAuditResult LoadInvalidClsid()
    {
        var items = new List<RegistryItem>();
        var scanned = 0;
        var truncated = false;

        foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
        {
            foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, @"Software\Classes\CLSID")))
            {
                scanned++;
                if (scanned > MaxClsidScan) { truncated = true; break; }

                foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                {
                    var path = $@"Software\Classes\CLSID\{clsid}\{server}";
                    var module = Reg.GetString(RegPath.Of(hive, path));
                    if (string.IsNullOrWhiteSpace(module)) continue;

                    var expanded = Expand(module);
                    if (expanded.Contains(',')) expanded = expanded.Split(',')[0].Trim();
                    if (string.IsNullOrWhiteSpace(expanded)) continue;
                    if (expanded.StartsWith("mscoree", StringComparison.OrdinalIgnoreCase)) continue;

                    bool missing;
                    try { missing = !File.Exists(expanded); }
                    catch (Exception) { missing = false; }
                    if (!missing) continue;

                    var name = ContextMenuService.ResolveClsidName(clsid);
                    items.Add(new RegistryItem
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? clsid : $"{name}（{clsid}）",
                        Detail = $"{server} 指向的文件不存在：{expanded}",
                        KeyPath = RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}").FullPath,
                        Hive = hive,
                        SubKey = $@"Software\Classes\CLSID\{clsid}",
                        ValueName = null,
                        Risk = RiskLevel.Safe,
                        IsInvalid = true,
                        CanRemove = true
                    });
                    break;
                }
            }
        }

        return new RegistryAuditResult { Items = items, Scanned = scanned, Truncated = truncated };
    }

    // ---------- 6. 策略与限制 ----------

    private RegistryAuditResult LoadPolicies()
    {
        var items = new List<RegistryItem>();
        var scanned = 0;

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var root = hive == HiveKind.CurrentUser
                ? @"Software\Microsoft\Windows\CurrentVersion\Policies"
                : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies";

            Walk(hive, root, 0);

            void Walk(HiveKind h, string path, int depth)
            {
                if (depth > 2) return;
                var regPath = RegPath.Of(h, path);
                using var key = Reg.Open(regPath, false);
                if (key is null) return;

                foreach (var name in key.GetValueNames())
                {
                    scanned++;
                    if (scanned > MaxItems) return;
                    var value = key.GetValue(name);
                    items.Add(new RegistryItem
                    {
                        Name = name,
                        Detail = $"{value}",
                        KeyPath = regPath.FullPath,
                        Hive = h,
                        SubKey = path,
                        ValueName = name,
                        Risk = RiskLevel.Caution,
                        IsInvalid = false,
                        CanRemove = false
                    });
                }

                foreach (var child in key.GetSubKeyNames())
                    Walk(h, path + "\\" + child, depth + 1);
            }
        }

        return new RegistryAuditResult
        {
            Items = items,
            Scanned = scanned,
            Note = "策略项多为安全软件或其他工具写入，本工具只展示不代删，避免破坏已有配置。"
        };
    }

    // ---------- 7. 环境变量 / PATH ----------

    private RegistryAuditResult LoadPath()
    {
        var items = new List<RegistryItem>();
        var scanned = 0;

        var targets = new (HiveKind Hive, string Path, string Label)[]
        {
            (HiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "系统 PATH"),
            (HiveKind.CurrentUser, @"Environment", "用户 PATH")
        };

        foreach (var (hive, path, label) in targets)
        {
            var raw = Reg.GetString(RegPath.Of(hive, path), "Path",
                expand: false) ?? "";
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (var segment in raw.Split(';'))
            {
                var entry = segment.Trim();
                if (string.IsNullOrWhiteSpace(entry)) continue;

                scanned++;
                var expanded = Expand(entry);
                var missing = !Directory.Exists(expanded);

                items.Add(new RegistryItem
                {
                    Name = entry,
                    Detail = missing ? "目录不存在" : expanded,
                    KeyPath = $"{label} · {RegPath.Of(hive, path).FullPath}",
                    Hive = hive,
                    SubKey = path,
                    ValueName = "Path",
                    EntryText = entry,
                    Risk = RiskLevel.Safe,
                    IsInvalid = missing,
                    CanRemove = missing
                });
            }
        }

        return new RegistryAuditResult
        {
            Items = items,
            Scanned = scanned,
            Note = "删除只会从 PATH 里移掉这一条，不会删除目录本身。"
        };
    }

    // ---------- 删除 ----------

    public void Remove(RegistryItem item)
    {
        if (!item.CanRemove) return;

        _journal.Record($"清理注册表项：{item.Name}", new[] { (item.Hive, item.SubKey) }, () =>
        {
            if (item.EntryText is not null && item.ValueName is not null)
            {
                // PATH：重写整条值，去掉这一段
                var regPath = RegPath.Of(item.Hive, item.SubKey);
                var raw = Reg.GetString(regPath, item.ValueName, expand: false) ?? "";
                var kept = raw.Split(';')
                    .Where(s => !s.Trim().Equals(item.EntryText, StringComparison.OrdinalIgnoreCase))
                    .Where(s => !string.IsNullOrWhiteSpace(s));
                var next = string.Join(";", kept);

                if (string.IsNullOrWhiteSpace(next)) Reg.DeleteValue(regPath, item.ValueName);
                else Reg.SetExpandString(regPath, item.ValueName, next);
                return;
            }

            if (item.ValueName is null)
            {
                Reg.DeleteTree(RegPath.Of(item.Hive, item.SubKey));
            }
            else
            {
                Reg.DeleteValue(RegPath.Of(item.Hive, item.SubKey), item.ValueName);
            }
        });

        Native.NotifyAssociationsChanged();
    }
}
