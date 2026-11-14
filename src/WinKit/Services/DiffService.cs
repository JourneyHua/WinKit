using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WinKit.Services;

public enum DiffKind
{
    OnlyLeft,
    OnlyRight,
    ContentDiffers,
    NewerLeft,
    NewerRight,
    Same
}

public sealed class DiffEntry
{
    public string RelativePath { get; init; } = "";
    public DiffKind Kind { get; init; }
    public long LeftSize { get; init; }
    public long RightSize { get; init; }
    public DateTime LeftTime { get; init; }
    public DateTime RightTime { get; init; }
    public bool IsFolder { get; init; }

    public string KindText => Kind switch
    {
        DiffKind.OnlyLeft => "仅左侧",
        DiffKind.OnlyRight => "仅右侧",
        DiffKind.ContentDiffers => "内容不同",
        DiffKind.NewerLeft => "左侧较新",
        DiffKind.NewerRight => "右侧较新",
        _ => "相同"
    };

    public string LeftSizeText => IsFolder ? "目录" : LeftSize > 0 ? ThisPcService.FormatSize(LeftSize) : "—";
    public string RightSizeText => IsFolder ? "目录" : RightSize > 0 ? ThisPcService.FormatSize(RightSize) : "—";
    public string LeftTimeText => LeftTime == default ? "" : LeftTime.ToString("yyyy-MM-dd HH:mm");
    public string RightTimeText => RightTime == default ? "" : RightTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 合并列：原先「左侧大小 + 右侧大小 + 左侧时间 + 右侧时间」占 4 列共 440px，
    /// 把「相对路径」挤到 142px（而操作列 230px 比路径还宽）。
    /// </summary>
    public string LeftDetail => Combine(LeftSizeText, LeftTimeText);
    public string RightDetail => Combine(RightSizeText, RightTimeText);

    private static string Combine(string size, string time)
    {
        if (string.IsNullOrWhiteSpace(time)) return size;
        if (size is "—" or "") return time;
        return $"{size} · {time}";
    }
}

/// <summary>目录/文件的统一描述：FileInfo.Length 对目录会抛异常，必须自己区分。</summary>
internal sealed class ScanEntry
{
    public string FullPath { get; init; } = "";
    public long Length { get; init; }
    public DateTime LastWriteTime { get; init; }
    public bool IsDirectory { get; init; }
}

