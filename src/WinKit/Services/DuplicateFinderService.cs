using System.IO;
using System.Security.Cryptography;

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

public sealed class DuplicateGroup
{
    public string Hash { get; init; } = "";
    public long Size { get; init; }
    public List<DuplicateFile> Files { get; init; } = new();

    public int Count => Files.Count;

    /// <summary>保留一份后可以省下的空间（重复份数 - 1）× 单份体积。</summary>
    public long WastedBytes => Size * Math.Max(0, Files.Count - 1);

    public string SizeText => ThisPcService.FormatSize(Size);
    public string WastedText => ThisPcService.FormatSize(WastedBytes);
    public string CountText => $"{Files.Count} 份";
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
            .Select(r => r.Trim().Trim('"'))
            .Where(r => Directory.Exists(r) || File.Exists(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validRoots.Count == 0) return (groups, "请至少选择一个存在的扫描目录");

        try
        {
            var bySize = new Dictionary<long, List<string>>();

            foreach (var root in validRoots)
            {
                foreach (var file in Enumerate(root, includeHidden, token))
                {
                    token.ThrowIfCancellationRequested();

                    long size;
                    try { size = file.Length; } catch (Exception) { continue; }
                    if (size < minSize) continue;

                    if (!bySize.TryGetValue(size, out var list))
                    {
                        list = new List<string>();
                        bySize[size] = list;
                    }

                    list.Add(file.FullName);
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
                        foreach (var path in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
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

                        groups.Add(new DuplicateGroup { Hash = hash, Size = size, Files = groupFiles });
                    }

                    processed++;
                    if (processed % 20 == 0) progress?.Invoke($"已校验 {processed} 组…");
                }
            }
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

    /// <summary>把指定文件安全移入回收站（不直接删除，可还原）。</summary>
    public static (bool Ok, string Message) MoveToRecycleBin(IEnumerable<string> paths)
    {
        var failed = 0;
        var moved = 0;

        foreach (var path in paths)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                moved++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        return failed == 0
            ? (true, $"已将 {moved} 个文件移入回收站")
            : (false, $"已移入回收站 {moved} 个，{failed} 个失败");
    }
}
