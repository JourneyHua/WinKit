using System.Diagnostics;
using System.IO;
using WinKit.Core; // Shortcuts / RegPath / ChangeJournal

namespace WinKit.Services;

public sealed class UninstallEntry
{
    public string Name { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public string Version { get; init; } = "";

    public string DisplayText => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name}  {Version}";
}

/// <summary>扫描到的候选卸载器（未登记在卸载列表里的软件只能靠它）。</summary>
public sealed class CandidateUninstaller
{
    public string Name { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Company { get; init; } = "";
    public string Description { get; init; } = "";
    public string SizeText { get; init; } = "";
    public bool IsSelected { get; set; }

    public string Detail => string.IsNullOrWhiteSpace(Company) ? Description : $"{Company} · {Description}";
}

/// <summary>
/// 调起软件官方的卸载器，并在卸载后复查残留。
/// 只做“找到它、帮你点开、告诉你装在哪、事后复查”，不代替软件自己的卸载流程。
/// </summary>
public sealed class UninstallService
{
    private const string UninstallRoot = @"Microsoft\Windows\CurrentVersion\Uninstall";

    public List<UninstallEntry> LoadAll()
    {
        var list = new List<UninstallEntry>();

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
                var name = Reg.GetString(path, "DisplayName");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var uninstall = Reg.GetString(path, "UninstallString");
                if (string.IsNullOrWhiteSpace(uninstall)) continue;

                // 系统组件通常没有 DisplayIcon 且带 SystemComponent=1，不列出来
                var systemComponent = Reg.GetInt(path, "SystemComponent") ?? 0;
                if (systemComponent == 1) continue;

                list.Add(new UninstallEntry
                {
                    Name = name!.Trim(),
                    UninstallString = uninstall!.Trim(),
                    InstallLocation = (Reg.GetString(path, "InstallLocation") ?? "").Trim(),
                    RegistryPath = path.FullPath,
                    Version = Reg.GetString(path, "DisplayVersion") ?? ""
                });
            }
        }

