using System.Diagnostics;
using System.IO;
using System.Text;
using WinKit.Core;

namespace WinKit.Services;

public enum AppSourceKind
{
    Desktop,
    Store
}

public sealed class InstalledAppItem : ObservableObject
{
    private string _version = "";
    private string _latestVersion = "";
    private string _wingetId = "";

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public AppSourceKind Kind { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string PackageFullName { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public HiveKind Hive { get; init; }
    public string SubKey { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SizeText { get; init; } = "—";
    public string InstallDate { get; init; } = "";

    public string InstallDateFormatted =>
        InstallDate.Length == 8 && InstallDate.All(char.IsDigit)
            ? $"{InstallDate[..4]}-{InstallDate[4..6]}-{InstallDate[6..8]}"
            : string.IsNullOrWhiteSpace(InstallDate) ? "—" : InstallDate;

    public string KindText => Kind == AppSourceKind.Desktop ? "桌面软件" : "商店/打包";

    public string Version
    {
        get => _version;
        set
        {
            if (Set(ref _version, value)) OnPropertyChanged(nameof(VersionText));
        }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (Set(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(HasUpdate));
                OnPropertyChanged(nameof(VersionText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string WingetId
    {
        get => _wingetId;
        set => Set(ref _wingetId, value);
    }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(_latestVersion);

    public bool HasLocation => !string.IsNullOrWhiteSpace(InstallLocation) && Directory.Exists(InstallLocation);

    public string VersionText => HasUpdate
        ? $"{(string.IsNullOrWhiteSpace(_version) ? "未知" : _version)} → {_latestVersion}"
        : string.IsNullOrWhiteSpace(_version) ? "—" : _version;

    public string StatusText => HasUpdate ? "可更新" : "已安装";

    public string CopyablePath => !string.IsNullOrWhiteSpace(InstallLocation)
        ? InstallLocation
        : !string.IsNullOrWhiteSpace(RegistryPath)
            ? RegistryPath
            : PackageFullName;

    public string SearchBlob =>
        $"{Name} {Publisher} {Version} {InstallLocation} {PackageFullName} {WingetId}".ToLowerInvariant();
}

public sealed class WingetUpgradeEntry
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string AvailableVersion { get; init; } = "";
}

public sealed class AppResidualReport
{
    public string AppName { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public bool FolderStillExists { get; init; }
    public List<(HiveKind Hive, string SubKey, string Description)> RegistryResiduals { get; init; } = new();

    public bool HasAny => FolderStillExists || RegistryResiduals.Count > 0;
}

public sealed class AppManagerService
{
    private const string UninstallRoot = @"Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppModelPackages =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly string[] InfraMarkers =
    {
        "WinAppRuntime", "DDLM", "VCLibs", "UI.Xaml", "NET.Native",
        "WindowsAppRuntime", "WinUI", "DesktopAppRuntime", "DirectXRuntime",
        "Advertising.Xaml", "Services.Store.Engagement", "StorePurchaseApp"
    };

    private static readonly string[] SystemPackagePrefixes =
    {
        "MicrosoftWindows.", "Microsoft.Windows.", "Microsoft.UI.",
        "Microsoft.NET.", "Microsoft.VCLibs", "Windows.CBSPreview",
        "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.AsyncTextService",
        "Microsoft.BioEnrollment", "Microsoft.CredDialogHost", "Microsoft.ECApp",
        "Microsoft.LockApp", "Microsoft.Win32WebViewHost", "Microsoft.XboxGameCallableUI"
    };

    private readonly ChangeJournal _journal;

    public AppManagerService(ChangeJournal journal) => _journal = journal;

    public List<InstalledAppItem> LoadAll()
    {
        var list = new List<InstalledAppItem>();
        var seenDesktop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, root) in new[]
                 {
                     (HiveKind.LocalMachine, $@"SOFTWARE\{UninstallRoot}"),
                     (HiveKind.LocalMachine, $@"SOFTWARE\WOW6432Node\{UninstallRoot}"),
                     (HiveKind.CurrentUser, $@"Software\{UninstallRoot}")
                 })
        {
            foreach (var key in Reg.SubKeyNames(RegPath.Of(hive, root)))
            {
                var subKey = $@"{root}\{key}";
                var path = RegPath.Of(hive, subKey);
                var name = Reg.GetString(path, "DisplayName")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var uninstall = (Reg.GetString(path, "UninstallString") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(uninstall)) continue;

                if ((Reg.GetInt(path, "SystemComponent") ?? 0) == 1) continue;
                if (!string.IsNullOrWhiteSpace(Reg.GetString(path, "ParentKeyName"))) continue;

                var version = (Reg.GetString(path, "DisplayVersion") ?? "").Trim();
                var dedupKey = $"{name}|{version}";
                if (!seenDesktop.Add(dedupKey)) continue;

                var publisher = (Reg.GetString(path, "Publisher") ?? "未知").Trim();
                var location = (Reg.GetString(path, "InstallLocation") ?? "").Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(location))
                {
                    var exe = ContextMenuService.ExtractExecutable(uninstall);
                    if (!string.IsNullOrWhiteSpace(exe) && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        try { location = Path.GetDirectoryName(exe) ?? ""; }
                        catch (Exception) { location = ""; }
                    }
                }

                var sizeKb = Reg.GetInt(path, "EstimatedSize") ?? 0;
                var sizeBytes = sizeKb > 0 ? (long)sizeKb * 1024L : 0L;

                list.Add(new InstalledAppItem
                {
                    Id = $"desktop:{hive}:{subKey}",
                    Name = name!,
                    Publisher = string.IsNullOrWhiteSpace(publisher) ? "未知" : publisher,
                    Version = version,
                    Kind = AppSourceKind.Desktop,
                    InstallLocation = location,
                    UninstallString = uninstall,
                    RegistryPath = path.FullPath,
                    Hive = hive,
                    SubKey = subKey,
                    SizeBytes = sizeBytes,
                    SizeText = sizeBytes > 0 ? ThisPcService.FormatSize(sizeBytes) : "—",
                    InstallDate = (Reg.GetString(path, "InstallDate") ?? "").Trim()
                });
            }
        }

        // 读取 Store / MSIX 打包应用
        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
        {
            var root = RegPath.Of(hive, AppModelPackages);
            foreach (var pkg in Reg.SubKeyNames(root))
            {
                if (IsSystemOrInfraPackage(pkg)) continue;

                var parts = pkg.Split('_');
                var familyPrefix = parts.Length > 0 ? parts[0] : pkg;
                var version = parts.Length > 1 ? parts[1] : "";

                // 如果同名桌面应用已存在，避免重复列出稀疏包
                var pkgPath = RegPath.Of(hive, $@"{AppModelPackages}\{pkg}");
                var rawDisplay = Reg.GetString(pkgPath, "DisplayName");
                var display = Native.ResolveIndirectString(rawDisplay);
                if (string.IsNullOrWhiteSpace(display))
                {
                    if (!string.IsNullOrWhiteSpace(rawDisplay)
                        && !rawDisplay!.StartsWith('@')
                        && !rawDisplay.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    {
                        display = rawDisplay.Trim();
                    }
                    else
                    {
                        display = HumanizePackageName(familyPrefix);
                    }
                }

                if (string.IsNullOrWhiteSpace(display)) continue;
                if (!seenFamilies.Add(display!)) continue;
                if (list.Any(d => d.Name.Equals(display, StringComparison.OrdinalIgnoreCase))) continue;

                var rootFolder = (Reg.GetString(pkgPath, "PackageRootFolder") ?? "").Trim();
                var publisher = GuessStorePublisher(familyPrefix);

                list.Add(new InstalledAppItem
                {
                    Id = $"store:{pkg}",
                    Name = display!,
                    Publisher = publisher,
                    Version = version,
                    Kind = AppSourceKind.Store,
                    InstallLocation = rootFolder,
                    PackageFullName = pkg,
                    RegistryPath = pkgPath.FullPath,
                    Hive = hive,
                    SubKey = $@"{AppModelPackages}\{pkg}",
                    SizeText = "打包应用"
                });
            }
        }

        return list
            .OrderBy(a => a.Kind == AppSourceKind.Desktop ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsSystemOrInfraPackage(string package)
    {
        foreach (var marker in InfraMarkers)
            if (package.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in SystemPackagePrefixes)
            if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string HumanizePackageName(string familyName)
    {
        var segs = familyName.Split('.');
        var meaningful = segs
            .Where(s => s.Any(char.IsLetter) && !s.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return meaningful.Count > 0 ? meaningful[^1] : familyName;
    }

    private static string GuessStorePublisher(string familyName)
    {
        if (familyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) return "Microsoft";
        var first = familyName.Split('.')[0];
        return string.IsNullOrWhiteSpace(first) ? "Microsoft Store" : first;
    }

    /// <summary>
    /// 调用 winget upgrade 获取本机可升级的软件列表，并写回匹配到的 InstalledAppItem（未在本地列表中匹配到的 winget 可升级项也会自动补充）。
    /// </summary>
    public async Task<(int MatchedCount, List<WingetUpgradeEntry> RawUpgrades, string? Error)> CheckUpdatesAsync(
        List<InstalledAppItem> apps)
    {
        var (exitCode, output, error) = await RunProcessAsync(
            "winget",
            "upgrade --include-unknown --accept-source-agreements --disable-interactivity",
            TimeSpan.FromSeconds(60));

        if (exitCode == -1 && !string.IsNullOrWhiteSpace(error))
            return (0, new List<WingetUpgradeEntry>(), error);

        var upgrades = ParseWingetUpgradeTable(output);
        apps.RemoveAll(a => a.Id.StartsWith("winget:", StringComparison.OrdinalIgnoreCase));
        foreach (var app in apps)
        {
            app.LatestVersion = "";
            app.WingetId = "";
        }

        var matched = 0;
        foreach (var up in upgrades)
        {
            var target = MatchApp(apps, up);
            if (target is not null)
            {
                target.WingetId = up.Id;
                target.LatestVersion = up.AvailableVersion;
                matched++;
            }
            else
            {
                var pub = up.Id.Contains('.') ? up.Id.Split('.')[0] : "winget";
                apps.Add(new InstalledAppItem
                {
                    Id = $"winget:{up.Id}",
                    Name = up.Name,
                    Publisher = pub,
                    Version = up.CurrentVersion,
                    LatestVersion = up.AvailableVersion,
                    WingetId = up.Id,
                    Kind = AppSourceKind.Desktop,
                    InstallLocation = $"winget 包 ({up.Id})",
                    SizeText = "—"
                });
                matched++;
            }
        }

        return (matched, upgrades, null);
    }

    private static InstalledAppItem? MatchApp(IReadOnlyList<InstalledAppItem> apps, WingetUpgradeEntry up)
    {
        // 1. 名称精确匹配
        var exact = apps.FirstOrDefault(a => a.Name.Equals(up.Name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // 2. 包名或名称包含匹配
        var byId = apps.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.PackageFullName)
            && a.PackageFullName.StartsWith(up.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        return apps.FirstOrDefault(a =>
            (a.Name.Length >= 3 && up.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase))
            || (up.Name.Length >= 3 && a.Name.Contains(up.Name, StringComparison.OrdinalIgnoreCase)));
    }

    public static List<WingetUpgradeEntry> ParseWingetUpgradeTable(string rawOutput)
    {
        var result = new List<WingetUpgradeEntry>();
        if (string.IsNullOrWhiteSpace(rawOutput)) return result;

        var lines = rawOutput
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(l => l.TrimEnd())
            .ToList();

        var sepIndex = lines.FindIndex(l => l.StartsWith("---", StringComparison.Ordinal) && l.Length >= 20);
        if (sepIndex <= 0) return result;

        for (var i = sepIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.EndsWith("upgrades available.", StringComparison.OrdinalIgnoreCase)
                || line.Contains("个可用升级")
                || line.Contains("包具有无法确定的版本号")
                || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;

            var idx = tokens.Length - 1;
            if (tokens[idx].Equals("winget", StringComparison.OrdinalIgnoreCase)
                || tokens[idx].Equals("msstore", StringComparison.OrdinalIgnoreCase))
            {
                idx--;
            }

            if (idx < 3) continue;
            var availVer = tokens[idx--];
            var currentVer = tokens[idx--];
            if (idx >= 2 && tokens[idx] == "<")
            {
                currentVer = "< " + currentVer;
                idx--;
            }

            var id = tokens[idx--];
            var name = string.Join(" ", tokens.Take(idx + 1)).Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(availVer)) continue;
            if (currentVer.Equals(availVer, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new WingetUpgradeEntry
            {
                Name = name,
                Id = id,
                CurrentVersion = currentVer,
                AvailableVersion = availVer
            });
        }

        return result;
    }

    public async Task<(bool Success, string Message)> UpgradeAppAsync(
        InstalledAppItem app,
        Action<string>? progress = null)
    {
        var selector = !string.IsNullOrWhiteSpace(app.WingetId)
            ? $"--id \"{app.WingetId}\" --exact"
            : $"--name \"{app.Name}\"";

        progress?.Invoke($"正在后台更新「{app.Name}」→ {app.LatestVersion}…");

        var args = $"upgrade {selector} --accept-package-agreements --accept-source-agreements --disable-interactivity";
        var (code, output, err) = await RunProcessAsync("winget", args, TimeSpan.FromMinutes(10));

        if (code == 0 || output.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
                      || output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{app.Name}」已更新至 {app.LatestVersion}");
        }

        // 如果静默模式因安装器要求界面交互失败，自动回退交互模式再试一次
        progress?.Invoke($"「{app.Name}」需要交互式安装，正在切换交互模式…");
        var interactiveArgs = $"upgrade {selector} --accept-package-agreements --accept-source-agreements --interactive";
        var (code2, output2, err2) = await RunProcessAsync("winget", interactiveArgs, TimeSpan.FromMinutes(10));

        if (code2 == 0 || output2.Contains("成功安装", StringComparison.OrdinalIgnoreCase)
                       || output2.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return (true, $"「{app.Name}」已更新至 {app.LatestVersion}");
        }

        var tail = ExtractLastMeaningfulLine(string.IsNullOrWhiteSpace(output2) ? output : output2, err2 ?? err);
        return (false, $"更新「{app.Name}」未完成：{tail}");
    }

    public async Task<(bool Success, string Message)> UninstallStoreAppAsync(InstalledAppItem app)
    {
        if (string.IsNullOrWhiteSpace(app.PackageFullName))
            return (false, "缺少包全名，无法卸载");

        var escaped = app.PackageFullName.Replace("'", "''");
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '{escaped}' -ErrorAction Stop\"";
        var (code, _, err) = await RunProcessAsync("powershell.exe", args, TimeSpan.FromMinutes(3));

        return code == 0
            ? (true, $"已卸载商店/打包应用：{app.Name}")
            : (false, string.IsNullOrWhiteSpace(err) ? $"卸载失败（退出码 {code}）" : err.Trim());
    }

    /// <summary>
    /// 扫描指定应用的卸载残留（失效的 Uninstall 项、指向该软件目录且文件已不存在的 CLSID、以及残余安装目录）。
    /// </summary>
    public AppResidualReport ScanResiduals(InstalledAppItem app)
    {
        var regResiduals = new List<(HiveKind Hive, string SubKey, string Description)>();

        if (app.Kind == AppSourceKind.Desktop && !string.IsNullOrWhiteSpace(app.SubKey))
        {
            var regPath = RegPath.Of(app.Hive, app.SubKey);
            if (Reg.Exists(regPath))
            {
                var exe = ContextMenuService.ExtractExecutable(app.UninstallString);
                var missing = !string.IsNullOrWhiteSpace(exe)
                              && !exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)
                              && !File.Exists(Environment.ExpandEnvironmentVariables(exe));
                if (missing)
                {
                    regResiduals.Add((app.Hive, app.SubKey, $"失效的卸载注册项：{regPath.FullPath}"));
                }
            }
        }

        var folderExists = false;
        var normLoc = NormalizeLocation(app.InstallLocation);
        if (normLoc.Length > 3)
        {
            try
            {
                if (Directory.Exists(app.InstallLocation))
                    folderExists = Directory.EnumerateFileSystemEntries(app.InstallLocation).Any();
            }
            catch (Exception)
            {
                folderExists = false;
            }

            // 查找指向该安装目录且 DLL/EXE 已被删掉的残留 CLSID
            foreach (var hive in new[] { HiveKind.CurrentUser, HiveKind.LocalMachine })
            {
                var scanned = 0;
                foreach (var clsid in Reg.SubKeyNames(RegPath.Of(hive, @"Software\Classes\CLSID")))
                {
                    if (++scanned > 8000) break;
                    foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                    {
                        var sub = $@"Software\Classes\CLSID\{clsid}\{server}";
                        var raw = Reg.GetString(RegPath.Of(hive, sub));
                        if (string.IsNullOrWhiteSpace(raw)) continue;

                        var exe = ContextMenuService.ExtractExecutable(Environment.ExpandEnvironmentVariables(raw));
                        if (string.IsNullOrWhiteSpace(exe)) continue;
                        if (!NormalizeLocation(exe).StartsWith(normLoc, StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(exe)) continue;

                        var clsidKey = $@"Software\Classes\CLSID\{clsid}";
                        regResiduals.Add((hive, clsidKey, $"残留 COM 类 {clsid}（文件已不存在：{exe}）"));
                        break;
                    }
                }
            }
        }

        return new AppResidualReport
        {
            AppName = app.Name,
            InstallLocation = app.InstallLocation,
            FolderStillExists = folderExists,
            RegistryResiduals = regResiduals
        };
    }

    public int CleanRegistryResiduals(AppResidualReport report)
    {
        if (report.RegistryResiduals.Count == 0) return 0;

        var tracked = report.RegistryResiduals
            .Select(r => (r.Hive, r.SubKey))
            .Distinct()
            .ToArray();

        _journal.Record($"清理「{report.AppName}」卸载残留（{tracked.Length} 项）", tracked, () =>
        {
            foreach (var (hive, subKey) in tracked)
                Reg.DeleteTree(RegPath.Of(hive, subKey));
        });

        Native.NotifyAssociationsChanged();
        return tracked.Length;
    }

    private static string NormalizeLocation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private static string ExtractLastMeaningfulLine(string stdout, string? stderr)
    {
        var combined = $"{stdout}\n{stderr}";
        var line = combined
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 2 && !l.All(c => c is '-' or '\\' or '|' or '/'));
        return string.IsNullOrWhiteSpace(line) ? "请检查网络或安装包状态" : line;
    }

    /// <summary>
    /// 原生控制台工具（pnputil / sc / netsh / chcp 输出）在中文 Windows 上按 OEM 代码页输出，
    /// 用 UTF-8 解码会把中文提示变成替换字符，导致“正在使用”这类判定永远不命中。
    /// </summary>
    public static Encoding GetOemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.Default;
        }
    }

    public static async Task<(int ExitCode, string StdOut, string? StdErr)> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        Encoding? encoding = null)
    {
        // 中文 Windows 上 pnputil/sc/net 这类原生工具按 OEM 代码页输出，
        // 固定 UTF-8 会把“正在使用”“一个或多个设备”解成替换字符，导致后续判定永远不命中。
        var enc = encoding ?? Encoding.UTF8;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = enc,
                    StandardErrorEncoding = enc
                }
            };

            process.Start();
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception) { }
                return (-1, await outTask, "命令执行超时");
            }

            return (process.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
