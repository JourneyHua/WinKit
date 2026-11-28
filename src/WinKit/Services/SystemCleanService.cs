using System.IO;
using System.Runtime.InteropServices;
using WinKit.Core;

namespace WinKit.Services;

public sealed class CleanupTargetItem : ObservableObject
{
    private bool _isSelected;
    private long _sizeBytes;
    private int _fileCount;

    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Paths { get; init; } = Array.Empty<string>();
    public string FilePattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;
    public bool DefaultSelected { get; init; } = true;
    public bool IsRecycleBin { get; init; }
    public bool RequiresAdmin { get; init; }

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

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (Set(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeText));
        }
    }

    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (Set(ref _fileCount, value))
                OnPropertyChanged(nameof(CountText));
        }
    }

    public string SizeText => SizeBytes > 0 ? ThisPcService.FormatSize(SizeBytes) : "0 B";
    public string CountText => FileCount > 0 ? $"{FileCount} 个文件" : "—";
    public string PrimaryPath => Paths.FirstOrDefault() ?? "";
    public bool HasDirectory => !IsRecycleBin && !string.IsNullOrWhiteSpace(PrimaryPath) && Directory.Exists(PrimaryPath);
    public bool CanSelect { get; internal set; } = true;
    public string SelectionHint => CanSelect ? "将此类文件加入清理范围" : "此项清理需要管理员权限";
}

public sealed class LargeFileItem : ObservableObject
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string Extension { get; init; } = "";
    public long SizeBytes { get; init; }
    public string SizeText { get; init; } = "";
    public DateTime LastModified { get; init; }
    public string LastModifiedText { get; init; } = "";

    public string SearchBlob => $"{FileName} {DirectoryPath} {Extension}".ToLowerInvariant();
}

