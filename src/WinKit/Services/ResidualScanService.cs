using System.Diagnostics;
using System.IO;
using WinKit.Core;

namespace WinKit.Services;

public enum ResidualKind
{
    Directory,
    Shortcut,
    Service,
    ScheduledTask,
    Startup,
    FileAssociation
}

/// <summary>置信度：目录名精确相等才算「确定」，包含匹配一律降级为「可能」。</summary>
public enum ResidualConfidence
{
    Confirmed,
    Possible
}

public sealed class ResidualItem : ObservableObject
{
    private bool _isSelected;

    public ResidualKind Kind { get; init; }
    public ResidualConfidence Confidence { get; init; }

    /// <summary>目录路径 / .lnk 路径 / 注册表完整路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>补充说明（目标 exe、命令行、值名等）。</summary>
    public string Detail { get; init; } = "";

    public HiveKind? Hive { get; init; }
    public string SubKey { get; init; } = "";
    public string ValueName { get; init; } = "";

    /// <summary>目录占用空间，只有用户点「计算大小」后才填。</summary>
    public long? SizeBytes { get; set; }

    public string SizeText => SizeBytes is null ? "—" : ThisPcService.FormatSize(SizeBytes.Value);

    public string KindText => Kind switch
    {
        ResidualKind.Directory => "目录",
        ResidualKind.Shortcut => "快捷方式",
        ResidualKind.Service => "服务",
        ResidualKind.ScheduledTask => "计划任务",
        ResidualKind.Startup => "开机启动",
        ResidualKind.FileAssociation => "文件关联",
        _ => "其他"
    };

    public string ConfidenceText => Confidence == ResidualConfidence.Confirmed ? "确定" : "可能";

    public bool IsConfirmed => Confidence == ResidualConfidence.Confirmed;

    /// <summary>
    /// 文件关联指向的 UserChoice 带哈希签名，删掉会被系统随即重置回原值，
    /// 所以这类命中只做提示，不参与清理。
    /// </summary>
    public bool CanClean => Kind != ResidualKind.FileAssociation;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void RaiseSizeChanged() => OnPropertyChanged(nameof(SizeText));
}

public sealed class ResidualScanRequest
{
    /// <summary>目录名精确等于其中任一项 → 判定「确定」。</summary>
    public IReadOnlyList<string> ExactNames { get; init; } = Array.Empty<string>();

    /// <summary>目录名包含其中任一项 → 判定「可能」。</summary>
    public IReadOnlyList<string> PartialNames { get; init; } = Array.Empty<string>();

    /// <summary>路径片段：安装目录、exe 名。用于服务/计划任务/启动项/文件关联的命中判断。</summary>
    public IReadOnlyList<string> PathFragments { get; init; } = Array.Empty<string>();

    /// <summary>额外扫描根（用户拖进来的文件夹所在位置）。</summary>
    public IReadOnlyList<string> ExtraRoots { get; init; } = Array.Empty<string>();

    /// <summary>目录扫描深度，1 = 只扫根的直属子目录。</summary>
    public int MaxDepth { get; init; } = 3;

    public bool IncludeDirectories { get; init; } = true;
    public bool IncludeShortcuts { get; init; } = true;
    public bool IncludeServices { get; init; } = true;
    public bool IncludeTasks { get; init; } = true;
    public bool IncludeStartup { get; init; } = true;
    public bool IncludeAssociations { get; init; } = true;

    /// <summary>跳过的路径（用户正在卸载的那个安装目录本身不算残留）。</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = Array.Empty<string>();
}

public sealed class ResidualScanResult
{
    public List<ResidualItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public int ScannedDirectories { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Elapsed { get; set; }

    public int ConfirmedCount => Items.Count(i => i.Confidence == ResidualConfidence.Confirmed);
    public int PossibleCount => Items.Count(i => i.Confidence == ResidualConfidence.Possible);
}

/// <summary>
/// 残留扫描：在软件安装目录之外，按「名称 → 路径片段」两级匹配，找出遗留的
/// 目录、快捷方式、服务、计划任务、开机启动项与文件关联。
///
/// 性能事实（本机实测）：只扫一层深 0.9 秒 / 3785 目录；扫三层深 4.6 秒 / 14296 目录；
/// 递归全扫 108 秒 / 141000 目录。所以默认三层深，深扫交给用户手动触发。
/// reparse point 必须排除：%LOCALAPPDATA% 有 3 个、%ProgramData% 有 5 个指向自身的
/// 兼容性链接，跟进会成环、也会把匹配结果指到根本不该动的位置。
/// </summary>
public sealed class ResidualScanService
{
    private const int MaxProgressReport = 40;

