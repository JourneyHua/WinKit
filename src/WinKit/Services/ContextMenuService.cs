using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class ContextMenuService
{
    private const string BlockedHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string BlockedHkcu = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private readonly ChangeJournal _journal;

    public ContextMenuService(ChangeJournal journal) => _journal = journal;

    public static string SendToFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "SendTo");

    public static string SendToBackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinKit", "SendToBackup");

    public static IReadOnlyList<MenuDomain> Domains => MenuDomain.All;

    public List<MenuEntry> Load(MenuDomainKind kind)
    {
        return kind switch
        {
            MenuDomainKind.SendTo => LoadSendTo(),
            MenuDomainKind.New => LoadShellNew(),
            _ => LoadClassDomain(MenuDomain.All.First(d => d.Kind == kind))
        };
    }

    private List<MenuEntry> LoadClassDomain(MenuDomain domain)
    {
        var list = new List<MenuEntry>();
        if (domain.ClassRoot is null) return list;

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var shellRoot = $@"Software\Classes\{domain.ClassRoot}\shell";
            var handlerRoot = $@"Software\Classes\{domain.ClassRoot}\shellex\ContextMenuHandlers";

            foreach (var name in Reg.SubKeyNames(RegPath.Of(hive, shellRoot)))
            {
                var entry = BuildVerbEntry(hive, domain, shellRoot, name);
                if (entry is not null) list.Add(entry);
            }

            foreach (var name in Reg.SubKeyNames(RegPath.Of(hive, handlerRoot)))
            {
                var entry = BuildHandlerEntry(hive, domain, handlerRoot, name);
                if (entry is not null) list.Add(entry);
            }
        }

        return list;
    }

    private MenuEntry? BuildVerbEntry(HiveKind hive, MenuDomain domain, string shellRoot, string childName)
    {
        var keyPath = $@"{shellRoot}\{childName}";
        var path = RegPath.Of(hive, keyPath);
        if (!Reg.Exists(path)) return null;

        var display = ResolveShellName(path, childName);
        if (display == childName) display = ResolveFallbackName(childName);
        display = CleanDisplayName(display);
        var command = Reg.GetString(RegPath.Of(hive, keyPath + @"\command")) ?? "";
        var exe = ExtractExecutable(command);
        var publisher = GuessPublisher(exe);
        var disabled = Reg.HasValue(path, "LegacyDisable") || Reg.HasValue(path, "ProgrammaticAccessOnly");
        var hasSubMenu = Reg.Exists(RegPath.Of(hive, keyPath + @"\shell"))
                         || Reg.HasValue(path, "SubCommands")
                         || Reg.HasValue(path, "ExtendedSubCommandsKey");

        return new MenuEntry
        {
            Id = $"{hive}:{keyPath}",
            DisplayName = display,
            KeyName = childName,
            Kind = MenuEntryKind.Verb,
            Domain = domain.Kind,
            DomainTitle = domain.Title,
            HiveText = hive == HiveKind.CurrentUser ? "当前用户" : "所有用户",
            Publisher = publisher,
            IsSystem = IsSystemPublisher(publisher, exe, hive),
            RegistryPath = path.FullPath,
            CommandPath = exe,
            IsDisabled = disabled,
            CanRename = true,
            Note = hasSubMenu ? "含子菜单" : ""
        };
    }

    private MenuEntry? BuildHandlerEntry(HiveKind hive, MenuDomain domain, string handlerRoot, string childName)
    {
        var keyPath = $@"{handlerRoot}\{childName}";
        var path = RegPath.Of(hive, keyPath);
        if (!Reg.Exists(path)) return null;

        var clsid = Reg.GetString(path)?.Trim();
        if (string.IsNullOrWhiteSpace(clsid)) return null;
        clsid = clsid.Split('\n', '\r')[0].Trim();

        var display = ResolveClsidName(clsid);
        if (string.IsNullOrWhiteSpace(display)) display = ResolveFallbackName(childName);
        var blocked = IsBlocked(clsid);
        var module = ResolveClsidModule(clsid);
        var publisher = GuessPublisher(module);

        return new MenuEntry
        {
            Id = $"{hive}:{keyPath}",
            DisplayName = CleanDisplayName(string.IsNullOrWhiteSpace(display) ? "未知扩展" : display!),
            KeyName = childName,
            Kind = MenuEntryKind.Handler,
            Domain = domain.Kind,
            DomainTitle = domain.Title,
            HiveText = hive == HiveKind.CurrentUser ? "当前用户" : "所有用户",
            Publisher = publisher,
            IsSystem = IsSystemPublisher(publisher, module, hive),
            RegistryPath = path.FullPath,
            CommandPath = module ?? "",
            Clsid = clsid,
            IsDisabled = blocked,
            CanRename = false,
            CanToggle = true,
            Note = blocked ? "已写入屏蔽列表" : "COM 扩展"
        };
    }

    private List<MenuEntry> LoadSendTo()
    {
        var list = new List<MenuEntry>();

        if (Directory.Exists(SendToFolder))
        {
            foreach (var file in Directory.GetFiles(SendToFolder))
            {
                var info = new FileInfo(file);
                list.Add(new MenuEntry
                {
                    Id = "sendto:" + info.Name,
                    DisplayName = Path.GetFileNameWithoutExtension(info.Name),
                    KeyName = info.Name,
                    Kind = MenuEntryKind.SendToItem,
                    Domain = MenuDomainKind.SendTo,
                    DomainTitle = "发送到",
                    HiveText = "用户文件夹",
                    Publisher = Path.GetFileNameWithoutExtension(info.Name),
                    IsSystem = false,
                    RegistryPath = file,
                    CommandPath = file,
                    IsDisabled = false,
                    CanRename = false,
                    CanToggle = true,
                    CanDelete = true,
                    Note = "快捷方式文件"
                });
            }
        }

        if (Directory.Exists(SendToBackupFolder))
        {
            foreach (var file in Directory.GetFiles(SendToBackupFolder))
            {
                var info = new FileInfo(file);
                list.Add(new MenuEntry
                {
                    Id = "sendto-off:" + info.Name,
                    DisplayName = Path.GetFileNameWithoutExtension(info.Name),
                    KeyName = info.Name,
                    Kind = MenuEntryKind.SendToItem,
                    Domain = MenuDomainKind.SendTo,
                    DomainTitle = "发送到",
                    HiveText = "已移入备份",
                    Publisher = "（已禁用）",
                    IsSystem = false,
                    RegistryPath = file,
                    CommandPath = file,
                    IsDisabled = true,
                    CanRename = false,
                    CanToggle = true,
                    CanDelete = true,
                    Note = "文件已移至 WinKit 备份目录"
                });
            }
        }

        return list;
    }

    private List<MenuEntry> LoadShellNew()
    {
        var list = new List<MenuEntry>();

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var classesRoot = RegPath.Of(hive, @"Software\Classes");
            foreach (var ext in Reg.SubKeyNames(classesRoot))
            {
                if (!ext.StartsWith('.')) continue;

                var activePath = $@"Software\Classes\{ext}\ShellNew";
                var disabledPath = activePath + ".WinKitDisabled";
                var isActive = Reg.Exists(RegPath.Of(hive, activePath));
                var isDisabled = Reg.Exists(RegPath.Of(hive, disabledPath));
                if (!isActive && !isDisabled) continue;

                var progId = Reg.GetString(RegPath.Of(hive, $@"Software\Classes\{ext}")) ?? "";
                var friendly = string.IsNullOrWhiteSpace(progId)
                    ? ""
                    : ResolveShellName(RegPath.Of(hive, $@"Software\Classes\{progId}"), progId);

                list.Add(new MenuEntry
                {
                    Id = $"{hive}:new:{ext}",
                    DisplayName = string.IsNullOrWhiteSpace(friendly) ? $"{ext} 文件" : $"{friendly}（{ext}）",
                    KeyName = ext,
                    Kind = MenuEntryKind.ShellNew,
                    Domain = MenuDomainKind.New,
                    DomainTitle = "新建",
                    HiveText = hive == HiveKind.CurrentUser ? "当前用户" : "所有用户",
                    Publisher = string.IsNullOrWhiteSpace(progId) ? "未知" : progId,
                    IsSystem = hive == HiveKind.LocalMachine,
                    RegistryPath = RegPath.Of(hive, activePath).FullPath,
                    CommandPath = "",
                    IsDisabled = !isActive && isDisabled,
                    CanRename = false,
                    CanToggle = true,
                    CanDelete = true,
                    Note = "通过重命名注册表项实现禁用"
                });
            }
        }

        return list;
    }

    public void SetEnabled(MenuEntry entry, bool enabled)
    {
        switch (entry.Kind)
        {
            case MenuEntryKind.Verb:
                ToggleVerb(entry, enabled);
                break;
            case MenuEntryKind.Handler:
                ToggleHandler(entry, enabled);
                break;
            case MenuEntryKind.SendToItem:
                ToggleSendTo(entry, enabled);
                break;
            case MenuEntryKind.ShellNew:
                ToggleShellNew(entry, enabled);
                break;
        }

        entry.IsDisabled = !enabled;
        Native.NotifyAssociationsChanged();
    }

    private void ToggleVerb(MenuEntry entry, bool enabled)
    {
        var (hive, subKey) = ParseId(entry.Id);
        var path = RegPath.Of(hive, subKey);
        var title = $"{(enabled ? "启用" : "禁用")}菜单项：{entry.DisplayName}";

        _journal.Record(title, new[] { (hive, subKey) }, () =>
        {
            if (enabled)
            {
                Reg.DeleteValue(path, "LegacyDisable");
                Reg.DeleteValue(path, "ProgrammaticAccessOnly");
            }
            else
            {
                Reg.SetString(path, "LegacyDisable", "");
            }
        });
    }

    private void ToggleHandler(MenuEntry entry, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(entry.Clsid)) return;
        var clsid = entry.Clsid;
        var title = $"{(enabled ? "启用" : "禁用")}COM 扩展：{entry.DisplayName}";

        _journal.Record(title, new[]
        {
            (HiveKind.LocalMachine, BlockedHklm),
            (HiveKind.CurrentUser, BlockedHkcu)
        }, () =>
        {
            if (enabled)
            {
                Reg.DeleteValue(RegPath.Hklm(BlockedHklm), clsid);
                Reg.DeleteValue(RegPath.Hkcu(BlockedHkcu), clsid);
            }
            else
            {
                Reg.SetString(RegPath.Hklm(BlockedHklm), clsid, "");
            }
        });
    }

    private void ToggleSendTo(MenuEntry entry, bool enabled)
    {
        Directory.CreateDirectory(SendToFolder);
        Directory.CreateDirectory(SendToBackupFolder);

        var activeFile = Path.Combine(SendToFolder, entry.KeyName);
        var backupFile = Path.Combine(SendToBackupFolder, entry.KeyName);
        var source = enabled ? backupFile : activeFile;
        var target = enabled ? activeFile : backupFile;
        var title = $"{(enabled ? "启用" : "禁用")}发送到项：{entry.DisplayName}";

        _journal.Record(title, Array.Empty<(HiveKind, string)>(), () =>
        {
            if (File.Exists(source))
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
            }
        }, new[] { activeFile, backupFile });

        entry.RegistryPath = target;
        entry.CommandPath = target;
        entry.HiveText = enabled ? "用户文件夹" : "已移入备份";
        Native.NotifyAssociationsChanged();
    }

    private void ToggleShellNew(MenuEntry entry, bool enabled)
    {
        var (hive, _) = ParseId(entry.Id);
        var ext = entry.KeyName;
        var activePath = $@"Software\Classes\{ext}\ShellNew";
        var disabledPath = activePath + ".WinKitDisabled";

        var title = $"{(enabled ? "启用" : "禁用")}新建项：{entry.DisplayName}";

        _journal.Record(title, new[]
        {
            (hive, activePath),
            (hive, disabledPath)
        }, () =>
        {
            if (enabled)
            {
                if (Reg.Exists(RegPath.Of(hive, disabledPath)))
                    MoveKey(hive, disabledPath, activePath);
            }
            else
            {
                if (Reg.Exists(RegPath.Of(hive, activePath)))
                    MoveKey(hive, activePath, disabledPath);
            }
        });
    }

    private static void MoveKey(HiveKind hive, string from, string to)
    {
        var snapshot = SnapshotEngine.Capture(hive, from);
        snapshot.SubKey = to;
        snapshot.Existed = true;
        SnapshotEngine.Restore(snapshot);
        Reg.DeleteTree(RegPath.Of(hive, from));
    }

    public void Delete(MenuEntry entry)
    {
        switch (entry.Kind)
        {
            case MenuEntryKind.SendToItem:
                var activeFile = Path.Combine(SendToFolder, entry.KeyName);
                var backupFile = Path.Combine(SendToBackupFolder, entry.KeyName);
                _journal.Record(
                    $"删除发送到项：{entry.DisplayName}",
                    Array.Empty<(HiveKind, string)>(),
                    () =>
                    {
                        if (File.Exists(entry.RegistryPath)) File.Delete(entry.RegistryPath);
                        if (File.Exists(activeFile)) File.Delete(activeFile);
                        if (File.Exists(backupFile)) File.Delete(backupFile);
                    },
                    new[] { entry.RegistryPath, activeFile, backupFile });
                break;
            case MenuEntryKind.ShellNew:
                // ShellNew 的 Id 是 "{hive}:new:{ext}"，不能走 ParseId（会得到 "new:.txt" 这种不存在的键）
                var (newHive, _) = ParseId(entry.Id);
                var newActive = $@"Software\Classes\{entry.KeyName}\ShellNew";
                var newDisabled = newActive + ".WinKitDisabled";
                _journal.Record($"删除新建项：{entry.DisplayName}", new[]
                {
                    (newHive, newActive),
                    (newHive, newDisabled)
                }, () =>
                {
                    Reg.DeleteTree(RegPath.Of(newHive, newActive));
                    Reg.DeleteTree(RegPath.Of(newHive, newDisabled));
                });
                break;
            default:
                var (hive, subKey) = ParseId(entry.Id);
                _journal.Record($"删除菜单项：{entry.DisplayName}", new[] { (hive, subKey) },
                    () => Reg.DeleteTree(RegPath.Of(hive, subKey)));
                break;
        }

        Native.NotifyAssociationsChanged();
    }

    public void Rename(MenuEntry entry, string newName)
    {
        if (!entry.CanRename || string.IsNullOrWhiteSpace(newName)) return;
        var (hive, subKey) = ParseId(entry.Id);
        _journal.Record($"重命名菜单项：{entry.DisplayName} → {newName}", new[] { (hive, subKey) },
            () => Reg.SetString(RegPath.Of(hive, subKey), "MUIVerb", newName));
        Native.NotifyAssociationsChanged();
    }

    public static bool IsBlocked(string clsid)
    {
        return Reg.HasValue(RegPath.Hklm(BlockedHklm), clsid)
               || Reg.HasValue(RegPath.Hkcu(BlockedHkcu), clsid);
    }

    public static IReadOnlyList<string> ListBlockedClsids()
    {
        var result = new List<string>();
        foreach (var path in new[] { RegPath.Hklm(BlockedHklm), RegPath.Hkcu(BlockedHkcu) })
        {
            using var key = Reg.Open(path, false);
            if (key is null) continue;
            foreach (var name in key.GetValueNames())
                if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(name);
        }
        return result;
    }

    /// <summary>把 "HKEY_LOCAL_MACHINE\SOFTWARE\..." 这类完整路径拆成 hive + 子键。</summary>
    public static (HiveKind Hive, string SubKey) ParseHivePath(string fullPath)
    {
        var path = fullPath.Trim();
        if (path.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
            return (HiveKind.CurrentUser, path["HKEY_CURRENT_USER\\".Length..]);
        if (path.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            return (HiveKind.LocalMachine, path["HKEY_LOCAL_MACHINE\\".Length..]);
        if (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            return (HiveKind.CurrentUser, path["HKCU\\".Length..]);
        if (path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            return (HiveKind.LocalMachine, path["HKLM\\".Length..]);

        return (HiveKind.CurrentUser, path);
    }

    public static (HiveKind Hive, string SubKey) ParseId(string id)    {
        var idx = id.IndexOf(':');
        if (idx <= 0) return (HiveKind.CurrentUser, id);
        var hive = id[..idx] == "LocalMachine" || id[..idx].Equals("HKLM", StringComparison.OrdinalIgnoreCase)
            ? HiveKind.LocalMachine
            : HiveKind.CurrentUser;
        return (hive, id[(idx + 1)..]);
    }

    public static string ResolveShellName(RegPath key, string fallback)
    {
        var muiVerb = Reg.GetString(key, "MUIVerb");
        var resolved = Native.ResolveIndirectString(muiVerb);
        if (!string.IsNullOrWhiteSpace(resolved)) return CleanAccelerators(resolved);
        if (!string.IsNullOrWhiteSpace(muiVerb) && !muiVerb!.StartsWith('@')) return CleanAccelerators(muiVerb);

        var defaultValue = Reg.GetString(key);
        resolved = Native.ResolveIndirectString(defaultValue);
        if (!string.IsNullOrWhiteSpace(resolved)) return CleanAccelerators(resolved);
        if (!string.IsNullOrWhiteSpace(defaultValue) && !defaultValue!.StartsWith('@')) return CleanAccelerators(defaultValue);

        return fallback;
    }

    /// <summary>去掉菜单名里的 &amp; 加速键标记，“&amp;&amp;”还原为单个 &amp;。</summary>
    public static string CleanAccelerators(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const string sentinel = "\u0001";
        var cleaned = text.Replace("&&", sentinel).Replace("&", "").Replace(sentinel, "&").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned;
    }

    /// <summary>清理 COM 类名里的 “ Class”“ContextMenu” 等技术后缀，让名称更像人话。</summary>
    public static string CleanDisplayName(string display)
    {
        var text = CleanAccelerators(display);
        if (text.EndsWith(" Class", StringComparison.OrdinalIgnoreCase)) text = text[..^6].Trim();

        foreach (var suffix in new[] { "ContextMenuHandler", "ContextMenu", "ShellExt", "Shellext", "ShellExtension" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && text.Length > suffix.Length)
            {
                text = text[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(text) ? display : text;
    }

    /// <summary>名称完全解析不出来时的兜底：GUID 型键名尝试当 CLSID 解析。</summary>
    public static string ResolveFallbackName(string keyName)
    {
        if (keyName.StartsWith('{'))
        {
            var byClsid = ResolveClsidName(keyName);
            if (!string.IsNullOrWhiteSpace(byClsid)) return CleanDisplayName(byClsid);
            return "系统内置项";
        }

        return keyName;
    }

    public static string? ResolveClsidName(string clsid)
    {
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var path = RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}");
            var name = ResolveShellName(path, "");
            if (!string.IsNullOrWhiteSpace(name)) return name;

            var localized = Native.ResolveIndirectString(Reg.GetString(path, "LocalizedString"));
            if (!string.IsNullOrWhiteSpace(localized)) return localized;
        }

        return null;
    }

    public static string? ResolveClsidModule(string clsid)
    {
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            foreach (var server in new[] { "InprocServer32", "LocalServer32" })
            {
                var raw = Reg.GetString(RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}\{server}"));
                var exe = ExtractExecutable(raw ?? "");
                if (!string.IsNullOrWhiteSpace(exe)) return exe;
            }
        }

        return null;
    }

    public static string ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var cmd = command.Trim();

        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : cmd.Trim('"');
        }

        var lower = cmd.ToLowerInvariant();
        var exeIndex = lower.IndexOf(".exe", StringComparison.Ordinal);
        if (exeIndex > 0) return cmd[..(exeIndex + 4)].Trim();

        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    public static string GuessPublisher(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "未知";

        var path = exePath.Replace('/', '\\');
        var lower = path.ToLowerInvariant();

        if (lower.Contains(@"\windows\system32") || lower.Contains(@"\windows\syswow64") ||
            lower.StartsWith(@"c:\windows\") || lower.Contains(@"\windows\explorer"))
            return "Windows";

        foreach (var marker in new[] { @"\program files (x86)\", @"\program files\", @"\programdata\" })
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = path[(idx + marker.Length)..];
                var seg = rest.Split('\\').FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(seg)) return seg;
            }
        }

        foreach (var marker in new[] { @"\appdata\local\programs\", @"\appdata\local\", @"\appdata\roaming\" })
        {
            var idx = lower.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var rest = path[(idx + marker.Length)..];
                var seg = rest.Split('\\').FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(seg)) return seg;
            }
        }

        var file = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(file) ? "未知" : file;
    }

    private static bool IsSystemPublisher(string publisher, string? exePath, HiveKind hive)
    {
        if (publisher.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return true;
        if (hive == HiveKind.LocalMachine && !string.IsNullOrWhiteSpace(exePath))
        {
            var lower = exePath!.ToLowerInvariant();
            return lower.Contains(@"\windows\");
        }
        return false;
    }
}
