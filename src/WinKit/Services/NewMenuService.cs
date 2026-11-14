using WinKit.Core;

namespace WinKit.Services;

public sealed class NewMenuItem : ObservableObject
{
    private bool _isBlocked;

    public string Package { get; init; } = "";
    public IReadOnlyList<string> Packages { get; init; } = Array.Empty<string>();
    public string DisplayName { get; init; } = "";
    public string ClassNames { get; init; } = "";
    public string Location { get; init; } = "";
    public IReadOnlyList<string> Clsids { get; init; } = Array.Empty<string>();
    public bool CanBlock { get; init; } = true;

    private string _note = "";

    public string Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>清单里声明的作用域；空 = 未确认。</summary>
    public IReadOnlyList<MenuDomainKind> Scopes { get; init; } = Array.Empty<MenuDomainKind>();
    public bool ScopeConfirmed { get; init; }

    public string ScopeText
    {
        get
        {
            if (!ScopeConfirmed) return "无法确认";
            if (Scopes.Count == 0) return "—";

            return string.Join("、", Scopes.Select(ToScopeLabel));
        }
    }

    private bool _showDriveInherited;

    /// <summary>当前在「驱动器」分组下、且是通过文件夹作用域继承来的。</summary>
    public bool ShowDriveInherited
    {
        get => _showDriveInherited;
        set
        {
            if (Set(ref _showDriveInherited, value)) OnPropertyChanged(nameof(ScopeText));
        }
    }

    private static string ToScopeLabel(MenuDomainKind kind) => kind switch
    {
        MenuDomainKind.File => "文件",
        MenuDomainKind.Folder => "文件夹",
        MenuDomainKind.FolderBackground => "空白处",
        MenuDomainKind.Drive => "驱动器",
        MenuDomainKind.DesktopBackground => "桌面",
        MenuDomainKind.Library => "库",
        _ => kind.ToString()
    };