public sealed class SystemCleanService
{
    public const int DefaultMinAgeMinutes = 120;
    public const int MaxLargeFileResults = 300;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    public List<CleanupTargetItem> BuildTargets()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return new List<CleanupTargetItem>
        {
            new()
            {
                Id = "user_temp",
                Category = "系统临时文件",
                Title = "当前用户临时文件夹 (%TEMP%)",
                Description = "软件安装、解压与运行期间产生的临时文件（正在被占用的文件会自动跳过）",
                Paths = new[] { Path.GetTempPath() },
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "win_temp",
                Category = "系统临时文件",
                Title = "Windows 系统级临时目录",
                Description = "系统服务和安装程序的临时文件。清理需要管理员权限。",
                Paths = new[] { Path.Combine(winDir, "Temp") },
                RequiresAdmin = true,
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "wu_cache",
                Category = "系统更新缓存",
                Title = "Windows Update 更新下载缓存",
                Description = "Windows 可能会重新下载所需更新。清理需要管理员权限。",
                Paths = new[] { Path.Combine(winDir, "SoftwareDistribution", "Download") },
                RequiresAdmin = true,
                DefaultSelected = false,
                IsSelected = false
            },
            new()
            {
                Id = "crash_dumps",
                Category = "崩溃转储与报告",
                Title = "程序崩溃转储 (CrashDumps / Minidump / WER)",
                Description = "删除后无法再用这些转储和报告排查崩溃。清理需要管理员权限。",
                RequiresAdmin = true,
                Paths = new[]
                {
                    Path.Combine(localApp, "CrashDumps"),
                    Path.Combine(winDir, "Minidump"),
                    @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                    @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue"
                },
                DefaultSelected = false,
                IsSelected = false
            },
            new()
            {
                Id = "gpu_shader",
                Category = "图形与显示缓存",
                Title = "DirectX 与显卡着色器缓存 (Shader Cache)",
                Description = "DirectX、NVIDIA 与 AMD 驱动编译的旧着色器缓存，清理后游戏会按需重新生成",
                Paths = new[]
                {
                    Path.Combine(localApp, "D3DSCache"),
                    Path.Combine(localApp, "NVIDIA", "DXCache"),
                    Path.Combine(localApp, "NVIDIA", "GLCache"),
                    Path.Combine(localApp, "AMD", "DxCache")
                },
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "win_logs",
                Category = "系统日志文件",
                Title = "Windows CBS 与 DISM 日志",
                Description = "删除后无法再用这些日志排查组件或更新问题。清理需要管理员权限。",
                Paths = new[]
                {
                    Path.Combine(winDir, "Logs", "CBS"),
                    Path.Combine(winDir, "Logs", "DISM")
                },
                FilePattern = "*.log",
                RequiresAdmin = true,
                DefaultSelected = false,
                IsSelected = false
            },
            new()
            {
                Id = "thumb_cache",
                Category = "资源管理器缓存",
                Title = "资源管理器缩略图缓存 (thumbcache)",
                Description = "图片与视频文件夹预览缩略图数据库，占用过大时可安全重置",
                Paths = new[] { Path.Combine(localApp, "Microsoft", "Windows", "Explorer") },
                FilePattern = "thumbcache_*.db",
                Recursive = false,
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "recycle_bin",
                Category = "可选清理项",
                Title = "系统回收站（所有分区）",
                Description = "清空所有磁盘分区回收站中的已删除文件（清空后不可从回收站恢复）",
                IsRecycleBin = true,
                DefaultSelected = false,
                IsSelected = false
            },
            new()
            {
                Id = "recent_links",
                Category = "可选清理项",
                Title = "最近使用的文档历史记录 (Recent)",
                Description = "仅清理 Recent 目录下的历史快捷方式 (.lnk)，绝不删除原文档文件",
                Paths = new[] { Path.Combine(appData, "Microsoft", "Windows", "Recent") },
                FilePattern = "*.lnk",
                Recursive = false,
                DefaultSelected = false,
                IsSelected = false
            },
            new()
            {
                Id = "dev_pkg_cache",
                Category = "可选清理项",
                Title = "开发包管理器下载缓存 (NuGet / npm / pip)",
                Description = "清理本地 NuGet HTTP 缓存、npm-cache 与 pip 下载缓存（不影响已安装环境）",
                Paths = new[]
                {
                    Path.Combine(localApp, "NuGet", "v3-cache"),
                    Path.Combine(localApp, "npm-cache"),
                    Path.Combine(localApp, "pip", "Cache")
                },
                DefaultSelected = false,
                IsSelected = false
            }
        };
    }

    public async Task ScanTargetsAsync(IReadOnlyList<CleanupTargetItem> targets)
    {
        var cutoff = CutoffUtc(DefaultMinAgeMinutes);
        var measured = await Task.Run(() => targets.Select(item =>
        {
            if (item.IsRecycleBin) return QueryRecycleBin();

            long bytes = 0;
            var count = 0;
            foreach (var dir in item.Paths)
            {
                if (!IsCleanupRoot(dir)) continue;
                var (b, c, _) = MeasureDirectory(dir, item.FilePattern, item.Recursive, cutoff);
                bytes += b;
                count += c;
            }
            return (bytes, count);
        }).ToArray());

        for (var i = 0; i < measured.Length; i++)
            (targets[i].SizeBytes, targets[i].FileCount) = measured[i];
    }

    public async Task<(long FreedBytes, int DeletedFiles, int SkippedItems)> CleanTargetsAsync(
        IReadOnlyList<CleanupTargetItem> selectedTargets,
        Action<string>? progress = null,
        int minAgeMinutes = DefaultMinAgeMinutes)
    {
        var targets = selectedTargets.ToArray();
        if (targets.Any(item => item.IsRecycleBin))
            throw new InvalidOperationException("回收站不属于普通清理，请使用单独的清空回收站入口。");

        var cutoff = CutoffUtc(minAgeMinutes);
        return await Task.Run(() =>
        {
            long freedBytes = 0;
            var deletedFiles = 0;
            var skippedItems = 0;

            foreach (var item in targets)
            {
                progress?.Invoke($"正在清理：{item.Title}…");
                foreach (var dir in item.Paths)
                {
                    if (!Directory.Exists(dir)) continue;
                    if (!IsCleanupRoot(dir))
                    {
                        skippedItems++;
                        continue;
                    }

                    var (b, c, s) = CleanDirectory(dir, item.FilePattern, item.Recursive, cutoff);
                    freedBytes += b;
                    deletedFiles += c;
                    skippedItems += s;
                }
            }

            return (freedBytes, deletedFiles, skippedItems);
        });
    }

    internal Task<(bool Success, string Message)> EmptyRecycleBinAsync() => Task.Run(() =>
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        if (hr == 0) return (true, "回收站已清空。");
        return (false, $"未能确认回收站已清空（错误 0x{hr:X8}）。请重新扫描后查看剩余项目。");
    });

    private static DateTime CutoffUtc(int minAgeMinutes) => minAgeMinutes > 0
        ? DateTime.UtcNow.AddMinutes(-minAgeMinutes)
        : DateTime.MinValue;

    private static bool IsCleanupRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Parent is null) return false; // 不允许把整块盘作为缓存清理目标。
            for (DirectoryInfo? part = directory; part is not null; part = part.Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool CanDelete(FileInfo file, DateTime cutoff) =>
        (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) == 0 &&
        IsOlderThan(file, cutoff);

    private static bool CanTraverse(string path, int depth, DateTime cutoff)
    {
        var directory = new DirectoryInfo(path);
        return (directory.Attributes & FileAttributes.ReparsePoint) == 0 && (depth == 0 || IsOlderThan(directory, cutoff));
    }

    private static bool IsOlderThan(FileSystemInfo entry, DateTime cutoff) => cutoff == DateTime.MinValue ||
        (entry.CreationTimeUtc < cutoff && entry.LastWriteTimeUtc < cutoff);

    private static (long Bytes, int Count) QueryRecycleBin()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBin(null, ref info);
            if (hr == 0)
            {
                return (Math.Max(0, info.i64Size), (int)Math.Clamp(info.i64NumItems, 0, int.MaxValue));
            }
        }
        catch (Exception) { }
        return (0, 0);
    }

    /// <summary>
    /// 遍历深度上限。缓存目录里偶尔会出现软链成环、或构建工具留下的超深嵌套，
    /// 无上限时会一直走下去（清理这类操作还是管理员权限，走错就是大范围删）。
    /// </summary>
    private const int MaxTraverseDepth = 16;

    /// <summary>
    /// 统计目录占用。两个约束：
    /// 1. 有深度上限（见 <see cref="MaxTraverseDepth"/>）；
    /// 2. 跳过项要计数，而不是只 catch 掉——被跳过的基本都是「访问被拒」，
    ///    静默吞掉会让用户以为体积已经算全了。
    /// </summary>
    private static (long Bytes, int Count, int Skipped) MeasureDirectory(
        string rootDir, string pattern, bool recursive, DateTime cutoff)
    {
        long bytes = 0;
        var count = 0;
        var skipped = 0;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootDir, 0));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            try
            {
                if (!CanTraverse(current, depth, cutoff)) continue;
                foreach (var file in Directory.EnumerateFiles(current, pattern))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (!CanDelete(fi, cutoff)) continue;
                        bytes += fi.Length;
                        count++;
                    }
                    catch (Exception)
                    {
                        skipped++;
                    }
                }

                if (!recursive) continue;

                if (depth >= MaxTraverseDepth)
                {
                    skipped++;
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                    stack.Push((sub, depth + 1));
            }
            catch (Exception)
            {
                // 枚举本层就失败（权限/设备不可读），计一次跳过
                skipped++;
            }
        }

        return (bytes, count, skipped);
    }

    /// <summary>只清理超过时间阈值的文件；不修改只读属性，不跟随重解析点。</summary>
    private static (long FreedBytes, int DeletedCount, int Skipped) CleanDirectory(
        string rootDir, string pattern, bool recursive, DateTime cutoff)
    {
        long freed = 0;
        var deleted = 0;
        var skipped = 0;

        var dirs = new List<string>();
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootDir, 0));

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            try
            {
                if (!CanTraverse(current, depth, cutoff)) continue;
                dirs.Add(current);
                foreach (var file in Directory.EnumerateFiles(current, pattern))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (!CanDelete(fi, cutoff)) continue;

                        var len = fi.Length;
                        fi.Delete();
                        freed += len;
                        deleted++;
                    }
                    catch (Exception)
                    {
                        // 被正在运行的进程锁定的临时文件跳过，但不能当作「清理完成」蒙过去，
                        // 计数后在结果里如实报出来
                        skipped++;
                    }
                }

                if (!recursive) continue;

                if (depth >= MaxTraverseDepth)
                {
                    skipped++;
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                    stack.Push((sub, depth + 1));
            }
            catch (Exception)
            {
                // 整层枚举失败（权限不足、设备已拔出），计一次跳过
                skipped++;
            }
        }

        if (recursive && pattern == "*")
        {
            // 自底向上清理已变空的子目录（保留根缓存目录本身）
            for (var i = dirs.Count - 1; i >= 1; i--)
            {
                try
                {
                    if (!CanTraverse(dirs[i], 1, cutoff)) continue;

                    if (!Directory.EnumerateFileSystemEntries(dirs[i]).Any())
                        Directory.Delete(dirs[i], false);
                }
                catch (Exception)
                {
                    skipped++;
                }
            }
        }

        return (freed, deleted, skipped);
    }

    public async Task<List<LargeFileItem>> ScanLargeFilesAsync(
        string rootScope,
        long minBytes = 100L * 1024 * 1024,
        int maxResults = MaxLargeFileResults,
        CancellationToken cancellationToken = default)
    {
        var resultLimit = Math.Clamp(maxResults, 0, MaxLargeFileResults);
        return await Task.Run(() =>
        {
            var found = new PriorityQueue<(LargeFileItem File, long Sequence), (long Size, long Sequence)>();
            var roots = new List<string>();
            long sequence = 0;

            if (rootScope == "user")
            {
                roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            else if (Directory.Exists(rootScope))
            {
                roots.Add(rootScope);
            }

            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var stack = new Stack<string>();
            foreach (var r in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                    stack.Push(r);
            }

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();

                // 跳过系统受保护的 WinSxS / System32 内部文件，避免误导用户删除系统核心镜像
                if (current.StartsWith(Path.Combine(winDir, "WinSxS"), StringComparison.OrdinalIgnoreCase)
                    || current.StartsWith(Path.Combine(winDir, "System32"), StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith("System Volume Information", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(file);
                            var size = fi.Length;
                            if (size < minBytes) continue;
                            if (fi.Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
                                || fi.Name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                                || fi.Name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var priority = (Size: size, Sequence: sequence++);
                            if (resultLimit == 0 ||
                                (found.Count >= resultLimit && found.TryPeek(out _, out var smallest) &&
                                 Comparer<(long Size, long Sequence)>.Default.Compare(priority, smallest) <= 0))
                            {
                                continue;
                            }

                            var item = new LargeFileItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? "",
                                Extension = string.IsNullOrWhiteSpace(fi.Extension) ? "无扩展名" : fi.Extension.ToLowerInvariant(),
                                SizeBytes = size,
                                SizeText = ThisPcService.FormatSize(size),
                                LastModified = fi.LastWriteTime,
                                LastModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                            };
                            if (found.Count < resultLimit)
                            {
                                found.Enqueue((item, priority.Sequence), priority);
                            }
                            else
                            {
                                found.Dequeue();
                                found.Enqueue((item, priority.Sequence), priority);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception) { }
                    }

                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var attr = File.GetAttributes(sub);
                            if ((attr & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception) { }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return found.UnorderedItems
                .Select(entry => entry.Element)
                .OrderByDescending(entry => entry.File.SizeBytes)
                .ThenBy(entry => entry.Sequence)
                .Select(entry => entry.File)
                .ToList();
        }, cancellationToken);
    }

    public async Task<(bool Success, string Message)> RunDismComponentCleanupAsync(Action<string>? progress = null)
    {
        progress?.Invoke("正在调用 DISM 清理旧版 WinSxS 系统组件存储（可能需要 1~5 分钟）…");
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "dism.exe",
            "/Online /Cleanup-Image /StartComponentCleanup",
            TimeSpan.FromMinutes(10));

        if (code == 0)
            return (true, "DISM WinSxS 组件存储清理完成");

        return (false, string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
    }
}