    /// <summary>这些目录名太通用，命中一律按「可能」处理。</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "google", "common files", "temp", "tmp", "programs", "packages",
        "cache", "config", "data", "logs", "logs", "update", "updater", "installer",
        "node_modules", "bin", "lib", "app", "apps", "system", "windows", "documents",
        "desktop", "downloads", "public", "users", "default", "backup", "backups"
    };

    private static readonly EnumerationOptions DirOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        // 只跳 reparse point；默认的 Hidden|System 会把大量软件数据目录藏掉
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    public IReadOnlyList<string> DefaultRoots { get; }

    public ResidualScanService()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (roots.Any(r => string.Equals(r, path, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(path);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        // 软件装在非系统盘上很常见（实测任务栏里就有一个 D:\Quark\quark.exe）。
        // 各盘符只取顶层目录，成本几乎为零（106 个目录 / 4 毫秒）。
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                Add(drive.RootDirectory.FullName);
            }
        }
        catch (Exception)
        {
            // 枚举盘符失败不影响其余根
        }

        DefaultRoots = roots;
    }

    public ResidualScanResult Scan(
        ResidualScanRequest request,
        Action<string>? progress = null,
        CancellationToken token = default)
    {
        var result = new ResidualScanResult();
        var watch = Stopwatch.StartNew();

        var exact = new HashSet<string>(
            request.ExactNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Norm),
            StringComparer.OrdinalIgnoreCase);

