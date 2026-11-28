using System.IO;
using System.Security.Cryptography;
using WinKit.Core;

namespace WinKit.Services;

public sealed class DuplicateFile
{
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string SizeText => ThisPcService.FormatSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

public sealed class DuplicateGroup : ObservableObject
{
    private bool _isExpanded;

    public string Hash { get; init; } = "";
    public long Size { get; init; }
    public List<DuplicateFile> Files { get; init; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public int Count => Files.Count;

    /// <summary>保留一份后可以省下的空间（重复份数 - 1）× 单份体积。</summary>
    public long WastedBytes => Size * Math.Max(0, Files.Count - 1);

    public string SizeText => ThisPcService.FormatSize(Size);
    public string WastedText => ThisPcService.FormatSize(WastedBytes);
    public string CountText => $"{Files.Count} 份";
}

public sealed record DuplicateCleanupResult(IReadOnlyList<string> MovedPaths, IReadOnlyList<string> Failures)
{
    public bool Ok => MovedPaths.Count > 0 && Failures.Count == 0;
    public string Message => Failures.Count == 0
        ? $"已将 {MovedPaths.Count} 个文件移入回收站"
        : $"已移入回收站 {MovedPaths.Count} 个，{Failures.Count} 项未处理，请查看原因";
}

/// <summary>
/// 重复文件检测：三级过滤（大小 → 头部 64KB → 全文件 SHA-256），
/// 只有最终哈希相同的才算重复，不做“同名即重复”这种误判。
/// </summary>
public sealed class DuplicateFinderService
{
    private const int HeadBytes = 64 * 1024;

    public (List<DuplicateGroup> Groups, string? Error) Find(
        IEnumerable<string> roots,
        long minSize = 1024,
        bool includeHidden = false,
        Action<string>? progress = null,
        CancellationToken token = default)
    {
        var groups = new List<DuplicateGroup>();

        var validRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(NormalizePath)
            .OfType<string>()
            .Where(r => Directory.Exists(r) || File.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validRoots.Count == 0) return (groups, "请至少选择一个存在的扫描目录");

        try
        {
            var bySize = new Dictionary<long, List<string>>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in validRoots)
            {
                foreach (var file in Enumerate(root, includeHidden, token))
                {
                    token.ThrowIfCancellationRequested();

                    var path = NormalizePath(file.FullName);
                    if (path is null || !seenPaths.Add(path)) continue;

                    long size;
                    try { size = file.Length; } catch (Exception) { continue; }
                    if (size < minSize) continue;

                    if (!bySize.TryGetValue(size, out var list))
                    {
                        list = new List<string>();
                        bySize[size] = list;
                    }

                    list.Add(path);
                }
            }

            var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
            var totalCandidates = candidates.Sum(kv => kv.Value.Count);
            progress?.Invoke($"按大小筛选出 {candidates.Count} 组 / {totalCandidates} 个候选文件，开始校验内容…");

            var processed = 0;
            foreach (var (size, paths) in candidates)
            {
                token.ThrowIfCancellationRequested();

                // 第二级：头部 64KB 相同的才可能重复
                var byHead = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    var head = HashHead(path);
                    if (head is null) continue;

                    if (!byHead.TryGetValue(head, out var list))
                    {
                        list = new List<string>();
                        byHead[head] = list;
                    }

                    list.Add(path);
                }

                foreach (var group in byHead.Values.Where(v => v.Count > 1))
                {
                    token.ThrowIfCancellationRequested();

                    // 第三级：整文件 SHA-256
                    var byFull = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in group)
                    {
                        token.ThrowIfCancellationRequested();
                        var full = DiffService.HashFile(path);
                        if (full is null) continue;

                        if (!byFull.TryGetValue(full, out var list))
                        {
                            list = new List<string>();
                            byFull[full] = list;
                        }

                        list.Add(path);
                    }

                    foreach (var (hash, files) in byFull)
                    {
                        if (files.Count < 2) continue;

                        var groupFiles = new List<DuplicateFile>();
                        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        {
                            DateTime modified = default;
                            try { modified = File.GetLastWriteTime(path); } catch (Exception) { }

                            groupFiles.Add(new DuplicateFile
                            {
                                FullPath = path,
                                Size = size,
                                Modified = modified
                            });
                        }

                        if (groupFiles.Count > 1)
                            groups.Add(new DuplicateGroup { Hash = hash, Size = size, Files = groupFiles });
                    }

                    processed++;
                    if (processed % 20 == 0) progress?.Invoke($"已校验 {processed} 组…");
                }
            }

            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return (groups, "已取消扫描");
        }
        catch (Exception ex)
        {
            return (groups, ex.Message);
        }

