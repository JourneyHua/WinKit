using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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

/// <summary>路径和名称都只提供线索，不证明该项无人使用。</summary>
public enum ResidualConfidence
{
    ReferencesPath,
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

    public string ConfidenceText => Confidence == ResidualConfidence.ReferencesPath ? "引用目标路径" : "名称相似";

    /// <summary>计划任务文件不能代替任务注册状态的快照；文件关联由 Windows 保护。这两类仅供查看。</summary>
    public bool CanClean => Kind is not (ResidualKind.FileAssociation or ResidualKind.ScheduledTask)
        && (Kind != ResidualKind.Directory || DirectoryTarget is not null);

    internal ResidualScanService.DirectoryTarget? DirectoryTarget { get; init; }
    internal string ObservedShortcutTarget { get; init; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void RaiseSizeChanged() => OnPropertyChanged(nameof(SizeText));
}

public sealed class ResidualScanRequest
{
    /// <summary>与目标同名的项也仅作为待核对的候选。</summary>
    public IReadOnlyList<string> ExactNames { get; init; } = Array.Empty<string>();

    /// <summary>目录名包含其中任一项 → 判定「可能」。</summary>
    public IReadOnlyList<string> PartialNames { get; init; } = Array.Empty<string>();

    /// <summary>已识别的绝对安装路径；只匹配路径边界，不将独立名称当作路径证据。</summary>
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

