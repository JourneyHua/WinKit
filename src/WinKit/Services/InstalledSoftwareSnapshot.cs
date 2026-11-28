using System.Globalization;
using Microsoft.Win32;
using WinKit.Core;

namespace WinKit.Services;

/// <summary>未经应用列表过滤的只读记录，运行库仍能看到 SystemComponent 等登记。</summary>
public sealed record InstalledSoftwareEntry(
    HiveKind Hive, string SubKey, string Name, string Version, string Publisher,
    string InstallLocation, string UninstallString, string DisplayIcon,
    string ParentKeyName, bool IsSystemComponent, long SizeBytes, string InstallDate);

public sealed record InstalledStorePackage(
    HiveKind Hive, string SubKey, string PackageFullName, string DisplayName, string InstallLocation);

/// <summary>
/// 应用管理会话内共享的原始数据；每类首次访问才读取，不缓存 RegistryKey。
/// 手动刷新、安装/卸载结束及还原后由所属模块 Invalidate，普通切页复用。
/// 默认构造不读取系统，且没有进程级静态缓存。
/// </summary>
public sealed class InstalledSoftwareSnapshotCache
{
    private const string UninstallRoot = @"Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppModelRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string AppPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<InstalledSoftwareEntry>> _readUninstall;
    private readonly Func<IReadOnlyList<InstalledStorePackage>> _readStore;
    private readonly Func<IReadOnlyDictionary<string, string>> _readAppPaths;
    private Lazy<IReadOnlyList<InstalledSoftwareEntry>>? _uninstall;
    private Lazy<IReadOnlyList<InstalledStorePackage>>? _store;
    private Lazy<IReadOnlyDictionary<string, string>>? _appPaths;

    public InstalledSoftwareSnapshotCache() : this(ReadUninstall, ReadStore, ReadAppPaths) { }

    internal InstalledSoftwareSnapshotCache(
        Func<IReadOnlyList<InstalledSoftwareEntry>> readUninstall,
        Func<IReadOnlyList<InstalledStorePackage>> readStore,
        Func<IReadOnlyDictionary<string, string>> readAppPaths)
    {
        _readUninstall = readUninstall;
        _readStore = readStore;
        _readAppPaths = readAppPaths;
    }

    public IReadOnlyList<InstalledSoftwareEntry> GetUninstallEntries()
        => GetSnapshot(ref _uninstall, () => Array.AsReadOnly(_readUninstall().ToArray()));

    public IReadOnlyList<InstalledStorePackage> GetStorePackages()
        => GetSnapshot(ref _store, () => Array.AsReadOnly(_readStore().ToArray()));

    public IReadOnlyDictionary<string, string> GetAppPaths()
        => GetSnapshot(ref _appPaths, () =>
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(_readAppPaths(), StringComparer.OrdinalIgnoreCase)));

    private T GetSnapshot<T>(ref Lazy<T>? slot, Func<T> read)
    {
        Lazy<T> snapshot;
        lock (_gate) snapshot = slot ??= new Lazy<T>(read, LazyThreadSafetyMode.ExecutionAndPublication);
        // 注册表/文件读取在门锁外，UI 的失效操作不等待正在进行的枚举。
        try { return snapshot.Value; }
        catch
        {
            lock (_gate)
                if (ReferenceEquals(slot, snapshot)) slot = null;
            throw;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _uninstall = null;
            _store = null;
            _appPaths = null;
        }
    }

    private static IReadOnlyList<InstalledSoftwareEntry> ReadUninstall()
    {
        var entries = new List<InstalledSoftwareEntry>();
        foreach (var (hive, root) in new[]
        {
            (HiveKind.LocalMachine, $@"SOFTWARE\{UninstallRoot}"),
            (HiveKind.LocalMachine, $@"SOFTWARE\WOW6432Node\{UninstallRoot}"),
            (HiveKind.CurrentUser, $@"Software\{UninstallRoot}")
        })
        {
            using var parent = Reg.Open(RegPath.Of(hive, root), false);
            if (parent is null) continue;
            foreach (var name in SubKeyNames(parent))
            {
                try
                {
                    using var key = parent.OpenSubKey(name, false);
                    if (key is null) continue;
                    var displayName = Text(key, "DisplayName");
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    var sizeKb = Integer(key, "EstimatedSize");
                    entries.Add(new InstalledSoftwareEntry(
                        hive, $@"{root}\{name}", displayName, Text(key, "DisplayVersion"),
                        Text(key, "Publisher"), Text(key, "InstallLocation").Trim('"'),
                        Text(key, "UninstallString"), Text(key, "DisplayIcon"),
                        Text(key, "ParentKeyName"), Integer(key, "SystemComponent") == 1,
                        sizeKb > 0 && sizeKb <= long.MaxValue / 1024 ? sizeKb * 1024 : 0,
                        Text(key, "InstallDate")));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
                {
                    // 安装/卸载正在修改登记或单个键不可读时，只跳过该记录。
                }
            }
        }
        return entries;
    }

    private static IReadOnlyList<InstalledStorePackage> ReadStore()
    {
        var entries = new List<InstalledStorePackage>();
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            using var parent = Reg.Open(RegPath.Of(hive, AppModelRoot), false);
            if (parent is null) continue;
            foreach (var package in SubKeyNames(parent))
            {
                var display = "";
                var folder = "";
                try
                {
                    using var key = parent.OpenSubKey(package, false);
                    if (key is not null)
                    {
                        display = Text(key, "DisplayName");
                        folder = Text(key, "PackageRootFolder");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
                // 包名/版本已由父键枚举得到；属性不可读也保留，运行库检测不因此漏报。
                entries.Add(new InstalledStorePackage(hive, $@"{AppModelRoot}\{package}", package, display, folder));
            }
        }
        return entries;
    }

    private static IReadOnlyDictionary<string, string> ReadAppPaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            using var parent = Reg.Open(RegPath.Of(hive, AppPathsRoot), false);
            if (parent is null) continue;
            foreach (var name in SubKeyNames(parent))
            {
                try
                {
                    using var key = parent.OpenSubKey(name, false);
                    if (key is null) continue;
                    var value = Text(key, "").Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        paths[name] = Environment.ExpandEnvironmentVariables(value);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
            }
        }
        return paths;
    }

    private static string[] SubKeyNames(RegistryKey parent)
    {
        try { return parent.GetSubKeyNames(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Text(RegistryKey key, string name)
        => Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static long Integer(RegistryKey key, string name)
        => key.GetValue(name) switch
        {
            int value => value,
            long value => value,
            string value when long.TryParse(value, out var parsed) => parsed,
            _ => 0
        };
}