/// <summary>
/// 文件/文件夹对比。
/// 默认按「大小 + 最后写入时间」快速比较（不读内容，几万个文件也很快）；
/// 开关打开后对参数完全相同的文件再算 SHA-256，确认内容是否真的相同。
/// </summary>
public sealed class DiffService
{
    public (List<DiffEntry> Entries, string? Error) Compare(
        string left,
        string right,
        bool recursive = true,
        bool compareHash = false,
        Action<string>? progress = null,
        CancellationToken token = default)
    {
        var entries = new List<DiffEntry>();

        var leftIsFile = File.Exists(left);
        var rightIsFile = File.Exists(right);

        if (!leftIsFile && !Directory.Exists(left)) return (entries, $"左侧路径不存在：{left}");
        if (!rightIsFile && !Directory.Exists(right)) return (entries, $"右侧路径不存在：{right}");

        // 单文件对单文件
        if (leftIsFile || rightIsFile)
        {
            if (!leftIsFile || !rightIsFile)
                return (entries, "两个路径必须都是文件或都是文件夹");

            var l = new FileInfo(left);
            var r = new FileInfo(right);
            entries.Add(ComparePair(ToEntry(l), ToEntry(r), Path.GetFileName(left), compareHash));
            return (entries, null);
        }

        try
        {
            var leftMap = WalkDirectory(left, recursive, progress, token);
            var rightMap = WalkDirectory(right, recursive, progress, token);

            var allKeys = leftMap.Keys.Union(rightMap.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            foreach (var key in allKeys)
            {
                token.ThrowIfCancellationRequested();

                var inLeft = leftMap.TryGetValue(key, out var l);
                var inRight = rightMap.TryGetValue(key, out var r);

                if (inLeft && !inRight)
                {
                    entries.Add(new DiffEntry
                    {
                        RelativePath = key,
                        Kind = DiffKind.OnlyLeft,
                        LeftSize = l!.Length,
                        LeftTime = l.LastWriteTime,
                        IsFolder = l.IsDirectory
                    });
                    continue;
                }

                if (!inLeft && inRight)
                {
                    entries.Add(new DiffEntry
                    {
                        RelativePath = key,
                        Kind = DiffKind.OnlyRight,
                        RightSize = r!.Length,
                        RightTime = r.LastWriteTime,
                        IsFolder = r.IsDirectory
                    });
                    continue;
                }

                entries.Add(ComparePair(l!, r!, key, compareHash));
            }
        }
        catch (OperationCanceledException)
        {
            return (entries, "已取消对比");
        }
        catch (Exception ex)
        {
            return (entries, ex.Message);
        }

        return (entries, null);
    }

    private static ScanEntry ToEntry(FileInfo info)
    {
        var isDir = (info.Attributes & FileAttributes.Directory) != 0;
        return new ScanEntry
        {
            FullPath = info.FullName,
            Length = isDir ? 0 : info.Length,
            LastWriteTime = info.LastWriteTime,
            IsDirectory = isDir
        };
    }

    private static DiffEntry ComparePair(ScanEntry l, ScanEntry r, string key, bool compareHash)
    {
        if (l.IsDirectory != r.IsDirectory)
        {
            return new DiffEntry
            {
                RelativePath = key,
                Kind = DiffKind.ContentDiffers,
                LeftSize = l.Length,
                RightSize = r.Length,
                LeftTime = l.LastWriteTime,
                RightTime = r.LastWriteTime
            };
        }

        if (l.IsDirectory)
        {
            return new DiffEntry
            {
                RelativePath = key,
                Kind = DiffKind.Same,
                LeftTime = l.LastWriteTime,
                RightTime = r.LastWriteTime,
                IsFolder = true
            };
        }

        DiffKind kind;
        if (l.Length != r.Length)
        {
            kind = DiffKind.ContentDiffers;
        }
        else if (compareHash && HashFile(l.FullPath) != HashFile(r.FullPath))
        {
            kind = DiffKind.ContentDiffers;
        }
        else if (l.LastWriteTime == r.LastWriteTime)
        {
            kind = DiffKind.Same;
        }
        else
        {
            kind = l.LastWriteTime > r.LastWriteTime ? DiffKind.NewerLeft : DiffKind.NewerRight;
        }

        return new DiffEntry
        {
            RelativePath = key,
            Kind = kind,
            LeftSize = l.Length,
            RightSize = r.Length,
            LeftTime = l.LastWriteTime,
            RightTime = r.LastWriteTime
        };
    }

    private static Dictionary<string, ScanEntry> WalkDirectory(
        string root, bool recursive, Action<string>? progress, CancellationToken token)
    {
        var map = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
            ReturnSpecialDirectories = false
        };

        var count = 0;
        foreach (var info in new DirectoryInfo(rootFull).EnumerateFileSystemInfos("*", options))
        {
            token.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(rootFull, info.FullName);
            var isDir = (info.Attributes & FileAttributes.Directory) != 0;

            map[relative] = new ScanEntry
            {
                FullPath = info.FullName,
                Length = isDir ? 0 : ((FileInfo)info).Length,
                LastWriteTime = info.LastWriteTime,
                IsDirectory = isDir
            };

            if (++count % 2000 == 0) progress?.Invoke($"已扫描 {count} 项：{Path.GetFileName(rootFull)}");
        }

        progress?.Invoke($"扫描完成：{Path.GetFileName(rootFull)} 共 {map.Count} 项");
        return map;
    }

    public static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按差异类型统计，供界面汇总行使用。</summary>
    public static string Summarize(IReadOnlyList<DiffEntry> entries)
    {
        if (entries.Count == 0) return "两侧完全一致";

        var onlyLeft = entries.Count(e => e.Kind == DiffKind.OnlyLeft);
        var onlyRight = entries.Count(e => e.Kind == DiffKind.OnlyRight);
        var differs = entries.Count(e => e.Kind == DiffKind.ContentDiffers);
        var newerL = entries.Count(e => e.Kind == DiffKind.NewerLeft);
        var newerR = entries.Count(e => e.Kind == DiffKind.NewerRight);

        var parts = new List<string>();
        if (differs > 0) parts.Add($"内容不同 {differs}");
        if (onlyLeft > 0) parts.Add($"仅左侧 {onlyLeft}");
        if (onlyRight > 0) parts.Add($"仅右侧 {onlyRight}");
        if (newerL > 0) parts.Add($"左侧较新 {newerL}");
        if (newerR > 0) parts.Add($"右侧较新 {newerR}");

        return parts.Count == 0 ? "两侧完全一致" : string.Join(" · ", parts);
    }
}