    /// <summary>跳过指定目录及其子树；包含这些目录的上级只能查看。</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = Array.Empty<string>();
}

public sealed class ResidualScanResult
{
    public List<ResidualItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public int ScannedDirectories { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Elapsed { get; set; }

    public int PathReferenceCount => Items.Count(i => i.Confidence == ResidualConfidence.ReferencesPath);
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

        var fragments = request.PathFragments.Select(NormalizeLocalPath)
            .OfType<string>().Where(path => path.Length > 3)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var excluded = request.ExcludedPaths.Select(NormalizeLocalPath).OfType<string>().ToList();

        if (request.IncludeDirectories && (exact.Count > 0 || partial.Count > 0))
            ScanDirectories(request, exact, partial, excluded, result, progress, token);

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

        // 引用目标路径的项优先；所有候选默认不选，由用户核对后勾选。
        result.Items.Sort((a, b) =>
        {
            var c = a.Confidence.CompareTo(b.Confidence);
            if (c != 0) return c;
            c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        result.Elapsed = watch.Elapsed;
        return result;
    }

    // ---------- 目录 ----------

    private void ScanDirectories(
        ResidualScanRequest request,
        HashSet<string> exact,
        HashSet<string> partial,
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
        foreach (var root in roots)
        {
            if (!IsExcluded(root, excluded)) level.Add((root, 0));
        }

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

                    if (IsExcluded(child, excluded) || !seen.Add(child)) continue;
                    result.ScannedDirectories++;

                    if (result.ScannedDirectories - reported >= MaxProgressReport)
                    {
                        reported = result.ScannedDirectories;
                        progress?.Invoke($"已扫描 {result.ScannedDirectories} 个目录，命中 {result.Items.Count} 项…");
                    }

                    var name = Path.GetFileName(child);
                    if (MatchesName(name, exact, partial))
                    {
                        var canRecycle = TryGetDirectoryTarget(child, out var directory, out var reason, excluded);
                        result.Items.Add(new ResidualItem
                        {
                            Kind = ResidualKind.Directory,
                            Confidence = ResidualConfidence.Possible,
                            Path = child,
                            Detail = canRecycle ? DescribeDirectory(child) : reason,
                            DirectoryTarget = directory
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

    private static bool MatchesName(string name, HashSet<string> exact, HashSet<string> partial)
    {
        var norm = Norm(name);
        return exact.Contains(norm) || partial.Any(term => term.Length >= 3 && norm.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcluded(string path, List<string> excluded)
        => excluded.Count > 0 && NormalizeLocalPath(path) is { } full && excluded.Any(root => IsAtOrBelow(full, root));

    private static bool IsAtOrBelow(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + '\\', StringComparison.OrdinalIgnoreCase);

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
                var linkName = Path.GetFileNameWithoutExtension(link);
                var pathMatch = NormalizeLocalPath(target) is { } targetPath &&
                    fragments.Any(fragment => IsAtOrBelow(targetPath, fragment));
                if (!pathMatch && !MatchesName(linkName, exact, partial)) continue;

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
                    Confidence = pathMatch ? ResidualConfidence.ReferencesPath : ResidualConfidence.Possible,
                    Path = link,
                    ObservedShortcutTarget = target,
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

            if (!ReferencesTargetPath(image, fragments)) continue;

            result.Items.Add(new ResidualItem
            {
                Kind = ResidualKind.Service,
                Confidence = ResidualConfidence.ReferencesPath,
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
            var confidence = MatchesName(Path.GetFileName(file), exact, partial)
                ? ResidualConfidence.Possible : (ResidualConfidence?)null;
            if (fragments.Count > 0)
            {
                try
                {
                    if (ReferencesTargetPath(File.ReadAllText(file), fragments))
                        confidence = ResidualConfidence.ReferencesPath;
                }
                catch (Exception)
                {
                    // 读不到任务内容时只保留名称线索。
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
                if (!ReferencesTargetPath(command, fragments)) continue;

                result.Items.Add(new ResidualItem
                {
                    Kind = ResidualKind.Startup,
                    Confidence = ResidualConfidence.ReferencesPath,
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
                if (!ReferencesTargetPath(command, fragments)) continue;

                result.Items.Add(new ResidualItem
                {
                    Kind = ResidualKind.FileAssociation,
                    Confidence = ResidualConfidence.ReferencesPath,
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

    internal static bool ReferencesTargetPath(string text, IReadOnlyList<string> fragments)
    {
        var expanded = Environment.ExpandEnvironmentVariables(text).Replace('/', '\\');
        foreach (var fragment in fragments)
        {
            var from = 0;
            while (from < expanded.Length)
            {
                var at = expanded.IndexOf(fragment, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var end = at + fragment.Length;
                // 左边允许命令/XML 的分隔符；右边空格并不安全（Foo 与 Foo Bar 是不同目录）。
                if ((at == 0 || char.IsWhiteSpace(expanded[at - 1]) || expanded[at - 1] is '"' or '\'' or '>' or '=') &&
                    (end == expanded.Length || expanded[end] is '\\' or '"' or '\'' or '<'))
                    return true;
                from = at + 1;
            }
        }
        return false;
    }

    internal static string? NormalizeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var text = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
            if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\' ||
                text.AsSpan(2).Contains(':')) return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
            return full[3..].Split('\\').Any(part => part.EndsWith('.') || part.EndsWith(' ')) ? null : full;
        }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    internal sealed record DirectoryTarget(string Path, uint Volume, ulong Index);

    internal static bool TryGetDirectoryTarget(string path, out DirectoryTarget? target, out string reason,
        IReadOnlyList<string>? excludedPaths = null)
    {
        target = null;
        reason = "只支持可确认身份的本地磁盘文件夹。";
        var requested = NormalizeLocalPath(path);
        if (requested is null || requested.Length == 3) return false;
        if (excludedPaths?.Any(excluded => NormalizeLocalPath(excluded) is { } root &&
            (IsAtOrBelow(requested, root) || IsAtOrBelow(root, requested))) == true)
        {
            reason = "残留清理不会处理目标安装文件夹、它的上级或子文件夹。";
            return false;
        }

        try
        {
            if (new DriveInfo(requested[..3]).DriveType != DriveType.Fixed) return false;
            for (var part = requested; part.Length > 3; part = Path.GetDirectoryName(part)!)
            {
                var attrs = File.GetAttributes(part);
                if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                {
                    reason = "不能处理快捷链接、联接点或位于链接内的文件夹。";
                    return false;
                }
            }

            // 只读打开目录自身，核对 Windows 返回的真实路径，避免短文件名、替代盘符等别名绕过保护。
            const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
            using var handle = OpenDirectory(requested, 0, 7, IntPtr.Zero, 3, backupSemantics | openReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !ReadDirectoryInfo(handle, out var info) ||
                ((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                return false;
            var buffer = new StringBuilder(32768);
            var size = ReadFinalPath(handle, buffer, (uint)buffer.Capacity, 0);
            if (size == 0 || size >= buffer.Capacity) return false;
            var resolved = buffer.ToString();
            var real = resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? NormalizeLocalPath(resolved[4..]) : null;
            if (real is null || !string.Equals(real, requested, StringComparison.OrdinalIgnoreCase)) return false;
            if (IsProtectedDirectory(real))
            {
                reason = "系统文件夹、个人资料根目录及其上级不能在这里处理。";
                return false;
            }
            target = new DirectoryTarget(real, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
            reason = "";
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static bool IsProtectedDirectory(string path)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var protectedTree in new[]
        {
            system, Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
            Path.Combine(path[..3], "$Recycle.Bin"), Path.Combine(path[..3], "System Volume Information"),
            Path.Combine(path[..3], "Recovery"), Path.Combine(path[..3], "Boot")
        })
        {
            if (NormalizeLocalPath(protectedTree) is { } root && (IsAtOrBelow(path, root) || IsAtOrBelow(root, path))) return true;
        }

        foreach (var folder in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.CommonDesktopDirectory
        })
        {
            if (NormalizeLocalPath(Environment.GetFolderPath(folder)) is { } root && IsAtOrBelow(root, path)) return true;
        }
        if (NormalizeLocalPath(Shortcuts.GetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"))) is { } downloads &&
            IsAtOrBelow(downloads, path)) return true;
        if (NormalizeLocalPath(Environment.ProcessPath) is { } ownExe && IsAtOrBelow(ownExe, path)) return true;

        // 也保护其他账户的配置文件根，不能用管理员身份把整个配置文件当成应用目录。
        var profiles = Reg.GetString(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"), "ProfilesDirectory");
        if (NormalizeLocalPath(profiles) is { } profileRoot &&
            (IsAtOrBelow(profileRoot, path) || string.Equals(Path.GetDirectoryName(path), profileRoot, StringComparison.OrdinalIgnoreCase)))
            return true;
        foreach (var sid in Reg.SubKeyNames(RegPath.Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")))
        {
            var value = Reg.GetString(RegPath.Hklm($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}"), "ProfileImagePath");
            if (NormalizeLocalPath(value) is { } profile && IsAtOrBelow(profile, path)) return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryInfoByHandle
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryInfo(SafeFileHandle handle, out DirectoryInfoByHandle info);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint ReadFinalPath(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

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