        return (groups
            .OrderByDescending(g => g.WastedBytes)
            .ThenByDescending(g => g.Size)
            .ToList(), null);
    }

    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<FileInfo> Enumerate(string root, bool includeHidden, CancellationToken token)
    {
        if (File.Exists(root))
        {
            yield return new FileInfo(root);
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = includeHidden ? FileAttributes.None : (FileAttributes.System | FileAttributes.Hidden)
        };

        IEnumerator<FileInfo>? enumerator = null;
        try
        {
            enumerator = new DirectoryInfo(root).EnumerateFiles("*", options).GetEnumerator();
        }
        catch (Exception)
        {
            yield break;
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();

            FileInfo? current;
            try
            {
                if (!enumerator.MoveNext()) break;
                current = enumerator.Current;
            }
            catch (Exception)
            {
                break;
            }

            yield return current;
        }
    }

    private static string? HashHead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(HeadBytes, (int)Math.Min(stream.Length, HeadBytes))];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return null;

            return Convert.ToHexString(SHA1.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Summarize(IReadOnlyList<DuplicateGroup> groups)
    {
        if (groups.Count == 0) return "没有发现重复文件";

        var files = groups.Sum(g => g.Count);
        var wasted = groups.Sum(g => g.WastedBytes);

        return $"发现 {groups.Count} 组重复，涉及 {files} 个文件，最多可释放 {ThisPcService.FormatSize(wasted)}";
    }

    /// <summary>按扫描指纹复核后移入回收站；保留副本在整批操作中禁止写入和删除。</summary>
    public static DuplicateCleanupResult MoveToRecycleBin(
        IEnumerable<string> paths, string keepPath, long expectedSize, string expectedHash)
    {
        var moved = new List<string>();
        var failures = new List<string>();
        var result = new DuplicateCleanupResult(moved, failures);
        if (expectedSize < 0 || string.IsNullOrWhiteSpace(expectedHash) ||
            expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)))
        {
            failures.Add("扫描指纹无效，已取消清理，请重新扫描");
            return result;
        }

        var keep = NormalizePath(keepPath);
        if (keep is null)
        {
            failures.Add("无法确认保留文件路径，已取消清理");
            return result;
        }

        FileStream keepStream;
        try
        {
            keepStream = OpenVerifiedFile(keep, expectedSize, expectedHash);
        }
        catch (Exception ex)
        {
            failures.Add($"保留文件无法通过复核，整组未清理：{keep}\n{ex.Message}");
            return result;
        }

        using (keepStream)
        {
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keep };
            foreach (var candidate in paths)
            {
                var path = NormalizePath(candidate);
                if (path is null)
                {
                    failures.Add($"无法确认目标路径，未清理：{candidate}");
                    continue;
                }
                if (!seenPaths.Add(path)) continue;

                try
                {
                    // 每个目标在回收前重新计算完整哈希；不允许读取正在写入的文件。
                    using (var target = OpenVerifiedFile(path, expectedSize, expectedHash)) { }

                    // Shell 按路径回收，需先释放目标的禁止删除句柄；保留副本仍被锁定。
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    moved.Add(path);
                }
                catch (Exception ex)
                {
                    failures.Add($"未移入回收站：{path}\n{ex.Message}");
                }
            }
        }

        if (moved.Count == 0 && failures.Count == 0)
            failures.Add("没有可安全移入回收站的其它文件，保留副本未改动");
        return result;
    }

    private static FileStream OpenVerifiedFile(string path, long expectedSize, string expectedHash)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            HeadBytes, FileOptions.SequentialScan);
        try
        {
            if (stream.Length != expectedSize)
                throw new IOException("文件大小已与扫描结果不同，请重新扫描");

            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (stream.Length != expectedSize || !StringComparer.OrdinalIgnoreCase.Equals(hash, expectedHash))
                throw new IOException("文件内容已与扫描结果不同，请重新扫描");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
