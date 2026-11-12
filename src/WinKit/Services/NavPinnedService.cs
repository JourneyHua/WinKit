using WinKit.Core;

namespace WinKit.Services;

public sealed class PinnedFolderItem : ObservableObject
{
    private bool _isVisible = true;

    public string Clsid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ModulePath { get; init; } = "";
    public string OwnerText { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public string ValueName { get; init; } = "";
    public HiveKind Hive { get; init; }
    public bool CanToggle { get; init; } = true;
    public string Note { get; init; } = "";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (value == _isVisible) return;
            _isVisible = value;
            OnPropertyChanged(nameof(StatusText));
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<PinnedFolderItem, bool>? ToggleRequested { get; set; }

    public string StatusText => IsVisible ? "显示中" : "已隐藏";
}

/// <summary>
/// 管理资源管理器左侧那些由软件钉上去的节点（iCloud Photos、WPS 云盘、应用宝文件管理等）。
/// 原理：CLSID 上的 System.IsPinnedToNameSpaceTree = 1。改成 0 即从导航栏消失，改回 1 即恢复，不删任何键。
/// </summary>
public sealed class NavPinnedService
{
    private static readonly string[] PinValueNames =
    {
        "System.IsPinnedToNameSpaceTree",
        "System.IsPinnedToNamespaceTree"
    };

    private static readonly HashSet<string> ProtectedClsids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{679f85cb-0220-4080-b29b-5540cc05aab6}" // 快速访问本身
    };

    private static readonly Dictionary<string, string> KnownPinnedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{679f85cb-0220-4080-b29b-5540cc05aab6}"] = "快速访问",
        ["{f874310e-b6b7-47dc-bc84-b9e6b38f5903}"] = "云文件（系统内置）",
        ["{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}"] = "收藏夹（系统内置）",
        ["{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}"] = "Linux"
    };

    private static readonly HashSet<string> SystemPinnedClsids = new(StringComparer.OrdinalIgnoreCase)
    {
        "{679f85cb-0220-4080-b29b-5540cc05aab6}",
        "{f874310e-b6b7-47dc-bc84-b9e6b38f5903}",
        "{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}",
        "{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}"
    };

    private readonly ChangeJournal _journal;

    public NavPinnedService(ChangeJournal journal) => _journal = journal;

    public List<PinnedFolderItem> Load()
    {
        var result = new Dictionary<string, PinnedFolderItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, @"Software\Classes\CLSID")))
            {
                if (!clsid.StartsWith('{')) continue;

                var path = RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}");
                using var key = Reg.Open(path, false);
                if (key is null) continue;

                var valueName = PinValueNames.FirstOrDefault(n =>
                    key.GetValueNames().Any(v => v.Equals(n, StringComparison.OrdinalIgnoreCase)));
                if (valueName is null) continue;

                var raw = key.GetValue(valueName);
                var pinned = raw is int i && i != 0;

                if (result.ContainsKey(clsid)) continue;
                result[clsid] = BuildItem(hive, clsid, valueName, pinned);
            }
        }

        return result.Values
            .OrderBy(i => i.IsVisible ? 0 : 1)
            .ThenBy(i => i.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private PinnedFolderItem BuildItem(HiveKind hive, string clsid, string valueName, bool pinned)
    {
        var display = KnownPinnedNames.TryGetValue(clsid, out var known) ? known : null;

        if (string.IsNullOrWhiteSpace(display))
            display = ContextMenuService.ResolveClsidName(clsid);

        if (string.IsNullOrWhiteSpace(display))
            display = ContextMenuService.ResolveShellName(RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}"), "");

        if (string.IsNullOrWhiteSpace(display))
            display = SystemPinnedClsids.Contains(clsid) ? "系统内置节点" : clsid;

        var module = ContextMenuService.ResolveClsidModule(clsid) ?? "";
        var owner = DescribeOwner(module, clsid);

        return new PinnedFolderItem
        {
            Clsid = clsid,
            DisplayName = ContextMenuService.CleanDisplayName(display!),
            ModulePath = module,
            OwnerText = owner,
            RegistryPath = RegPath.Of(hive, $@"Software\Classes\CLSID\{clsid}").FullPath,
            ValueName = valueName,
            Hive = hive,
            IsVisible = pinned,
            CanToggle = !ProtectedClsids.Contains(clsid),
            Note = ProtectedClsids.Contains(clsid)
                ? "快速访问是资源管理器自身节点，不支持在此关闭"
                : "关掉后从左侧导航栏消失，随时可恢复"
        };
    }

    private static string DescribeOwner(string module, string clsid)
    {
        if (SystemPinnedClsids.Contains(clsid)) return "系统内置节点";
        if (string.IsNullOrWhiteSpace(module)) return "第三方软件注册";

        var lower = module.ToLowerInvariant();
        if (lower.Contains("shell32.dll") || lower.Contains("windows.storage.dll"))
            return "第三方软件注册";

        var publisher = ContextMenuService.GuessPublisher(module);
        return publisher is "未知" or "Windows" ? "第三方软件注册" : publisher;
    }

    public void SetVisible(PinnedFolderItem item, bool visible)
    {
        var path = RegPath.Of(item.Hive, $@"Software\Classes\CLSID\{item.Clsid}");
        var marker = RegPath.Hkcu(@"Software\WinKit\HiddenNavNodes");

        var title = $"{(visible ? "恢复" : "隐藏")}导航栏节点：{item.DisplayName}";

        _journal.Record(title, new[] { (item.Hive, $@"Software\Classes\CLSID\{item.Clsid}"), (HiveKind.CurrentUser, marker.SubKey) },
            () =>
            {
                Reg.SetInt(path, item.ValueName, visible ? 1 : 0);

                if (visible) Reg.DeleteValue(marker, item.Clsid);
                else Reg.SetString(marker, item.Clsid, item.DisplayName);
            });

        item.IsVisible = visible;
        Native.NotifyAssociationsChanged();
    }
}