        var partial = new HashSet<string>(
            request.PartialNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Norm),
            StringComparer.OrdinalIgnoreCase);

        var fragments = request.PathFragments
            .Where(f => !string.IsNullOrWhiteSpace(f) && f.Trim().Length > 3)
            .Select(f => f.Trim().Trim('"').Replace('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var excluded = request.ExcludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().TrimEnd('\\'))
            .ToList();

        if (request.IncludeDirectories && (exact.Count > 0 || partial.Count > 0))
            ScanDirectories(request, exact, partial, fragments, excluded, result, progress, token);

        token.ThrowIfCancellationRequested();

        if (request.IncludeShortcuts)
            ScanShortcuts(exact, partial, fragments, excluded, result, token);

        if (request.IncludeServices)
            ScanServices(fragments, result);

        if (request.IncludeTasks)
            ScanScheduledTasks(exact, partial, fragments, result);

        if (request.IncludeStartup)
            ScanStartup(fragments, result);

        if (request.IncludeAssociations)
            ScanAssociations(fragments, result);

        // 确定项排在可能项前面，同类按路径排序，方便用户逐项核对
        result.Items.Sort((a, b) =>
        {
            var c = a.Confidence.CompareTo(b.Confidence);
            if (c != 0) return c;
            c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var item in result.Items)
            item.IsSelected = item.Confidence == ResidualConfidence.Confirmed;

        result.Elapsed = watch.Elapsed;
        return result;
    }

    // ---------- 目录 ----------

    private void ScanDirectories(
        ResidualScanRequest request,
        HashSet<string> exact,
        HashSet<string> partial,
        List<string> fragments,
        List<string> excluded,
        ResidualScanResult result,
        Action<string>? progress,
        CancellationToken token)
    {
        var roots = new List<string>(DefaultRoots);
        foreach (var extra in request.ExtraRoots)
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            var trimmed = extra.Trim().TrimEnd('\\');
            if (!Directory.Exists(trimmed)) continue;
            if (roots.Any(r => string.Equals(r.TrimEnd('\\'), trimmed, StringComparison.OrdinalIgnoreCase))) continue;
            roots.Add(trimmed);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var level = new List<(string Path, int Depth)>();
        foreach (var root in roots) level.Add((root, 0));

        var reported = 0;

        for (var depth = 1; depth <= Math.Max(1, request.MaxDepth); depth++)
        {
            var next = new List<(string Path, int Depth)>();

            foreach (var (dir, _) in level)
            {
                token.ThrowIfCancellationRequested();

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(dir, "*", DirOptions);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    token.ThrowIfCancellationRequested();

                    // 再判一次 reparse point：某些兼容性链接的 Attributes 在枚举时才可见
                    try
                    {
                        var attrs = File.GetAttributes(child);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (!seen.Add(child)) continue;
                    result.ScannedDirectories++;

                    if (result.ScannedDirectories - reported >= MaxProgressReport)
                    {
                        reported = result.ScannedDirectories;
                        progress?.Invoke($"已扫描 {result.ScannedDirectories} 个目录，命中 {result.Items.Count} 项…");
                    }

                    var name = System.IO.Path.GetFileName(child);
                    var confidence = Classify(name, child, exact, partial, fragments);
                    if (confidence is not null && !IsExcluded(child, excluded))
                    {
                        result.Items.Add(new ResidualItem
                        {
                            Kind = ResidualKind.Directory,
                            Confidence = confidence.Value,
                            Path = child,
                            Detail = DescribeDirectory(child)
                        });
                    }

                    if (depth < request.MaxDepth) next.Add((child, depth));
                }
            }

            level = next;
            if (level.Count == 0) break;

            // %USERPROFILE%\AppData 已经分别是 Roaming / Local 两个根，跟着它再扫一遍是纯浪费
            if (depth == 1)
                level = level.Where(t => !t.Path.EndsWith(@"\AppData", StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private static ResidualConfidence? Classify(
        string name,
        string fullPath,
        HashSet<string> exact,
        HashSet<string> partial,
        List<string> fragments)
    {
        var norm = Norm(name);

        if (exact.Contains(norm))
            return GenericNames.Contains(norm) ? ResidualConfidence.Possible : ResidualConfidence.Confirmed;

        foreach (var token in partial)
        {
            if (token.Length < 3) continue;
            if (norm.Contains(token, StringComparison.OrdinalIgnoreCase))
                return ResidualConfidence.Possible;
        }

        // 名字不像，但路径里带着发布者目录（如 Roaming\The Quark Authors）——只能算「可能」
        foreach (var fragment in fragments)
        {
            if (fragment.Length < 4) continue;
            if (fullPath.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return ResidualConfidence.Possible;
        }

        return null;
    }

    private static bool IsExcluded(string path, List<string> excluded)
    {
        foreach (var item in excluded)
        {
            if (path.Equals(item, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string DescribeDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var lastWrite = info.LastWriteTime;
            return $"最后修改 {lastWrite:yyyy-MM-dd}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ---------- 快捷方式 ----------

    private void ScanShortcuts(
        HashSet<string> exact,
        HashSet<string> partial,
        List<string> fragments,
        List<string> excluded,
        ResidualScanResult result,
        CancellationToken token)
    {
        foreach (var (folder, label) in ShortcutFolders())
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                });
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var link in links)
            {
                token.ThrowIfCancellationRequested();

                if (IsExcluded(link, excluded)) continue;

                var target = Shortcuts.ResolveLinkTarget(link);
                var linkName = System.IO.Path.GetFileNameWithoutExtension(link);

                var hit = false;
                var confidence = ResidualConfidence.Possible;

                foreach (var fragment in fragments)
                {
                    if (fragment.Length < 4) continue;
                    if (target.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = true;
                        confidence = ResidualConfidence.Confirmed;
                        break;
                    }
                }

                if (!hit)
                {
                    var norm = Norm(linkName);
                    if (exact.Contains(norm)) { hit = true; confidence = ResidualConfidence.Confirmed; }
                    else if (partial.Any(t => t.Length >= 3 && norm.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        hit = true;
                        confidence = ResidualConfidence.Possible;
                    }
                }

                if (!hit) continue;

                var missing = Shortcuts.IsTargetMissing(target);
                var state = missing switch
                {
                    true => "（目标已不存在）",
                    null => "（目标无法解析）",
                    _ => ""
                };

                result.Items.Add(new ResidualItem
                {
                    Kind = ResidualKind.Shortcut,
                    Confidence = confidence,
                    Path = link,
                    Detail = $"{label} · 目标：{(string.IsNullOrWhiteSpace(target) ? "（无目标路径）" : target)}{state}"
                });
            }
        }
    }

    private static IEnumerable<(string Folder, string Label)> ShortcutFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "开始菜单");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "公共开始菜单");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面");
        yield return (TaskbarService.PinnedFolder, "任务栏");

        var quickLaunch = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch");
        yield return (quickLaunch, "快速启动");
    }

    // ---------- 服务 ----------

    private static void ScanServices(List<string> fragments, ResidualScanResult result)
    {
        if (fragments.Count == 0) return;

        var root = RegPath.Hklm(@"SYSTEM\CurrentControlSet\Services");
        foreach (var name in Reg.SubKeyNames(root))
        {
            var path = RegPath.Hklm($@"SYSTEM\CurrentControlSet\Services\{name}");
            var image = Reg.GetString(path, "ImagePath");
            if (string.IsNullOrWhiteSpace(image)) continue;

            if (!ContainsFragment(image, fragments)) continue;

            result.Items.Add(new ResidualItem
            {
                Kind = ResidualKind.Service,
                Confidence = ResidualConfidence.Confirmed,
                Path = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}",
                Hive = HiveKind.LocalMachine,
                SubKey = $@"SYSTEM\CurrentControlSet\Services\{name}",
                Detail = image
            });
        }
    }

    // ---------- 计划任务 ----------

    private static void ScanScheduledTasks(
        HashSet<string> exact,
        HashSet<string> partial,
        List<string> fragments,
        ResidualScanResult result)
    {
        var folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");

        if (!Directory.Exists(folder)) return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            });
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
        {
            var taskName = System.IO.Path.GetFileName(file);
            var norm = Norm(taskName);

            var confidence = (ResidualConfidence?)null;

            if (exact.Contains(norm)) confidence = ResidualConfidence.Confirmed;
            else if (partial.Any(t => t.Length >= 3 && norm.Contains(t, StringComparison.OrdinalIgnoreCase)))
                confidence = ResidualConfidence.Possible;

            if (confidence is null && fragments.Count > 0)
            {
                // 任务名不一定带软件名，命令行里带安装路径才是硬证据
                try
                {
                    var content = File.ReadAllText(file);
                    if (ContainsFragment(content, fragments)) confidence = ResidualConfidence.Confirmed;
                }
                catch (Exception)
                {
                    // 读不了就看名字
                }
            }

            if (confidence is null) continue;

            var relative = System.IO.Path.GetRelativePath(folder, file);

            result.Items.Add(new ResidualItem
            {
                Kind = ResidualKind.ScheduledTask,
                Confidence = confidence.Value,
                Path = file,
                Detail = $"任务名：{relative.Replace('\\', '/')}"
            });
        }
    }

    // ---------- 开机启动 ----------

    private static void ScanStartup(List<string> fragments, ResidualScanResult result)
    {
        if (fragments.Count == 0) return;

        var runKeys = new[]
        {
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (HiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (HiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (HiveKind.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
        };

        foreach (var (hive, subKey) in runKeys)
        {
            var path = RegPath.Of(hive, subKey);
            foreach (var valueName in Reg.ValueNames(path))
            {
                var command = Reg.GetString(path, valueName);
                if (string.IsNullOrWhiteSpace(command)) continue;
                if (!ContainsFragment(command, fragments)) continue;

                result.Items.Add(new ResidualItem
                {
                    Kind = ResidualKind.Startup,
                    Confidence = ResidualConfidence.Confirmed,
                    Path = path.FullPath,
                    Hive = hive,
                    SubKey = subKey,
                    ValueName = valueName,
                    Detail = command
                });
            }
        }
    }

    // ---------- 文件关联 ----------

    private static void ScanAssociations(List<string> fragments, ResidualScanResult result)
    {
        if (fragments.Count == 0) return;

        var fileExts = RegPath.Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts");

        foreach (var ext in Reg.SubKeyNames(fileExts))
        {
            if (!ext.StartsWith('.')) continue;

            var progId = Reg.GetString(RegPath.Hkcu($@"{fileExts.SubKey}\{ext}\UserChoice"), "ProgId");
            if (string.IsNullOrWhiteSpace(progId)) continue;

            foreach (var classesRoot in new[]
                     {
                         (HiveKind.CurrentUser, $@"Software\Classes\{progId}\shell\open\command"),
                         (HiveKind.LocalMachine, $@"SOFTWARE\Classes\{progId}\shell\open\command"),
                         (HiveKind.LocalMachine, $@"SOFTWARE\Classes\WOW6432Node\{progId}\shell\open\command")
                     })
            {
                var command = Reg.GetString(RegPath.Of(classesRoot.Item1, classesRoot.Item2));
                if (string.IsNullOrWhiteSpace(command)) continue;
                if (!ContainsFragment(command, fragments)) continue;

                result.Items.Add(new ResidualItem
                {
                    Kind = ResidualKind.FileAssociation,
                    Confidence = ResidualConfidence.Confirmed,
                    // UserChoice 带哈希签名，删掉会被系统重置，这里只做提示不参与清理
                    Path = $@"HKEY_CURRENT_USER\{fileExts.SubKey}\{ext}\UserChoice",
                    Hive = HiveKind.CurrentUser,
                    SubKey = $@"{fileExts.SubKey}\{ext}\UserChoice",
                    ValueName = "ProgId",
                    Detail = $"{ext} → {progId} · {command}"
                });

                break;
            }
        }
    }

    // ---------- 工具 ----------

    private static bool ContainsFragment(string text, List<string> fragments)
    {
        foreach (var fragment in fragments)
        {
            if (fragment.Length < 4) continue;
            if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Norm(string value)
        => value.Trim().TrimEnd('\\').ToLowerInvariant();

    /// <summary>统计目录占用空间（用户点「计算大小」时才调，不参与扫描主流程）。</summary>
    public (long Bytes, int Files) MeasureDirectory(string path, CancellationToken token = default)
    {
        long bytes = 0;
        var files = 0;

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (Exception)
                {
                    // 单个文件读不到就跳过
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 目录被删/无权限：返回已统计到的部分
        }

        return (bytes, files);
    }
}
