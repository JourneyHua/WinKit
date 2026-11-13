using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class RuntimeComponentItem : ObservableObject
{
    private bool _isInstalled;
    private string _installedVersion = "";

    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Description { get; init; } = "";
    public string InstallId { get; init; } = "";
    public bool CanInstall { get; init; } = true;

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (Set(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ActionButtonText));
            }
        }
    }

    public string InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (Set(ref _installedVersion, value))
                OnPropertyChanged(nameof(VersionDisplay));
        }
    }

    public string StatusText => IsInstalled ? "已安装" : "未安装（缺失）";

    public string VersionDisplay => IsInstalled
        ? string.IsNullOrWhiteSpace(InstalledVersion) ? "已就绪" : InstalledVersion
        : "—";

    public string ActionButtonText => IsInstalled ? "修复/重装" : "安装";

    public string SearchBlob => $"{Name} {Category} {Architecture} {Description} {InstallId}".ToLowerInvariant();
}

public sealed class RuntimeCheckService
{
    private const string UninstallRoot = @"Microsoft\Windows\CurrentVersion\Uninstall";

    public List<RuntimeComponentItem> CheckAll()
    {
        var allUninstall = LoadAllUninstallEntriesIncludingSystem();
        var dotnetDesktopVersions = LoadDotNetDesktopRuntimeVersions();
        var list = new List<RuntimeComponentItem>();

        // 1. VC++ 运行库
        AddVcRedist2015Plus(list, "x64", allUninstall);
        AddVcRedist2015Plus(list, "x86", allUninstall);
        AddVcRedistLegacy(list, "2013", "12.0", "x64", "Microsoft.VCRedist.2013.x64", allUninstall);
        AddVcRedistLegacy(list, "2013", "12.0", "x86", "Microsoft.VCRedist.2013.x86", allUninstall);
        AddVcRedistLegacy(list, "2012", "11.0", "x64", "Microsoft.VCRedist.2012.x64", allUninstall);
        AddVcRedistLegacy(list, "2012", "11.0", "x86", "Microsoft.VCRedist.2012.x86", allUninstall);
        AddVcRedistLegacy(list, "2010", "10.0", "x64", "Microsoft.VCRedist.2010.x64", allUninstall);
        AddVcRedistLegacy(list, "2010", "10.0", "x86", "Microsoft.VCRedist.2010.x86", allUninstall);
        AddVcRedistLegacy(list, "2008", "9.0", "x64", "Microsoft.VCRedist.2008.x64", allUninstall);
        AddVcRedistLegacy(list, "2008", "9.0", "x86", "Microsoft.VCRedist.2008.x86", allUninstall);
        AddVcRedistLegacy(list, "2005", "8.0", "x64", "Microsoft.VCRedist.2005.x64", allUninstall);
        AddVcRedistLegacy(list, "2005", "8.0", "x86", "Microsoft.VCRedist.2005.x86", allUninstall);

        // 2. .NET 运行库
        AddDotNetDesktop(list, 10, "Microsoft.DotNet.DesktopRuntime.10", "现代 WPF/WinForms 桌面软件运行库（WinKit 自身依赖）", dotnetDesktopVersions, allUninstall);
        AddDotNetDesktop(list, 9, "Microsoft.DotNet.DesktopRuntime.9", ".NET 9 桌面应用程序运行库", dotnetDesktopVersions, allUninstall);
        AddDotNetDesktop(list, 8, "Microsoft.DotNet.DesktopRuntime.8", "主流 .NET 8 LTS 桌面应用程序运行库", dotnetDesktopVersions, allUninstall);
        AddDotNetDesktop(list, 6, "Microsoft.DotNet.DesktopRuntime.6", ".NET 6 LTS 桌面应用程序运行库", dotnetDesktopVersions, allUninstall);
        AddNetFx35(list);
        AddNetFx48(list);

        // 3. 游戏与图形组件
        AddDirectX9(list);
        AddXna40(list, allUninstall);
        AddOpenAl(list, allUninstall);

        // 4. 现代系统组件
        AddWebView2(list, allUninstall);
        AddWindowsAppRuntime(list);

        return list;
    }

