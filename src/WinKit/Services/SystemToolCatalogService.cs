using System.Diagnostics;
using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public sealed class SystemToolItem : ObservableObject
{
    private bool _isSelected;
    private bool _isInstalled;
    private string _installedVersion = "";
    private string _executablePath = "";
    private string _installLocation = "";

    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public string WingetId { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] MatchNames { get; init; } = Array.Empty<string>();
    public string[] ExeCandidates { get; init; } = Array.Empty<string>();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    public Action? SelectionChanged { get; set; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (Set(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(VersionDisplay));
                OnPropertyChanged(nameof(ActionButtonText));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(HasLocation));
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

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (Set(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(HasLocation));
            }
        }
    }

    public string InstallLocation
    {
        get => _installLocation;
        set
        {
            if (Set(ref _installLocation, value))
                OnPropertyChanged(nameof(HasLocation));
        }
    }

    public string StatusText => IsInstalled ? "已安装" : "未安装";

    public string VersionDisplay => IsInstalled
        ? string.IsNullOrWhiteSpace(InstalledVersion) ? "已安装" : InstalledVersion
        : "—";

    public string ActionButtonText => IsInstalled ? "更新/重装" : "安装";

    public bool CanLaunch => IsInstalled && !string.IsNullOrWhiteSpace(ExecutablePath);

    public bool HasLocation => !string.IsNullOrWhiteSpace(InstallLocation) && Directory.Exists(InstallLocation);

    public string SearchBlob => $"{Category} {Name} {WingetId} {Description}".ToLowerInvariant();
}