    public string ClsidText => Clsids.Count == 0 ? "" : string.Join(", ", Clsids);

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (Set(ref _isBlocked, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsBlockedOn));
                OnPropertyChanged(nameof(BlockToggleText));
            }
        }
    }

    public bool IsBlockedOn
    {
        get => IsBlocked;
        set
        {
            if (value == IsBlocked) return;
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<NewMenuItem, bool>? ToggleRequested { get; set; }

    private bool _deepBlocked;

    /// <summary>屏蔽后 PackagedCom 里的类登记是否已被停用（改名）。</summary>
    public bool DeepBlocked
    {
        get => _deepBlocked;
        set
        {
            if (Set(ref _deepBlocked, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => !IsBlocked ? "显示中" : DeepBlocked ? "已屏蔽·深度" : "已屏蔽";

    public string BlockToggleText => IsBlocked ? "恢复" : "屏蔽";

    public string SearchBlob =>
        $"{DisplayName} {Package} {ClassNames} {Location} {ScopeText} {ClsidText}".ToLowerInvariant();
}

/// <summary>
/// 管理 Win11 一级（紧凑）右键菜单里的第三方项——即不点“显示更多选项”时就能看到的那一层。
/// 按软件包分组，一个软件一行；屏蔽 = 把该包所有 IExplorerCommand 的 CLSID 写入 Shell Extensions\Blocked。
/// 系统内置项由 Windows 硬编码，无公开接口移除，只能诚实展示。
/// </summary>
public sealed class NewMenuService
{
    private const string BlockedHklm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string BlockedHkcu = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string AppModelPackages =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly string[] InfraPackageMarkers =
    {
        "WinAppRuntime", "DDLM", "VCLibs", "UI.Xaml", "NET.Native",
        "WindowsAppRuntime", "WinUI", "DesktopAppRuntime", "Runtime"
    };

    private readonly ChangeJournal _journal;

    public NewMenuService(ChangeJournal journal) => _journal = journal;

    /// <summary>上一次 Load 时清单读取情况：(成功数, 尝试数)。</summary>
    public (int Found, int Total) LastManifestStats { get; private set; }

    public static IReadOnlyList<string> BuiltInItems { get; } = new[]
    {
        "打开 / 打开方式", "剪切", "复制", "粘贴", "创建快捷方式", "删除", "重命名", "共享",
        "发送到", "属性", "显示更多选项"
    };

    public List<NewMenuItem> Load()
    {
        // 先按包归并所有 IExplorerCommand 的 CLSID
        var packages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
        {
            var packageRoot = RegPath.Of(hive, @"Software\Classes\PackagedCom\Package");
            foreach (var package in Reg.SubKeyNames(packageRoot))
            {
                if (IsInfrastructurePackage(package)) continue;

                var classRoot = RegPath.Of(hive, $@"Software\Classes\PackagedCom\Package\{package}\Class");
                var clsids = Reg.SubKeyNames(classRoot);
                if (clsids.Count == 0) continue;

                if (!packages.TryGetValue(package, out var list))
                {
                    list = new List<string>();
                    packages[package] = list;
                }

                foreach (var clsid in clsids)
                    if (!list.Contains(clsid, StringComparer.OrdinalIgnoreCase))
                        list.Add(clsid);
            }
        }

        // 同一个软件可能有多个版本的包，按显示名合并成一行，并保留全部包名以免漏改
        var merged = new Dictionary<string, (List<string> Packages, List<string> Clsids, string Location, ManifestScopeInfo Manifest)>(StringComparer.OrdinalIgnoreCase);
        var manifests = new Dictionary<string, ManifestScopeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in packages)
        {
            var package = pair.Key;

            if (!manifests.TryGetValue(package, out var manifest))
            {
                manifest = AppxManifest.Read(package, ResolveInstallRoot(package));
                manifests[package] = manifest;
            }

            // 清单读到了、但压根没声明右键菜单 → 它根本不是菜单项（背景任务、预览器、缩略图之类），丢掉
            if (manifest.Found && !manifest.DeclaresContextMenu) continue;

            var effective = pair.Value;
            if (manifest.Found && manifest.Clsids.Count > 0)
            {
                // 清单里的 GUID 不带花括号，注册表键名带；统一去掉再比
                var declared = manifest.Clsids
                    .Select(NormalizeClsid)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var filtered = pair.Value.Where(c => declared.Contains(NormalizeClsid(c))).ToList();
                effective = filtered.Count > 0
                    ? filtered
                    : manifest.Clsids.Select(d => "{" + NormalizeClsid(d) + "}").ToList();
            }

            var display = ResolvePackageDisplayName(package);
            var nameResolved = !string.IsNullOrWhiteSpace(display);
            if (!nameResolved) display = HumanizePackage(package);

            // 名称没解析出来时不能按短名合并：不同厂商的包很容易都叫 "Update"，
            // 合并后屏蔽一个会连带把另一个的 CLSID 一起写进 Blocked。
            var mergeKey = nameResolved ? display! : package;

            var location = ResolveInstallLocation(package);

            if (!merged.TryGetValue(mergeKey, out var entry))
            {
                entry = (new List<string>(), new List<string>(), location, manifest);
                merged[mergeKey] = entry;
            }

            if (!entry.Packages.Contains(package, StringComparer.OrdinalIgnoreCase))
                entry.Packages.Add(package);

            foreach (var clsid in effective)
                if (!entry.Clsids.Contains(clsid, StringComparer.OrdinalIgnoreCase))
                    entry.Clsids.Add(clsid);

            if (string.IsNullOrWhiteSpace(entry.Location) && !string.IsNullOrWhiteSpace(location))
                entry.Location = location;

            if (!entry.Manifest.Found && manifest.Found)
            {
                entry.Manifest = manifest;
            }
            else if (entry.Manifest.Found && manifest.Found)
            {
                foreach (var scopeType in manifest.ScopeTypes)
                {
                    if (!entry.Manifest.ScopeTypes.Contains(scopeType, StringComparer.OrdinalIgnoreCase))
                        entry.Manifest.ScopeTypes.Add(scopeType);
                }
            }

            merged[mergeKey] = entry;
        }

        var items = new List<NewMenuItem>();

        foreach (var pair in merged)
        {
            var display = pair.Key;
            var (pkgList, clsids, location, manifest) = pair.Value;
            if (clsids.Count == 0 || pkgList.Count == 0) continue;

            var blockedCount = clsids.Count(ContextMenuService.IsBlocked);

            var scopes = manifest.ScopeTypes
                .Select(AppxManifest.MapScope)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .Distinct()
                .ToList();

            // 清单读不到时，clsids 是整包登记的全部 COM 类（含预览器/后台任务），
            // 屏蔽会把非菜单的壳扩展一起废掉，所以这种情况下不允许屏蔽。
            var canBlock = manifest.Found;

            var note = blockedCount > 0 && blockedCount < clsids.Count ? "部分类已被屏蔽" : "";
            if (!manifest.Found)
                note = "读不到包清单，无法确认哪些类属于右键菜单，已禁用屏蔽";
            else if (string.IsNullOrWhiteSpace(ResolvePackageDisplayName(pkgList[0])))
                note = string.IsNullOrWhiteSpace(note) ? "显示名未解析，按包名单独列出" : note;

            items.Add(new NewMenuItem
            {
                Package = string.Join(" / ", pkgList),
                Packages = pkgList,
                DisplayName = display,
                ClassNames = DescribeClasses(clsids),
                Location = location,
                Clsids = clsids,
                Scopes = scopes,
                ScopeConfirmed = manifest.Found && manifest.ScopeTypes.Count > 0,
                IsBlocked = blockedCount > 0,
                DeepBlocked = blockedCount > 0 && clsids.Any(c => !ClassKeyActive(pkgList, c)),
                CanBlock = canBlock,
                Note = note
            });
        }

        LastManifestStats = (manifests.Count(m => m.Value.Found), manifests.Count);

        return items
            .OrderByDescending(i => i.IsBlocked)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static string NormalizeClsid(string clsid) => clsid.Trim().Trim('{', '}');

    private static string DescribeClasses(IReadOnlyList<string> clsids)
    {
        var names = clsids
            .Select(c => ContextMenuService.ResolveClsidName(c) ?? "")
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(ContextMenuService.CleanDisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0) return "—";
        return names.Count <= 2 ? string.Join(" / ", names) : $"{names[0]} 等 {names.Count} 项";
    }

    /// <summary>从 AppModel 仓库读包的真实显示名，拿不到时返回空。</summary>
    private static string? ResolvePackageDisplayName(string package)
    {
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var path = RegPath.Of(hive, $@"{AppModelPackages}\{package}");
            var raw = Reg.GetString(path, "DisplayName");
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var resolved = Native.ResolveIndirectString(raw);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved!.Trim();

            // ms-resource: 这类是包内资源占位符，界面里没法解析，当没拿到处理
            if (raw!.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;

            if (!raw.StartsWith('@')) return raw.Trim();
        }

        return null;
    }

    private static string ResolveInstallRoot(string package)
    {
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var folder = Reg.GetString(RegPath.Of(hive, $@"{AppModelPackages}\{package}"), "PackageRootFolder");
            if (!string.IsNullOrWhiteSpace(folder)) return folder!;
        }

        return "";
    }

    private static string ResolveInstallLocation(string package)
    {
        var folder = ResolveInstallRoot(package);
        if (string.IsNullOrWhiteSpace(folder)) return "";

        // 只保留到厂商目录一级，例如 D:\Bandizip\data -> D:\Bandizip
        var parts = folder.TrimEnd('\\').Split('\\');
        return parts.Length >= 2 ? string.Join("\\", parts.Take(parts.Length - 1)) : folder;
    }

    private static bool IsInfrastructurePackage(string package)
    {
        foreach (var marker in InfraPackageMarkers)
            if (package.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in new[] { "MicrosoftWindows.", "Microsoft.Windows.", "Microsoft.UI.", "Microsoft.UI.Xaml" })
            if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string HumanizePackage(string package)
    {
        var name = package.Split('_')[0];
        var parts = name.Split('.');

        var meaningful = parts
            .Where(p => p.Any(char.IsLetter) && !p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return meaningful.Count > 0 ? meaningful[^1] : name;
    }

    public void SetBlocked(NewMenuItem item, bool blocked)
    {
        if (!item.CanBlock)
            throw new InvalidOperationException("该软件的包清单无法读取，无法确定哪些 COM 类属于右键菜单，已阻止屏蔽以避免误伤其它壳扩展。");

        var clsids = item.Clsids.ToList();
        if (clsids.Count == 0) return;

        var packages = item.Packages.Count > 0
            ? item.Packages
            : string.IsNullOrWhiteSpace(item.Package)
                ? Array.Empty<string>()
                : new[] { item.Package };

        var tracked = new List<(HiveKind Hive, string SubKey)>
        {
            (HiveKind.LocalMachine, BlockedHklm),
            (HiveKind.CurrentUser, BlockedHkcu)
        };

        // 打包应用的菜单项不光写屏蔽列表，还要把 PackagedCom 里的类登记改名——
        // 只写 Blocked 对 IExplorerCommand（稀疏包）经常不生效，这是实测结论。
        foreach (var pkg in packages)
        {
            foreach (var clsid in clsids)
            {
                var leaf = "{" + NormalizeClsid(clsid) + "}";
                foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
                {
                    var classKey = $@"Software\Classes\PackagedCom\Package\{pkg}\Class\{leaf}";
                    tracked.Add((hive, classKey));
                    tracked.Add((hive, classKey + DisabledSuffix));
                }
            }
        }

        _journal.Record($"{(blocked ? "屏蔽" : "恢复")}一级菜单项：{item.DisplayName}", tracked, () =>
        {
            foreach (var clsid in clsids)
            {
                var leaf = "{" + NormalizeClsid(clsid) + "}";

                if (blocked)
                {
                    Reg.SetString(RegPath.Hklm(BlockedHklm), leaf, "");
                }
                else
                {
                    Reg.DeleteValue(RegPath.Hklm(BlockedHklm), leaf);
                    Reg.DeleteValue(RegPath.Hkcu(BlockedHkcu), leaf);
                }

                foreach (var pkg in packages)
                {
                    var classKey = $@"Software\Classes\PackagedCom\Package\{pkg}\Class\{leaf}";
                    foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
                    {
                        DisableClassKey(hive, classKey, blocked);
                    }
                }
            }
        });

        item.IsBlocked = blocked;
        item.DeepBlocked = blocked && clsids.Any(c => !ClassKeyActive(packages, c));

        if (blocked && !item.DeepBlocked)
        {
            // 只写了 Blocked 而类登记还在活动状态：对 IExplorerCommand 常常不生效，必须让用户知道
            item.Note = "已写入屏蔽列表，但包内类登记未能停用（菜单项可能仍会显示）";
        }

        Native.NotifyAssociationsChanged();
    }

    private const string DisabledSuffix = ".WinKitDisabled";
    private const string ClassicMenuClsidKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string ClassicMenuInprocKey = ClassicMenuClsidKey + @"\InprocServer32";

    private static void DisableClassKey(HiveKind hive, string classKey, bool disable)
    {
        var source = disable ? classKey : classKey + DisabledSuffix;
        var target = disable ? classKey + DisabledSuffix : classKey;

        if (!Reg.Exists(RegPath.Of(hive, source))) return;

        if (Reg.Exists(RegPath.Of(hive, target)))
        {
            if (disable)
            {
                // 上次屏蔽留下的 .WinKitDisabled 还在，而软件更新又把活动键注册回来了：
                // 先清掉旧副本，否则下面的改名会被跳过，菜单项照旧存在。
                Reg.DeleteTree(RegPath.Of(hive, target));
            }
            else
            {
                // 恢复时活动键已被软件重新注册，直接丢掉过期的停用副本即可
                Reg.DeleteTree(RegPath.Of(hive, source));
                return;
            }
        }

        // 改名靠“捕获→写到新路径→删旧路径”，注册表没有原生重命名
        var snapshot = SnapshotEngine.Capture(hive, source);
        snapshot.SubKey = target;
        snapshot.Existed = true;
        SnapshotEngine.Restore(snapshot);
        Reg.DeleteTree(RegPath.Of(hive, source));
    }

    /// <summary>类登记是否还处于激活状态（改名走了就算停用）。</summary>
    public static bool ClassKeyActive(string package, string clsid)
        => ClassKeyActive(new[] { package }, clsid);

    public static bool ClassKeyActive(IReadOnlyList<string> packages, string clsid)
    {
        var leaf = "{" + NormalizeClsid(clsid) + "}";
        foreach (var package in packages)
        {
            foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
            {
                if (Reg.Exists(RegPath.Of(hive, $@"Software\Classes\PackagedCom\Package\{package}\Class\{leaf}")))
                    return true;
            }
        }

        return false;
    }

    public int ForcedClassicMenuState()
    {
        return Reg.Exists(RegPath.Hkcu(ClassicMenuInprocKey)) ? 1 : 0;
    }

    public void SetForcedClassicMenu(bool enabled)
    {
        _journal.Record(
            enabled ? "开启默认直接展开经典右键菜单" : "恢复 Win11 默认一级右键菜单",
            new[] { (HiveKind.CurrentUser, ClassicMenuClsidKey) },
            () =>
            {
                if (enabled)
                    Reg.SetString(RegPath.Hkcu(ClassicMenuInprocKey), "", "");
                else
                    Reg.DeleteTree(RegPath.Hkcu(ClassicMenuClsidKey));
            });

        Native.NotifyAssociationsChanged();
    }
}
