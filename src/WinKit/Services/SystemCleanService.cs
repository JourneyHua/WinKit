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
    public string CountText => FileCount > 0 ? $"{FileCount} 个文件" : "干净";
    public string PrimaryPath => Paths.FirstOrDefault() ?? "";
    public bool HasDirectory => !IsRecycleBin && !string.IsNullOrWhiteSpace(PrimaryPath) && Directory.Exists(PrimaryPath);
    public string SafetyTag => DefaultSelected ? "安全推荐" : "可选清理";
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
                Description = @"C:\Windows\Temp 下的系统服务与安装器临时文件",
                Paths = new[] { Path.Combine(winDir, "Temp") },
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "wu_cache",
                Category = "系统更新缓存",
                Title = "Windows Update 更新下载缓存",
                Description = @"C:\Windows\SoftwareDistribution\Download 已下载完的补丁安装包缓存",
                Paths = new[] { Path.Combine(winDir, "SoftwareDistribution", "Download") },
                DefaultSelected = true,
                IsSelected = true
            },
            new()
            {
                Id = "crash_dumps",
                Category = "崩溃转储与报告",
                Title = "程序崩溃转储 (CrashDumps / Minidump / WER)",
                Description = "应用崩溃产生的 .dmp 内存转储与 Windows 错误报告队列文件",
                Paths = new[]
                {
                    Path.Combine(localApp, "CrashDumps"),
                    Path.Combine(winDir, "Minidump"),
                    @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                    @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue"
                },
                DefaultSelected = true,
                IsSelected = true
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
                Title = "Windows CBS 组件日志与安装日志",
                Description = @"C:\Windows\Logs\CBS 与 Panther 下的旧安装诊断日志",
                Paths = new[]
                {
                    Path.Combine(winDir, "Logs", "CBS"),
                    Path.Combine(winDir, "Logs", "DISM")
                },
                FilePattern = "*.log",
                DefaultSelected = true,
                IsSelected = true
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
        await Task.Run(() =>
        {
            foreach (var item in targets)
            {
                if (item.IsRecycleBin)
                {
                    var (bytes, count) = QueryRecycleBin();
                    item.SizeBytes = bytes;
                    item.FileCount = count;
                    continue;
                }

                long totalBytes = 0;
                var totalFiles = 0;

                foreach (var dir in item.Paths)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    var (b, c) = MeasureDirectory(dir, item.FilePattern, item.Recursive);
                    totalBytes += b;
                    totalFiles += c;
                }

                item.SizeBytes = totalBytes;
                item.FileCount = totalFiles;
            }
        });
    }

    public async Task<(long FreedBytes, int DeletedFiles)> CleanTargetsAsync(
        IReadOnlyList<CleanupTargetItem> selectedTargets,
        Action<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            long freedBytes = 0;
            var deletedFiles = 0;

            foreach (var item in selectedTargets)
            {
                progress?.Invoke($"正在清理：{item.Title}…");

                if (item.IsRecycleBin)
                {
                    var before = item.SizeBytes;
                    var beforeCount = item.FileCount;
                    var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

                    // 返回 HRESULT 且不会抛异常：失败（被占用/策略限制）时不能把体积算进"已释放"
                    if (hr == 0)
                    {
                        freedBytes += before;
                        deletedFiles += beforeCount;
                    }
                    else
                    {
                        progress?.Invoke($"回收站未能清空（HRESULT 0x{hr:X8}），已跳过统计");
                    }

                    continue;
                }

                foreach (var dir in item.Paths)
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    var (b, c) = CleanDirectory(dir, item.FilePattern, item.Recursive);
                    freedBytes += b;
                    deletedFiles += c;
                }
            }

            return (freedBytes, deletedFiles);
        });
    }

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

    private static (long Bytes, int Count) MeasureDirectory(string rootDir, string pattern, bool recursive)
    {
        long bytes = 0;
        var count = 0;

        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, pattern))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        bytes += fi.Length;
                        count++;
                    }
                    catch (Exception) { }
                }

                if (recursive)
                {
                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            var attr = File.GetAttributes(sub);
                            if ((attr & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        return (bytes, count);
    }

    private static (long FreedBytes, int DeletedCount) CleanDirectory(string rootDir, string pattern, bool recursive)
    {
        long freed = 0;
        var deleted = 0;

        var dirs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            dirs.Add(current);
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, pattern))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        var len = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += len;
                        deleted++;
                    }
                    catch (Exception)
                    {
                        // 被正在运行的进程锁定的临时文件直接跳过
                    }
                }

                if (recursive)
                {
                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            var attr = File.GetAttributes(sub);
                            if ((attr & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
        }

        if (recursive && pattern == "*")
        {
            // 自底向上清理已变空的子目录（保留根缓存目录本身）
            for (var i = dirs.Count - 1; i >= 1; i--)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dirs[i]).Any())
                        Directory.Delete(dirs[i], false);
                }
                catch (Exception) { }
            }
        }

        return (freed, deleted);
    }

    public async Task<List<LargeFileItem>> ScanLargeFilesAsync(
        string rootScope,
        long minBytes = 100L * 1024 * 1024,
        int maxResults = 300)
    {
        return await Task.Run(() =>
        {
            var found = new List<LargeFileItem>();
            var roots = new List<string>();

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
                if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                    stack.Push(r);

            while (stack.Count > 0)
            {
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
                        try
                        {
                            var fi = new FileInfo(file);
                            if (fi.Length < minBytes) continue;
                            if (fi.Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)
                                || fi.Name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                                || fi.Name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            found.Add(new LargeFileItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? "",
                                Extension = string.IsNullOrWhiteSpace(fi.Extension) ? "无扩展名" : fi.Extension.ToLowerInvariant(),
                                SizeBytes = fi.Length,
                                SizeText = ThisPcService.FormatSize(fi.Length),
                                LastModified = fi.LastWriteTime,
                                LastModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                            });
                        }
                        catch (Exception) { }
                    }

                    foreach (var sub in Directory.EnumerateDirectories(current))
                    {
                        try
                        {
                            var attr = File.GetAttributes(sub);
                            if ((attr & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }

            return found
                .OrderByDescending(f => f.SizeBytes)
                .Take(maxResults)
                .ToList();
        });
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