    private static void AddVcRedist2015Plus(
        List<RuntimeComponentItem> list,
        string arch,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        var regKey = $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}";
        var wowKey = $@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}";

        var installed = (Reg.GetInt(RegPath.Hklm(regKey), "Installed") ?? 0) == 1
                        || (Reg.GetInt(RegPath.Hklm(wowKey), "Installed") ?? 0) == 1;
        var version = Reg.GetString(RegPath.Hklm(regKey), "Version")
                      ?? Reg.GetString(RegPath.Hklm(wowKey), "Version")
                      ?? "";
        version = version.TrimStart('v', 'V');

        if (!installed)
        {
            var match = allUninstall.FirstOrDefault(u =>
                u.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
                && (u.Name.Contains("2015-2022", StringComparison.OrdinalIgnoreCase)
                    || u.Name.Contains("2015-2019", StringComparison.OrdinalIgnoreCase)
                    || u.Name.Contains("2022", StringComparison.OrdinalIgnoreCase))
                && u.Name.Contains(arch, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Name))
            {
                installed = true;
                version = match.Version;
            }
        }

        list.Add(new RuntimeComponentItem
        {
            Key = $"vc2015+{arch}",
            Name = $"Visual C++ 2015-2022 Redistributable ({arch})",
            Category = "VC++ 运行库",
            Architecture = arch,
            Description = $"绝大多数现代 C++ 软件与 3A 游戏必备运行库（msvcp140 / vcruntime140 {arch}）",
            InstallId = $"Microsoft.VCRedist.2015+.{arch}",
            IsInstalled = installed,
            InstalledVersion = version
        });
    }

    private static void AddVcRedistLegacy(
        List<RuntimeComponentItem> list,
        string year,
        string majorVer,
        string arch,
        string wingetId,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        var archToken = arch == "x64" ? "x64" : "x86";
        var match = allUninstall.FirstOrDefault(u =>
            u.Name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            && u.Name.Contains(year, StringComparison.OrdinalIgnoreCase)
            && (arch == "x64"
                ? u.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) || u.Name.Contains("64", StringComparison.OrdinalIgnoreCase)
                : u.Name.Contains("x86", StringComparison.OrdinalIgnoreCase) || (!u.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) && !u.Name.Contains("64-bit", StringComparison.OrdinalIgnoreCase))));

        list.Add(new RuntimeComponentItem
        {
            Key = $"vc{year}-{archToken}",
            Name = $"Visual C++ {year} Redistributable ({archToken})",
            Category = "VC++ 运行库",
            Architecture = archToken,
            Description = $"老版本 C++ 软件与经典游戏依赖（VC {majorVer} {archToken}）",
            InstallId = wingetId,
            IsInstalled = !string.IsNullOrWhiteSpace(match.Name),
            InstalledVersion = match.Version ?? ""
        });
    }

    private static void AddDotNetDesktop(
        List<RuntimeComponentItem> list,
        int major,
        string wingetId,
        string desc,
        IReadOnlyList<string> folderVersions,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        var prefix = $"{major}.";
        var ver = folderVersions
            .Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";

        if (string.IsNullOrWhiteSpace(ver))
        {
            var match = allUninstall.FirstOrDefault(u =>
                (u.Name.Contains($"Windows Desktop Runtime - {major}.", StringComparison.OrdinalIgnoreCase)
                 || u.Name.Contains($".NET Desktop Runtime {major}.", StringComparison.OrdinalIgnoreCase))
                && u.Name.Contains("x64", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Name)) ver = match.Version;
        }

        list.Add(new RuntimeComponentItem
        {
            Key = $"dotnet-desktop-{major}",
            Name = $".NET {major} Desktop Runtime (x64)",
            Category = ".NET 运行库",
            Architecture = "x64",
            Description = desc,
            InstallId = wingetId,
            IsInstalled = !string.IsNullOrWhiteSpace(ver),
            InstalledVersion = ver
        });
    }

    private static void AddNetFx35(List<RuntimeComponentItem> list)
    {
        const string key = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5";
        var installed = (Reg.GetInt(RegPath.Hklm(key), "Install") ?? 0) == 1;
        var version = Reg.GetString(RegPath.Hklm(key), "Version") ?? "";

        list.Add(new RuntimeComponentItem
        {
            Key = "netfx35",
            Name = ".NET Framework 3.5 (含 2.0 / 3.0)",
            Category = ".NET 运行库",
            Architecture = "x64/x86",
            Description = "老款 Windows 桌面软件与工业/办公软件常用的基础框架（通过 DISM 启用）",
            InstallId = "DISM:NetFx3",
            IsInstalled = installed,
            InstalledVersion = version
        });
    }

    private static void AddNetFx48(List<RuntimeComponentItem> list)
    {
        const string key = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";
        var release = Reg.GetInt(RegPath.Hklm(key), "Release") ?? 0;
        var version = Reg.GetString(RegPath.Hklm(key), "Version") ?? "";

        list.Add(new RuntimeComponentItem
        {
            Key = "netfx48",
            Name = ".NET Framework 4.8 / 4.8.1",
            Category = ".NET 运行库",
            Architecture = "x64/x86",
            Description = "Windows 11 系统内置的 .NET Framework 4.x 运行环境",
            InstallId = "",
            CanInstall = false,
            IsInstalled = release >= 528040,
            InstalledVersion = version
        });
    }

    private static void AddDirectX9(List<RuntimeComponentItem> list)
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

        var has64 = File.Exists(Path.Combine(sys32, "d3dx9_43.dll"))
                    && File.Exists(Path.Combine(sys32, "xinput1_3.dll"));
        var has32 = File.Exists(Path.Combine(sysWow, "d3dx9_43.dll"))
                    && File.Exists(Path.Combine(sysWow, "xinput1_3.dll"));

        list.Add(new RuntimeComponentItem
        {
            Key = "directx9",
            Name = "DirectX 9.0c 最终用户运行库",
            Category = "图形与游戏组件",
            Architecture = "x64/x86",
            Description = "解决游戏报错缺少 d3dx9_43.dll、xinput1_3.dll、xapofx1_5.dll 等问题",
            InstallId = "Microsoft.DirectX",
            IsInstalled = has64 && has32,
            InstalledVersion = has64 && has32 ? "9.29.1974 (完整)" : has64 || has32 ? "部分缺失" : ""
        });
    }

    private static void AddXna40(
        List<RuntimeComponentItem> list,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        var match = allUninstall.FirstOrDefault(u =>
            u.Name.Contains("XNA Framework", StringComparison.OrdinalIgnoreCase));
        var regInstalled = (Reg.GetInt(RegPath.Hklm(@"SOFTWARE\WOW6432Node\Microsoft\XNA\Framework\v4.0"), "Installed") ?? 0) == 1;

        list.Add(new RuntimeComponentItem
        {
            Key = "xna40",
            Name = "Microsoft XNA Framework Redistributable 4.0",
            Category = "图形与游戏组件",
            Architecture = "x86",
            Description = "《星露谷物语》《泰拉瑞亚》等众多独立游戏依赖的微软游戏框架",
            InstallId = "Microsoft.XNARedist",
            IsInstalled = regInstalled || !string.IsNullOrWhiteSpace(match.Name),
            InstalledVersion = !string.IsNullOrWhiteSpace(match.Version) ? match.Version : regInstalled ? "4.0" : ""
        });
    }

    private static void AddOpenAl(
        List<RuntimeComponentItem> list,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sysWow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var hasDll = File.Exists(Path.Combine(sys32, "OpenAL32.dll"))
                     || File.Exists(Path.Combine(sysWow, "OpenAL32.dll"));
        var match = allUninstall.FirstOrDefault(u =>
            u.Name.Equals("OpenAL", StringComparison.OrdinalIgnoreCase)
            || u.Name.Contains("OpenAL", StringComparison.OrdinalIgnoreCase));

        list.Add(new RuntimeComponentItem
        {
            Key = "openal",
            Name = "OpenAL 跨平台 3D 音频运行库",
            Category = "图形与游戏组件",
            Architecture = "x64/x86",
            Description = "解决部分游戏与多媒体程序提示找不到 OpenAL32.dll 的问题",
            InstallId = "CreativeTechnology.OpenAL",
            IsInstalled = hasDll || !string.IsNullOrWhiteSpace(match.Name),
            InstalledVersion = !string.IsNullOrWhiteSpace(match.Version) ? match.Version : hasDll ? "已安装" : ""
        });
    }

    private static void AddWebView2(
        List<RuntimeComponentItem> list,
        IReadOnlyList<(string Name, string Version)> allUninstall)
    {
        const string clientGuid = @"{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        var ver = Reg.GetString(RegPath.Hklm($@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{clientGuid}"), "pv")
                  ?? Reg.GetString(RegPath.Hkcu($@"Software\Microsoft\EdgeUpdate\Clients\{clientGuid}"), "pv")
                  ?? "";

        if (string.IsNullOrWhiteSpace(ver) || ver == "0.0.0.0")
        {
            var match = allUninstall.FirstOrDefault(u =>
                u.Name.Contains("Edge WebView2", StringComparison.OrdinalIgnoreCase));
            ver = match.Version ?? "";
        }

        list.Add(new RuntimeComponentItem
        {
            Key = "webview2",
            Name = "Microsoft Edge WebView2 Runtime",
            Category = "现代系统组件",
            Architecture = "x64/x86",
            Description = "现代 Windows 桌面应用内嵌网页渲染组件（众多新版客户端必备）",
            InstallId = "Microsoft.EdgeWebView2Runtime",
            IsInstalled = !string.IsNullOrWhiteSpace(ver) && ver != "0.0.0.0",
            InstalledVersion = ver
        });
    }

    private static void AddWindowsAppRuntime(List<RuntimeComponentItem> list)
    {
        const string appModelRoot =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

        var versions = new List<string>();
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            foreach (var pkg in Reg.SubKeyNames(RegPath.Of(hive, appModelRoot)))
            {
                if (!pkg.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = pkg.Split('_');
                if (parts.Length > 1) versions.Add(parts[1]);
            }
        }

        var best = versions.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
        list.Add(new RuntimeComponentItem
        {
            Key = "winappruntime",
            Name = "Windows App Runtime (WinUI / WinAppSDK)",
            Category = "现代系统组件",
            Architecture = "x64",
            Description = "WinUI 3 与现代 Windows 应用 SDK 运行库",
            InstallId = "Microsoft.WindowsAppRuntime.1.6",
            IsInstalled = !string.IsNullOrWhiteSpace(best),
            InstalledVersion = best
        });
    }

    private static List<(string Name, string Version)> LoadAllUninstallEntriesIncludingSystem()
    {
        var list = new List<(string Name, string Version)>();

        foreach (var (hive, root) in new[]
                 {
                     (HiveKind.LocalMachine, $@"SOFTWARE\{UninstallRoot}"),
                     (HiveKind.LocalMachine, $@"SOFTWARE\WOW6432Node\{UninstallRoot}"),
                     (HiveKind.CurrentUser, $@"Software\{UninstallRoot}")
                 })
        {
            foreach (var key in Reg.SubKeyNames(RegPath.Of(hive, root)))
            {
                var path = RegPath.Of(hive, $@"{root}\{key}");
                var name = Reg.GetString(path, "DisplayName")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var ver = (Reg.GetString(path, "DisplayVersion") ?? "").Trim();
                list.Add((name!, ver));
            }
        }

        return list;
    }

    private static List<string> LoadDotNetDesktopRuntimeVersions()
    {
        var result = new List<string>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dotnet-sdk", "shared", "Microsoft.WindowsDesktop.App")
        };

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                        result.Add(name);
                }
            }
            catch (Exception) { }
        }

        return result;
    }

    public async Task<(bool Success, string Message)> InstallRuntimeAsync(
        RuntimeComponentItem item,
        Action<string>? progress = null)
    {
        if (!item.CanInstall || string.IsNullOrWhiteSpace(item.InstallId))
            return (false, "该组件为系统内置，无需单独安装");

        if (item.InstallId.Equals("DISM:NetFx3", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Invoke("正在通过 DISM 启用 .NET Framework 3.5（可能需要几分钟）…");
            var (code, output, err) = await AppManagerService.RunProcessAsync(
                "dism.exe",
                "/Online /Enable-Feature /FeatureName:NetFx3 /All /NoRestart",
                TimeSpan.FromMinutes(10));

            return code == 0
                ? (true, "已成功启用 .NET Framework 3.5")
                : (false, $"启用失败（错误码 {code}）：{err ?? output}");
        }

        progress?.Invoke($"正在通过 winget 安装「{item.Name}」…");
        var forceFlag = item.IsInstalled ? " --force" : "";
        var args = $"install --id \"{item.InstallId}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity{forceFlag}";
        var (exitCode, stdout, stderr) = await AppManagerService.RunProcessAsync("winget", args, TimeSpan.FromMinutes(10));

        if (exitCode == 0
            || stdout.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("已安装现有的包", StringComparison.OrdinalIgnoreCase)
            || stdout.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{item.Name}」已安装就绪");
        }

        // 回退交互模式重试
        progress?.Invoke($"「{item.Name}」安装器需要界面交互，正在以交互模式运行…");
        var interactiveArgs = $"install --id \"{item.InstallId}\" --exact --accept-package-agreements --accept-source-agreements --interactive{forceFlag}";
        var (code2, stdout2, stderr2) = await AppManagerService.RunProcessAsync("winget", interactiveArgs, TimeSpan.FromMinutes(10));

        if (code2 == 0
            || stdout2.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
            || stdout2.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{item.Name}」已安装就绪");
        }

        var detail = string.IsNullOrWhiteSpace(stderr2) ? stdout2 : stderr2;
        return (false, $"安装「{item.Name}」未完成：{detail.Trim()}");
    }
}
