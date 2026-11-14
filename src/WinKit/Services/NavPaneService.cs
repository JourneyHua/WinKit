using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinKit.Core;

namespace WinKit.Services;

public sealed class QuickAccessItem : ObservableObject
{
    private int _order;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsFolder { get; init; }

    public string KindText => IsFolder ? "固定文件夹" : "最近使用";

    public int Order
    {
        get => _order;
        set => Set(ref _order, value);
    }
}

public sealed class NavToggleItem : ObservableObject
{
    private bool _isOn;

    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            _isOn = value;
            OnPropertyChanged();
            ToggleRequested?.Invoke(this, value);
        }
    }

    public Action<NavToggleItem, bool>? ToggleRequested { get; set; }
}

public sealed class NavPaneService
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string QuickAccessClsid = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    private readonly ChangeJournal _journal;

    public NavPaneService(ChangeJournal journal) => _journal = journal;

    public (List<QuickAccessItem> Items, string? Error) LoadQuickAccess()
    {
        var items = new List<QuickAccessItem>();

        object? shell = null;
        object? folder = null;
        object? collection = null;

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return (items, "系统未注册 Shell.Application");

            shell = Activator.CreateInstance(type);
            if (shell is null) return (items, "无法创建 Shell.Application");

            folder = ((dynamic)shell).Namespace(QuickAccessClsid);
            if (folder is null) return (items, "无法打开快速访问命名空间");

            collection = ((dynamic)folder).Items();
            var count = (int)((dynamic)collection).Count;
            var order = 0;

            for (var i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)collection).Item(i);
                    if (item is null) continue;

                    string name = "";
                    string path = "";
                    try { name = (string)((dynamic)item).Name; } catch (Exception) { }
                    try { path = (string)((dynamic)item).Path; } catch (Exception) { }

                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(path)) continue;

                    // 用文件系统判断是否文件夹，COM 的 IsFolder 在快速访问命名空间里不可靠
                    var isFolder = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

                    items.Add(new QuickAccessItem
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? path : name,
                        Path = path,
                        IsFolder = isFolder,
                        Order = order++
                    });
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        catch (Exception ex)
        {
            return (items, ex.Message);
        }
        finally
        {
            ReleaseCom(collection);
            ReleaseCom(folder);
            ReleaseCom(shell);
        }

        // 已固定的文件夹排在前面，最近使用的文件排在后面
        return (items.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Order).ToList(), null);
    }

    public void Pin(string folderPath)
    {
        if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException(folderPath);
        RunVerb(folderPath, "pintohome");
    }

    public void Unpin(string itemPath)
    {
        RunVerb(itemPath, "unpinfromhome");
    }

    /// <summary>
    /// 重新排列快速访问固定项：先全部取消，再按给定顺序重新固定（逐个等待，较慢但可靠）。
    /// 返回未能恢复的路径，调用方必须提示用户，不能静默当作成功。
    /// </summary>
    public IReadOnlyList<string> RebuildOrder(IReadOnlyList<string> orderedPaths, Action<string>? progress = null)
    {
        var (current, error) = LoadQuickAccess();
        if (error is not null) throw new InvalidOperationException(error);

        var failures = new List<string>();

        foreach (var item in current.Where(i => i.IsFolder && !string.IsNullOrWhiteSpace(i.Path)))
        {
            try
            {
                progress?.Invoke($"取消固定：{item.Name}");
                Unpin(item.Path);
                Thread.Sleep(120);
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}（取消固定失败：{ex.Message}）");
            }
        }

        foreach (var path in orderedPaths)
        {
            try
            {
                progress?.Invoke($"固定：{path}");
                Pin(path);
                Thread.Sleep(180);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}（重新固定失败：{ex.Message}）");
            }
        }

        return failures;
    }

    public List<NavToggleItem> LoadToggles()
    {
        var advanced = RegPath.Hkcu(AdvancedKey);
        var explorer = RegPath.Hkcu(ExplorerKey);

        return new List<NavToggleItem>
        {
            new()
            {
                Id = "showAllFolders",
                Title = "显示所有文件夹",
                Description = "在导航窗格中把“此电脑”的每个文件夹都平铺出来（对应 NavPaneShowAllFolders）",
                IsOn = (Reg.GetInt(advanced, "NavPaneShowAllFolders") ?? 0) == 1
            },
            new()
            {
                Id = "expandToCurrent",
                Title = "自动展开到当前文件夹",
                Description = "导航窗格自动定位并展开你当前所在的位置（NavPaneExpandToCurrentFolder）",
                IsOn = (Reg.GetInt(advanced, "NavPaneExpandToCurrentFolder") ?? 0) == 1
            },
            new()
            {
                Id = "showRecent",
                Title = "显示最近使用的文件",
                Description = "快速访问里列出最近打开的文件（ShowRecent）",
                IsOn = (Reg.GetInt(explorer, "ShowRecent") ?? 1) == 1
            },
            new()
            {
                Id = "showFrequent",
                Title = "显示常用文件夹",
                Description = "快速访问里列出你经常访问的文件夹（ShowFrequent）",
                IsOn = (Reg.GetInt(explorer, "ShowFrequent") ?? 1) == 1
            }
        };
    }

    public void SetToggle(NavToggleItem item, bool value)
    {
        var (subKey, name) = item.Id switch
        {
            "showAllFolders" => (AdvancedKey, "NavPaneShowAllFolders"),
            "expandToCurrent" => (AdvancedKey, "NavPaneExpandToCurrentFolder"),
            "showRecent" => (ExplorerKey, "ShowRecent"),
            "showFrequent" => (ExplorerKey, "ShowFrequent"),
            _ => throw new ArgumentOutOfRangeException(nameof(item))
        };

        _journal.Record($"{(value ? "开启" : "关闭")}：{item.Title}", new[] { (HiveKind.CurrentUser, subKey) },
            () => Reg.SetInt(RegPath.Hkcu(subKey), name, value ? 1 : 0));

        item.IsOn = value;
        Native.NotifyAssociationsChanged();
    }

    private static void RunVerb(string path, string verb)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            // pintohome / unpinfromhome 只注册了 CommandStateHandler，没有 command 子键，
            // 必须让 ShellExecuteEx 走 IContextMenu 调用（SEE_MASK_INVOKEIDLIST = 0x0C）。
            fMask = 0x0000000C,
            lpVerb = verb,
            lpFile = path,
            nShow = 1
        };

        if (!ShellExecuteEx(ref info))
        {
            var code = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"执行“{verb}”失败（错误码 {code}）");
        }
    }

    private static void ReleaseCom(object? comObject)
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
}
