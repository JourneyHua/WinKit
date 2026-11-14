using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WinKit.Services;

public sealed class ScoopPackage
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string InstallPath { get; init; } = "";
    public bool IsGlobal { get; init; }

    /// <summary>读不到已装版本时不报“可升级”，否则界面会出现「未知 → 26.06」这种假升级。</summary>
    public bool HasUpdate => !string.IsNullOrWhiteSpace(Version)
                             && Version != "版本未知"
                             && !string.IsNullOrWhiteSpace(LatestVersion)
                             && !VersionsEqual(Version, LatestVersion);

    public string VersionText => HasUpdate ? $"{Version} → {LatestVersion}" : Version;
    public string SourceText => IsGlobal ? $"scoop（全局 {Bucket}）" : $"scoop（{Bucket}）";

    internal static bool VersionsEqual(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Scoop 包管理集成：直接读本地目录，不依赖 scoop 命令。
/// 已装版本来自 apps\&lt;name&gt;\current\manifest.json，可升级版本来自 buckets\&lt;bucket&gt;\bucket\&lt;name&gt;.json。
/// 只有真正执行升级时才调用 scoop.exe（需要网络）。
/// </summary>
public sealed class ScoopService
{
    private static readonly string[] ScoopRoots =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"),
        @"C:\ProgramData\scoop"
    };

    public string? Root { get; }
    public bool IsInstalled => Root is not null;

    public ScoopService() : this(null)
    {
    }

    /// <summary>传入 roots 便于自测用临时目录；正常调用走默认的 user / global 两个位置。</summary>
    public ScoopService(IEnumerable<string>? roots)
    {
        var candidates = roots?.ToArray() ?? ScoopRoots;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!Directory.Exists(Path.Combine(candidate, "apps"))) continue;

            Root = candidate;
            break;
        }
    }

    /// <summary>枚举本机 scoop 已安装包，并和本地 bucket 清单比对出可升级版本。</summary>
    public List<ScoopPackage> Load()
    {
        var packages = new List<ScoopPackage>();
        if (Root is null) return packages;

        var appsDir = Path.Combine(Root, "apps");
        if (!Directory.Exists(appsDir)) return packages;

        var latest = LoadBucketVersions();

        foreach (var appDir in Directory.GetDirectories(appsDir))
        {
            var name = Path.GetFileName(appDir);
            var currentDir = Path.Combine(appDir, "current");
            if (!Directory.Exists(currentDir)) continue;

            var (version, bucket) = ReadManifest(currentDir, name);

            // scoop 自身是自举安装的，没有清单文件；读不到版本时如实标注，不猜
            if (string.IsNullOrWhiteSpace(version)) version = "版本未知";

            // bucket 清单里找不到时按当前版本处理（不虚报可升级）
            latest.TryGetValue(name, out var info);
            var latestVersion = info.Version ?? version;
            var bucketName = !string.IsNullOrWhiteSpace(info.Bucket) ? info.Bucket! : bucket;

            packages.Add(new ScoopPackage
            {
                Name = name,
                Version = version,
                LatestVersion = latestVersion,
                Bucket = string.IsNullOrWhiteSpace(bucketName) ? "?" : bucketName,
                InstallPath = currentDir,
                IsGlobal = IsGlobalRoot()
            });
        }

        return packages
            .OrderByDescending(p => p.HasUpdate)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsGlobalRoot()
        => Root is not null &&
           Root.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase);

    private static (string Version, string Bucket) ReadManifest(string currentDir, string name)
    {
        // 两种文件名并存：老包是 manifest.json，新包（7zip/git/go/nodejs 等）是 scoop-manifest.json。
        // 只认前者会让十几个包的版本读成空，进而误报“可升级到空版本”。
        foreach (var fileName in new[] { "scoop-manifest.json", "manifest.json" })
        {
            var path = Path.Combine(currentDir, fileName);
            if (!File.Exists(path)) continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

                // bucket 字段常常是空的，退回读 scoop-install.json 或从安装路径推断
                var bucket = root.TryGetProperty("bucket", out var b) ? b.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(bucket))
                    bucket = ReadInstallBucket(currentDir);

                return (version, bucket);
            }
            catch (Exception)
            {
                // 换下一个文件名再试
            }
        }

        return ("", "");
    }

    /// <summary>scoop-install.json 记录了真正来源的 bucket（比清单里的 bucket 字段可靠）。</summary>
    private static string ReadInstallBucket(string currentDir)
    {
        var installFile = Path.Combine(currentDir, "scoop-install.json");
        if (!File.Exists(installFile)) return "";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(installFile));
            return doc.RootElement.TryGetProperty("bucket", out var b) ? b.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>扫描所有 bucket 的清单文件，得到 name → 最新版本。</summary>
    private Dictionary<string, (string? Version, string? Bucket)> LoadBucketVersions()
    {
        var map = new Dictionary<string, (string? Version, string? Bucket)>(StringComparer.OrdinalIgnoreCase);
        if (Root is null) return map;

        var bucketsDir = Path.Combine(Root, "buckets");
        if (!Directory.Exists(bucketsDir)) return map;

        foreach (var bucketDir in Directory.GetDirectories(bucketsDir))
        {
            var bucketName = Path.GetFileName(bucketDir);
            var manifestDir = Path.Combine(bucketDir, "bucket");
            if (!Directory.Exists(manifestDir)) continue;

            foreach (var file in Directory.GetFiles(manifestDir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (map.ContainsKey(name)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                    map[name] = (version, bucketName);
                }
                catch (Exception)
                {
                    // 损坏的清单跳过
                }
            }
        }

        return map;
    }

    /// <summary>执行 scoop 命令（update/uninstall/cleanup）。需要网络，且 scoop 必须在 PATH 里。</summary>
    public async Task<(bool Ok, string Message)> RunScoopAsync(string arguments, TimeSpan? timeout = null)
    {
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "scoop.cmd",
            arguments,
            timeout ?? TimeSpan.FromMinutes(5),
            AppManagerService.GetOemEncoding());

        var text = string.Join("\n", new[] { output, err }.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
        return code == 0
            ? (true, string.IsNullOrWhiteSpace(text) ? "完成" : text)
            : (false, string.IsNullOrWhiteSpace(text) ? $"scoop 返回失败（错误码 {code}）" : text);
    }

    /// <summary>scoop 是否在 PATH 里可用。</summary>
    public static bool CommandAvailable()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir.Trim(), "scoop.cmd")));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