public sealed class SystemToolCatalogService
{
    private const string UninstallRoot = @"Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string AppModelPackages =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private sealed record ToolSpec(
        string Id,
        string Category,
        string Name,
        string WingetId,
        string Description,
        string[] MatchNames,
        string[] ExeCandidates);

    private static readonly ToolSpec[] Catalog =
    {
        // 1. 微软官方系统增强
        new("powertoys", "微软官方增强", "Microsoft PowerToys", "Microsoft.PowerToys",
            "微软官方系统增强瑞士军刀：窗口分屏布局 (FancyZones)、文件占用解锁 (File Locksmith)、快捷键指南、取色器与批量重命名",
            new[] { "PowerToys" },
            new[] { "PowerToys.exe", @"%LOCALAPPDATA%\PowerToys\PowerToys.exe", @"%ProgramFiles%\PowerToys\PowerToys.exe" }),

        new("autoruns", "微软官方增强", "Sysinternals Autoruns", "Microsoft.Sysinternals.Autoruns",
            "微软官方最权威的自启动项深度分析工具，涵盖驱动、服务、计划任务、Winlogon、资源管理器扩展等全维度",
            new[] { "Autoruns" },
            new[] { "Autoruns64.exe", "Autoruns.exe" }),

        new("procexp", "微软官方增强", "Sysinternals Process Explorer", "Microsoft.Sysinternals.ProcessExplorer",
            "微软官方进程与句柄深度监视器，可查进程加载的 DLL、文件句柄占用与线程堆栈",
            new[] { "Process Explorer" },
            new[] { "procexp64.exe", "procexp.exe" }),

        new("sysinternals", "微软官方增强", "Microsoft Sysinternals Suite", "Microsoft.Sysinternals.Suite",
            "微软官方全套系统底层诊断工具集（含 TCPView、ProcMon、RAMMap、VMMap、PsTools 等）",
            new[] { "Sysinternals Suite" },
            new[] { "procmon64.exe", "tcpview64.exe", "rammap64.exe" }),

        new("pcmanager", "微软官方增强", "微软电脑管家 (PC Manager)", "Microsoft.PCManager",
            "微软官方出品的轻量纯净系统维护工具，无广告弹窗",
            new[] { "Microsoft PC Manager", "微软电脑管家", "Microsoft.MicrosoftPCManager" },
            new[] { @"%ProgramFiles%\Microsoft PC Manager\MSPCManager.exe" }),

        // 2. 文件搜索、空间与解压
        new("everything", "文件与空间工具", "Everything 文件极速搜索", "voidtools.Everything",
            "基于 NTFS USN 日志的毫秒级全盘文件名搜索工具，轻量无广告",
            new[] { "Everything" },
            new[] { "Everything.exe", @"%ProgramFiles%\Everything\Everything.exe", @"%ProgramFiles(x86)%\Everything\Everything.exe" }),

        new("wiztree", "文件与空间工具", "WizTree 磁盘空间分析", "AntibodySoftware.WizTree",
            "直接读取 NTFS 主文件表 (MFT) 的极速磁盘空间占用可视化分析工具，秒级定位全盘大文件夹",
            new[] { "WizTree" },
            new[] { "WizTree64.exe", "WizTree.exe", @"%ProgramFiles%\WizTree\WizTree64.exe" }),

        new("7zip", "文件与空间工具", "7-Zip 开源解压缩", "7zip.7zip",
            "经典开源免费的高压缩比文件归档解压工具，零广告零后台",
            new[] { "7-Zip" },
            new[] { "7zFM.exe", @"%ProgramFiles%\7-Zip\7zFM.exe" }),

        new("nanazip", "文件与空间工具", "NanaZip (原生适配 Win11 菜单)", "M2Team.NanaZip",
            "基于 7-Zip 派生的现代开源解压工具，原生支持 Windows 11 一级右键菜单与深色模式",
            new[] { "NanaZip", "40174MouriNaruto.NanaZip" },
            new[] { "NanaZip.Universal.Windows.exe", "NanaZipG.exe" }),

        // 3. 卸载与系统维护
        new("bcu", "卸载与系统维护", "Bulk Crap Uninstaller (BCUninstaller)", "Klocman.BulkCrapUninstaller",
            "开源免费的深度批量软件卸载与孤立残留检测工具，支持静默批量卸载",
            new[] { "Bulk Crap Uninstaller", "BCUninstaller" },
            new[] { "BCUninstaller.exe", @"%ProgramFiles%\BCUninstaller\BCUninstaller.exe" }),

        new("geek", "卸载与系统维护", "Geek Uninstaller", "GeekUninstaller.GeekUninstaller",
            "单文件极简软件强制卸载与注册表/文件残留清除工具",
            new[] { "Geek Uninstaller" },
            new[] { "geek.exe" }),

        new("rufus", "卸载与系统维护", "Rufus 启动盘制作工具", "Rufus.Rufus",
            "轻量快速的开源 USB 系统安装启动盘制作工具（支持跳过 Win11 TPM/微软账号限制）",
            new[] { "Rufus" },
            new[] { "rufus.exe" }),

        new("ventoy", "卸载与系统维护", "Ventoy 多系统启动盘", "Ventoy.Ventoy",
            "新一代多系统引导 U 盘工具，无需反复格式化，直接拷贝 ISO/WIM 镜像即可启动安装",
            new[] { "Ventoy" },
            new[] { "Ventoy2Disk.exe" }),

        // 4. 硬件监控与性能检测
        new("cpuz", "硬件监控与检测", "CPU-Z 处理器与内存检测", "CPUID.CPU-Z",
            "权威的 CPU、主板芯片组、内存频率与时序参数识别工具",
            new[] { "CPU-Z" },
            new[] { "cpuz.exe", @"%ProgramFiles%\CPUID\CPU-Z\cpuz.exe" }),

        new("gpuz", "硬件监控与检测", "GPU-Z 显卡参数与传感器检测", "TechPowerUp.GPU-Z",
            "权威的独立显卡与核显规格、显存、功耗及温度传感器实时监测工具",
            new[] { "GPU-Z", "TechPowerUp GPU-Z" },
            new[] { "GPU-Z.exe", @"%ProgramFiles(x86)%\GPU-Z\GPU-Z.exe" }),

        new("crystaldiskinfo", "硬件监控与检测", "CrystalDiskInfo 硬盘健康检测", "CrystalDewWorld.CrystalDiskInfo",
            "固态/机械硬盘 SMART 健康度、通电时间、擦写寿命与温度监测工具",
            new[] { "CrystalDiskInfo" },
            new[] { "DiskInfo64.exe", @"%ProgramFiles%\CrystalDiskInfo\DiskInfo64.exe" }),

        new("hwinfo", "硬件监控与检测", "HWiNFO 全硬件传感器监控", "REALiX.HWiNFO",
            "专业级全硬件深度信息识别与主板/CPU/GPU 全传感器功耗温度实时监控",
            new[] { "HWiNFO" },
            new[] { "HWiNFO64.exe", @"%ProgramFiles%\HWiNFO64\HWiNFO64.exe" }),

        new("trafficmonitor", "硬件监控与检测", "TrafficMonitor 任务栏监控", "zhongyang219.TrafficMonitor",
            "开源轻量的任务栏网速、CPU 利用率、内存占用与温度实时显示工具",
            new[] { "TrafficMonitor" },
            new[] { "TrafficMonitor.exe" }),

        // 5. 开发与终端增强
        new("winterminal", "开发与终端增强", "Windows Terminal 现代终端", "Microsoft.WindowsTerminal",
            "微软官方现代多标签页命令行终端（集中管理 PowerShell、CMD、WSL 与 SSH）",
            new[] { "Windows Terminal", "Microsoft.WindowsTerminal" },
            new[] { "wt.exe", @"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe" }),

        new("git", "开发与终端增强", "Git for Windows", "Git.Git",
            "分布式版本控制系统与 Git Bash 环境",
            new[] { "Git" },
            new[] { "git.exe", @"%ProgramFiles%\Git\git-bash.exe", @"%ProgramFiles%\Git\cmd\git.exe" }),

        new("vscode", "开发与终端增强", "Visual Studio Code", "Microsoft.VisualStudioCode",
            "微软主流跨平台轻量代码编辑器",
            new[] { "Microsoft Visual Studio Code", "Visual Studio Code" },
            new[] { "Code.exe", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe", @"%ProgramFiles%\Microsoft VS Code\Code.exe" }),

        new("notepad3", "开发与终端增强", "Notepad3 轻量文本编辑器", "Rizonesoft.Notepad3",
            "基于 Scintilla 的开源极速语法高亮文本编辑器，适合替代系统记事本查看配置与日志",
            new[] { "Notepad3" },
            new[] { "Notepad3.exe", @"%ProgramFiles%\Notepad3\Notepad3.exe" })
    };

    public List<SystemToolItem> LoadCatalog()
    {
        var uninstallEntries = LoadUninstallSnapshot();
        var storeEntries = LoadStorePackagesSnapshot();
        var appPaths = LoadAppPathsSnapshot();
        var wingetLinksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links");
        var wingetPackagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        var result = new List<SystemToolItem>(Catalog.Length);

        foreach (var spec in Catalog)
        {
            var (installed, version, exePath, installDir) = DetectTool(
                spec, uninstallEntries, storeEntries, appPaths, wingetLinksDir, wingetPackagesDir);

            result.Add(new SystemToolItem
            {
                Id = spec.Id,
                Category = spec.Category,
                Name = spec.Name,
                WingetId = spec.WingetId,
                Description = spec.Description,
                MatchNames = spec.MatchNames,
                ExeCandidates = spec.ExeCandidates,
                IsInstalled = installed,
                InstalledVersion = version,
                ExecutablePath = exePath,
                InstallLocation = installDir
            });
        }

        return result;
    }

    private static (bool Installed, string Version, string ExePath, string InstallDir) DetectTool(
        ToolSpec spec,
        List<(string Name, string Version, string Location, string IconExe)> uninstallEntries,
        List<(string Name, string Version, string Folder)> storeEntries,
        Dictionary<string, string> appPaths,
        string wingetLinksDir,
        string wingetPackagesDir)
    {
        // 1. 先检查注册表 Uninstall
        foreach (var entry in uninstallEntries)
        {
            if (MatchesAny(entry.Name, spec.MatchNames))
            {
                var dir = entry.Location;
                var exe = ResolveExeFromCandidates(spec.ExeCandidates, dir, appPaths, wingetLinksDir);
                if (string.IsNullOrWhiteSpace(exe) && !string.IsNullOrWhiteSpace(entry.IconExe) && File.Exists(entry.IconExe))
                    exe = entry.IconExe;
                if (string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(exe))
                    dir = Path.GetDirectoryName(exe) ?? "";

                return (true, entry.Version, exe, dir);
            }
        }

        // 2. 检查商店包 / MSIX
        foreach (var pkg in storeEntries)
        {
            if (MatchesAny(pkg.Name, spec.MatchNames))
            {
                var exe = ResolveExeFromCandidates(spec.ExeCandidates, pkg.Folder, appPaths, wingetLinksDir);
                if (string.IsNullOrWhiteSpace(exe) && spec.Id == "nanazip")
                    exe = FindInWindowsApps("NanaZip.exe") ?? pkg.Folder;
                if (string.IsNullOrWhiteSpace(exe) && spec.Id == "pcmanager")
                    exe = "ms-pcmanager:";
                return (true, pkg.Version, exe, pkg.Folder);
            }
        }

        // 3. 检查 WinGet Packages 目录（便携版包如 Geek / Sysinternals / Rufus / Ventoy）
        if (Directory.Exists(wingetPackagesDir))
        {
            try
            {
                var pkgDir = Directory.GetDirectories(wingetPackagesDir, $"{spec.WingetId}_*").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(pkgDir))
                {
                    var exe = FindExeInDirectory(pkgDir, spec.ExeCandidates);
                    return (true, "WinGet 便携包", exe, pkgDir);
                }
            }
            catch (Exception) { }
        }

        // 4. 检查候选路径、App Paths 与环境变量 PATH
        var foundExe = ResolveExeFromCandidates(spec.ExeCandidates, "", appPaths, wingetLinksDir);
        if (!string.IsNullOrWhiteSpace(foundExe) && File.Exists(foundExe))
        {
            var ver = "";
            try
            {
                ver = FileVersionInfo.GetVersionInfo(foundExe).ProductVersion?.Split('+')[0] ?? "";
            }
            catch (Exception) { }

            var dir = Path.GetDirectoryName(foundExe) ?? "";
            return (true, ver, foundExe, dir);
        }

        return (false, "", "", "");
    }

    private static bool MatchesAny(string text, string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var p in patterns)
        {
            if (p.Equals("Git", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Equals("Git", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("Git version ", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            if (text.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ResolveExeFromCandidates(
        string[] candidates,
        string installDir,
        Dictionary<string, string> appPaths,
        string wingetLinksDir)
    {
        foreach (var raw in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (Path.IsPathRooted(expanded))
            {
                if (File.Exists(expanded)) return expanded;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(installDir))
            {
                var inInstall = Path.Combine(installDir, expanded);
                if (File.Exists(inInstall)) return inInstall;
            }

            if (appPaths.TryGetValue(expanded, out var mapped) && File.Exists(mapped))
                return mapped;

            if (Directory.Exists(wingetLinksDir))
            {
                var inLinks = Path.Combine(wingetLinksDir, expanded);
                if (File.Exists(inLinks)) return inLinks;
            }

            var inPath = FindOnPath(expanded);
            if (!string.IsNullOrWhiteSpace(inPath)) return inPath!;
        }

        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            return FindExeInDirectory(installDir, candidates);
        }

        return "";
    }

    private static string FindExeInDirectory(string dir, string[] candidates)
    {
        try
        {
            foreach (var c in candidates)
            {
                var fileName = Path.GetFileName(Environment.ExpandEnvironmentVariables(c));
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                var match = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
        }
        catch (Exception) { }
        return "";
    }

    private static string? FindInWindowsApps(string exeName)
    {
        var winApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", exeName);
        return File.Exists(winApps) ? winApps : null;
    }

    private static string? FindOnPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(part.Trim().Trim('"'), exeName);
                if (File.Exists(full)) return full;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static List<(string Name, string Version, string Location, string IconExe)> LoadUninstallSnapshot()
    {
        var list = new List<(string, string, string, string)>();
        foreach (var (hive, root) in new[]
                 {
                     (HiveKind.LocalMachine, $@"SOFTWARE\{UninstallRoot}"),
                     (HiveKind.LocalMachine, $@"SOFTWARE\WOW6432Node\{UninstallRoot}"),
                     (HiveKind.CurrentUser, $@"Software\{UninstallRoot}")
                 })
        {
            foreach (var key in Reg.SubKeyNames(RegPath.Of(hive, root)))
            {
                var p = RegPath.Of(hive, $@"{root}\{key}");
                var name = Reg.GetString(p, "DisplayName")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var ver = (Reg.GetString(p, "DisplayVersion") ?? "").Trim();
                var loc = (Reg.GetString(p, "InstallLocation") ?? "").Trim().Trim('"');
                var iconRaw = (Reg.GetString(p, "DisplayIcon") ?? "").Trim();
                var iconExe = ContextMenuService.ExtractExecutable(iconRaw) ?? "";
                if (!iconExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) iconExe = "";

                list.Add((name!, ver, loc, iconExe));
            }
        }
        return list;
    }

    private static List<(string Name, string Version, string Folder)> LoadStorePackagesSnapshot()
    {
        var list = new List<(string, string, string)>();
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var root = RegPath.Of(hive, AppModelPackages);
            foreach (var pkg in Reg.SubKeyNames(root))
            {
                var parts = pkg.Split('_');
                var family = parts.Length > 0 ? parts[0] : pkg;
                var ver = parts.Length > 1 ? parts[1] : "";
                var p = RegPath.Of(hive, $@"{AppModelPackages}\{pkg}");
                var folder = (Reg.GetString(p, "PackageRootFolder") ?? "").Trim();
                list.Add((family, ver, folder));
            }
        }
        return list;
    }

    private static Dictionary<string, string> LoadAppPathsSnapshot()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var root = RegPath.Of(hive, AppPathsRoot);
            foreach (var sub in Reg.SubKeyNames(root))
            {
                var val = Reg.GetString(RegPath.Of(hive, $@"{AppPathsRoot}\{sub}"))?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(val))
                    map[sub] = Environment.ExpandEnvironmentVariables(val!);
            }
        }
        return map;
    }

    public async Task<(bool Success, string Message)> InstallOrUpdateToolAsync(
        SystemToolItem tool,
        Action<string>? progress = null)
    {
        var verb = tool.IsInstalled ? "upgrade" : "install";
        progress?.Invoke($"正在通过 winget {(tool.IsInstalled ? "更新/重装" : "安装")}「{tool.Name}」({tool.WingetId})…");

        var args = $"{verb} --id \"{tool.WingetId}\" --exact --accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (code, output, err) = await AppManagerService.RunProcessAsync("winget", args, TimeSpan.FromMinutes(10));

        if (code == 0
            || output.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("找不到可用的升级", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{tool.Name}」已安装/更新就绪");
        }

        // 若 upgrade 提示未受 winget 跟踪，自动回退 install --force
        var fallbackArgs = $"install --id \"{tool.WingetId}\" --exact --accept-package-agreements --accept-source-agreements";
        var (code2, output2, err2) = await AppManagerService.RunProcessAsync("winget", fallbackArgs, TimeSpan.FromMinutes(10));
        if (code2 == 0
            || output2.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
            || output2.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{tool.Name}」已安装就绪");
        }

        var combined = $"{output2}\n{err2}\n{output}\n{err}";
        var lastLine = combined
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 2 && !l.All(c => c is '-' or '\\' or '|' or '/'))
            ?? "请检查网络或 winget 源状态";

        return (false, $"操作未完成：{lastLine}");
    }

    public (bool Success, string Message) LaunchTool(SystemToolItem tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ExecutablePath))
            return (false, "未定位到该工具的启动程序路径");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = tool.ExecutablePath,
                UseShellExecute = true
            });
            return (true, $"已启动：{tool.Name}");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }
}