        return list;
    }

    /// <summary>
    /// 按“安装目录前缀 → 显示名包含 → 没有任何候选”的顺序匹配；多候选交给用户选。
    /// </summary>
    public List<UninstallEntry> Match(string? installLocation, string displayName, IReadOnlyList<UninstallEntry> all)
    {
        var result = new List<UninstallEntry>();

        var location = Normalize(installLocation);
        if (location.Length > 3)
        {
            result.AddRange(all.Where(e =>
            {
                var loc = Normalize(e.InstallLocation);
                return loc.Length > 3 && (loc.StartsWith(location, StringComparison.OrdinalIgnoreCase)
                                          || location.StartsWith(loc, StringComparison.OrdinalIgnoreCase));
            }));
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(displayName))
        {
            var needle = displayName.Trim();
            result.AddRange(all.Where(e =>
                e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || needle.Contains(e.Name, StringComparison.OrdinalIgnoreCase)));
        }

        return result.Distinct().ToList();
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return p.ToLowerInvariant();
    }

    public void Launch(UninstallEntry entry)
    {
        var raw = entry.UninstallString.Trim();
        string fileName;
        string arguments;

        if (raw.StartsWith('"'))
        {
            var end = raw.IndexOf('"', 1);
            fileName = end > 1 ? raw[1..end] : raw.Trim('"');
            arguments = end > 1 ? raw[(end + 1)..].Trim() : "";
        }
        else
        {
            var space = raw.IndexOf(' ');
            fileName = space > 0 ? raw[..space] : raw;
            arguments = space > 0 ? raw[(space + 1)..].Trim() : "";
        }

        // MsiExec /I 是修复安装，卸载必须是 /X
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)
            && arguments.StartsWith("/I", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/X" + arguments[2..];
        }

        if (!File.Exists(fileName) && !fileName.Contains('\\'))
            throw new FileNotFoundException($"卸载程序不存在：{fileName}");

        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true
        });
    }

    /// <summary>这些 CLSID 是否还登记在 PackagedCom 里（卸载是否尚未完成）。</summary>
    public List<string> FindStillRegistered(IEnumerable<string> clsids)
    {
        var stillRegistered = new List<string>();
        var packages = new List<string>();

        foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
            packages.AddRange(Reg.SubKeyNames(RegPath.Of(hive, @"Software\Classes\PackagedCom\Package")));

        foreach (var clsid in clsids)
        {
            var normalized = clsid.Trim().Trim('{', '}');
            foreach (var hive in new[] { HiveKind.LocalMachine, HiveKind.CurrentUser })
            {
                foreach (var package in packages)
                {
                    var classKey = $@"Software\Classes\PackagedCom\Package\{package}\Class\{{{normalized}}}";
                    if (Reg.Exists(RegPath.Of(hive, classKey)) && !stillRegistered.Contains(clsid))
                    {
                        stillRegistered.Add(clsid);
                        break;
                    }
                }
            }
        }

        return stillRegistered;
    }

    /// <summary>
    /// 在指定文件夹里找候选卸载器。卸载器命名极不统一，实测有
    /// SUPUninstall.exe / PPUninstallerUi.exe / netbird_uninstall.exe / Uninstall Notion.exe，
    /// 所以只能按「名字带 uninst / 卸载」模糊匹配。
    ///
    /// 必须限定 .exe：uninstall.dat、Uninstall.lst 这类名字像卸载器的非可执行文件
    /// 在真实装机环境里很常见，误当成卸载器会让用户点下去毫无反应。
    /// </summary>
    public List<CandidateUninstaller> FindCandidateUninstallers(string folder, int maxDepth = 2)
    {
        var result = new List<CandidateUninstaller>();
        if (string.IsNullOrWhiteSpace(folder)) return result;

        var root = folder.Trim().Trim('"').TrimEnd('\\');
        if (!Directory.Exists(root)) return result;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = maxDepth > 1,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.exe", options);
        }
        catch (Exception)
        {
            return result;
        }

        var rootDepth = root.Count(c => c == '\\');

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!LooksLikeUninstaller(name)) continue;

            // 枚举是全递归的，深度要自己卡
            if (file.Count(c => c == '\\') - rootDepth > Math.Max(1, maxDepth)) continue;

            var info = DescribeFile(file);

            result.Add(new CandidateUninstaller
            {
                Name = name,
                FilePath = file,
                Folder = Path.GetDirectoryName(file) ?? root,
                Company = info.Company,
                Description = info.Description,
                SizeText = info.Size
            });
        }

        return result
            .OrderBy(i => i.FilePath.Count(c => c == '\\'))
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeUninstaller(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        return lower.Contains("uninst") || lower.Contains("卸载") || lower.Contains("unwise")
               || lower.Contains("remove") || lower.Contains("setup_helper");
    }

    private static (string Company, string Description, string Size) DescribeFile(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var size = ThisPcService.FormatSize(new FileInfo(path).Length);
            return ((vi.CompanyName ?? "").Trim(), (vi.FileDescription ?? "").Trim(), size);
        }
        catch (Exception)
        {
            return ("", "", "");
        }
    }

    /// <summary>把 .lnk 解析成它指向的目录；目标不是目录时返回其所在目录。</summary>
    public static (string Folder, string FilePath) ResolveShortcut(string shortcutPath)
    {
        var target = Shortcuts.ResolveLinkTarget(shortcutPath);
        if (string.IsNullOrWhiteSpace(target)) return ("", "");

        target = Shortcuts.Expand(target);

        if (Directory.Exists(target)) return (target.TrimEnd('\\'), "");
        return (Path.GetDirectoryName(target) ?? "", File.Exists(target) ? target : "");
    }

    /// <summary>卸载后复查：返回发现的问题，空数组表示干净。</summary>
    public List<string> CheckResidual(string displayName, string installLocation, IEnumerable<string> clsids)
    {
        var issues = new List<string>();

        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
        {
            try
            {
                var hasFiles = Directory.EnumerateFileSystemEntries(installLocation).Any();
                if (hasFiles) issues.Add($"安装目录仍然存在且非空：{installLocation}");
            }
            catch (Exception)
            {
                // 读不了就不报
            }
        }

        var stillRegistered = FindStillRegistered(clsids);
        if (stillRegistered.Count > 0)
            issues.Add($"仍有 {stillRegistered.Count} 个 COM 类登记在 PackagedCom 里：{string.Join(", ", stillRegistered)}");

        var blocked = clsids.Count(ContextMenuService.IsBlocked);
        if (blocked > 0)
            issues.Add($"屏蔽列表里还留着 {blocked} 条记录（可在此页取消勾选清掉）");

        if (issues.Count == 0)
        {
            var entry = Match(installLocation, displayName, LoadAll())
                .FirstOrDefault(e => e.Name.Contains(displayName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) issues.Add($"卸载列表里仍能看到「{entry.Name}」");
        }

        return issues;
    }
}
