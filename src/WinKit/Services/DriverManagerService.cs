using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinKit.Core;

namespace WinKit.Services;

public sealed class DriverPackageItem : ObservableObject
{
    public string PublishedName { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string Provider { get; init; } = "";
    public string ClassTitle { get; init; } = "";
    public string ClassGuid { get; init; } = "";
    public string Version { get; init; } = "";
    public string DateText { get; init; } = "";
    public string Signer { get; init; } = "";
    public bool IsCritical { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(OriginalName)
        ? PublishedName
        : $"{OriginalName}（{PublishedName}）";

    public string VersionAndDate => string.IsNullOrWhiteSpace(DateText)
        ? Version
        : $"{Version} · {DateText}";

    public string RiskText => IsCritical ? "核心驱动" : "普通外设";

    public string SearchBlob =>
        $"{PublishedName} {OriginalName} {Provider} {ClassTitle} {Version} {Signer}".ToLowerInvariant();
}

public sealed class DriverBackupEntry
{
    public string FolderPath { get; set; } = "";
    public string PublishedName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ClassTitle { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime BackupTimeUtc { get; set; } = DateTime.UtcNow;

    public string TimeText => BackupTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Summary => $"{OriginalName} ({PublishedName}) · {Provider} · v{Version}";
}

public sealed class WindowsDriverUpdateItem : ObservableObject
{
    public string UpdateId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string DriverClass { get; init; } = "";
    public string Description { get; init; } = "";
    public object? ComUpdateObject { get; init; }

    public string DetailText => string.IsNullOrWhiteSpace(Manufacturer) && string.IsNullOrWhiteSpace(Model)
        ? Description
        : $"{Manufacturer} {Model}".Trim();
}

public sealed class DriverManagerService
{
    private static readonly Dictionary<string, (string Title, bool Critical)> KnownClassGuids =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["{4d36e968-e325-11ce-bfc1-08002be10318}"] = ("显示适配器（显卡）", true),
            ["{4d36e972-e325-11ce-bfc1-08002be10318}"] = ("网络适配器（网卡）", true),
            ["{4d36e96c-e325-11ce-bfc1-08002be10318}"] = ("声音、视频和游戏控制器", false),
            ["{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}"] = ("音频输入和输出", false),
            ["{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"] = ("蓝牙", false),
            ["{4d36e97b-e325-11ce-bfc1-08002be10318}"] = ("存储控制器", true),
            ["{4d36e96a-e325-11ce-bfc1-08002be10318}"] = ("磁盘控制器 (IDE/ATA)", true),
            ["{4d36e967-e325-11ce-bfc1-08002be10318}"] = ("磁盘驱动器", true),
            ["{4d36e97d-e325-11ce-bfc1-08002be10318}"] = ("系统设备 / 芯片组", true),
            ["{36fc9e60-c465-11cf-8056-444553540000}"] = ("通用串行总线控制器 (USB)", true),
            ["{4d36e96f-e325-11ce-bfc1-08002be10318}"] = ("鼠标和其他指针设备", true),
            ["{4d36e96b-e325-11ce-bfc1-08002be10318}"] = ("键盘", true),
            ["{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"] = ("人体学输入设备 (HID)", false),
            ["{4d36e979-e325-11ce-bfc1-08002be10318}"] = ("打印队列 / 打印机", false),
            ["{6bdd1fc6-810f-11d0-bec7-08002be2092f}"] = ("图像设备 / 摄像头", false),
            ["{4d36e96e-e325-11ce-bfc1-08002be10318}"] = ("监视器", false),
            ["{5c4c3332-344d-483c-8739-259e934c9cc8}"] = ("软件组件", false),
            ["{62f9c741-b25a-46ce-b54c-9bccce08b6f2}"] = ("软件设备", false),
            ["{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}"] = ("固件", true),
            ["{88bae032-5a81-49f0-bc3d-a4ff138216d6}"] = ("通用串行总线设备", false),
            ["{50127dc3-0f36-415e-a6cc-4cb3be910b65}"] = ("处理器", true)
        };

    public static string DriverBackupRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinKit", "DriverBackups");

    public DriverManagerService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(DriverBackupRoot);
    }

    private static Encoding GetPnpEncoding() => AppManagerService.GetOemEncoding();

    public (List<DriverPackageItem> Drivers, string? Error) LoadDrivers()
    {
        try
        {
            var enc = GetPnpEncoding();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = enc,
                    StandardErrorEncoding = enc
                }
            };

            process.Start();

            // 必须先异步读流再等待退出：同步 ReadToEnd 会一直阻塞到进程退出，
            // 后面的 WaitForExit(15000) 永远没机会生效；stdout/stderr 顺序读还有管道互锁风险。
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch (Exception) { }
                return (new List<DriverPackageItem>(), "pnputil /enum-drivers 超过 15 秒未返回，已终止");
            }

            var output = outTask.GetAwaiter().GetResult();
            var err = errTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                return (new List<DriverPackageItem>(), string.IsNullOrWhiteSpace(err) ? "调用 pnputil 失败" : err);

            return (ParsePnpUtilDrivers(output), null);
        }
        catch (Exception ex)
        {
            return (new List<DriverPackageItem>(), ex.Message);
        }
    }

    public static List<DriverPackageItem> ParsePnpUtilDrivers(string rawOutput)
    {
        var result = new List<DriverPackageItem>();
        if (string.IsNullOrWhiteSpace(rawOutput)) return result;

        var blocks = Regex.Split(rawOutput, @"(\r?\n){2,}");
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;

            var lines = block
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Contains(':'))
                .ToList();

            if (lines.Count < 3) continue;

            string published = "";
            string original = "";
            string provider = "";
            string className = "";
            string classGuid = "";
            string version = "";
            string dateText = "";
            string signer = "";

            var kvList = new List<(string Key, string Value)>();
            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var k = line[..idx].Trim();
                var v = line[(idx + 1)..].Trim();
                kvList.Add((k, v));
            }

            foreach (var (k, v) in kvList)
            {
                if (string.IsNullOrWhiteSpace(published) && Regex.IsMatch(v, @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
                {
                    published = v;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(original)
                    && v.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                    && !v.Equals(published, StringComparison.OrdinalIgnoreCase))
                {
                    original = v;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(classGuid) && Regex.IsMatch(v, @"^\{[0-9a-fA-F\-]{36}\}$"))
                {
                    classGuid = v;
                    continue;
                }

                var verMatch = Regex.Match(v, @"^(\d{1,2}/\d{1,2}/\d{4})\s+(.+)$");
                if (verMatch.Success && string.IsNullOrWhiteSpace(version))
                {
                    dateText = verMatch.Groups[1].Value.Trim();
                    version = verMatch.Groups[2].Value.Trim();
                    continue;
                }

                if (k.Contains("提供程序", StringComparison.OrdinalIgnoreCase)
                    || k.Contains("Provider", StringComparison.OrdinalIgnoreCase))
                {
                    provider = v;
                }
                else if (k.Contains("类名", StringComparison.OrdinalIgnoreCase)
                         || k.Contains("Class Name", StringComparison.OrdinalIgnoreCase)
                         || k.Equals("Class", StringComparison.OrdinalIgnoreCase))
                {
                    className = v;
                }
                else if (k.Contains("签名", StringComparison.OrdinalIgnoreCase)
                         || k.Contains("Signer", StringComparison.OrdinalIgnoreCase))
                {
                    signer = v;
                }
            }

            if (string.IsNullOrWhiteSpace(published)) continue;

            // 位置兜底：第 3 行通常是 Provider，第 4 行是 ClassName，最后一行是 Signer
            if (string.IsNullOrWhiteSpace(provider) && kvList.Count >= 3)
                provider = kvList[2].Value;
            if (string.IsNullOrWhiteSpace(className) && kvList.Count >= 4)
                className = kvList[3].Value;
            if (string.IsNullOrWhiteSpace(signer) && kvList.Count >= 7)
                signer = kvList[^1].Value;

            var isCritical = false;
            var resolvedClassTitle = className;

            if (!string.IsNullOrWhiteSpace(classGuid) && KnownClassGuids.TryGetValue(classGuid, out var known))
            {
                resolvedClassTitle = known.Title;
                isCritical = known.Critical;
            }
            else if (string.IsNullOrWhiteSpace(resolvedClassTitle))
            {
                resolvedClassTitle = "其他设备";
            }

            result.Add(new DriverPackageItem
            {
                PublishedName = published,
                OriginalName = original,
                Provider = string.IsNullOrWhiteSpace(provider) ? "未知厂商" : provider,
                ClassTitle = resolvedClassTitle,
                ClassGuid = classGuid,
                Version = version,
                DateText = dateText,
                Signer = string.IsNullOrWhiteSpace(signer) ? "未签名" : signer,
                IsCritical = isCritical
            });
        }

        return result
            .OrderBy(d => d.ClassTitle, StringComparer.CurrentCulture)
            .ThenBy(d => d.Provider, StringComparer.CurrentCulture)
            .ThenBy(d => d.OriginalName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 卸载前自动导出驱动包到 %LOCALAPPDATA%\WinKit\DriverBackups。
    /// </summary>
    public async Task<(bool Success, string BackupFolder, string? Error)> BackupDriverAsync(DriverPackageItem driver)
    {
        var safeOrig = Path.GetFileNameWithoutExtension(driver.OriginalName);
        if (string.IsNullOrWhiteSpace(safeOrig)) safeOrig = "driver";
        safeOrig = string.Concat(safeOrig.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        var folderName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileNameWithoutExtension(driver.PublishedName)}_{safeOrig}";
        var targetDir = Path.Combine(DriverBackupRoot, folderName);
        Directory.CreateDirectory(targetDir);

        // 大驱动包（显卡可达 1 GB）导出很慢，给足 10 分钟；超时被强杀时目录里可能已有半截 .inf，
        // 所以成功判定必须以退出码为准，不能只看目录里有没有 .inf。
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "pnputil.exe",
            $"/export-driver {driver.PublishedName} \"{targetDir}\"",
            TimeSpan.FromMinutes(10),
            GetPnpEncoding());

        var exportedInf = Directory.EnumerateFiles(targetDir, "*.inf", SearchOption.AllDirectories).ToList();

        if (code != 0)
        {
            try { Directory.Delete(targetDir, true); } catch (Exception) { }
            return (false, "", string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim());
        }

        // 退出码为 0 但一个 .inf 都没有，同样不能当作备份成功
        if (exportedInf.Count == 0)
        {
            try { Directory.Delete(targetDir, true); } catch (Exception) { }
            return (false, "", "pnputil 报告成功，但导出目录里没有 .inf 文件，已丢弃该备份以避免误判");
        }

        var meta = new DriverBackupEntry
        {
            FolderPath = targetDir,
            PublishedName = driver.PublishedName,
            OriginalName = driver.OriginalName,
            Provider = driver.Provider,
            ClassTitle = driver.ClassTitle,
            Version = driver.Version,
            BackupTimeUtc = DateTime.UtcNow
        };

        File.WriteAllText(
            Path.Combine(targetDir, "winkit-driver-backup.json"),
            JsonSerializer.Serialize(meta));

        return (true, targetDir, null);
    }

    public async Task<(bool Success, bool InUseNeedsForce, string Message)> UninstallDriverAsync(
        DriverPackageItem driver,
        bool force = false)
    {
        var forceArg = force ? " /force" : "";
        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "pnputil.exe",
            $"/delete-driver {driver.PublishedName} /uninstall{forceArg}",
            TimeSpan.FromMinutes(2),
            GetPnpEncoding());

        var combined = $"{output}\n{err}".Trim();
        if (code == 0)
            return (true, false, $"已卸载驱动包：{driver.DisplayTitle}");

        var inUse = !force && (combined.Contains("currently in use", StringComparison.OrdinalIgnoreCase)
                               || combined.Contains("正在使用", StringComparison.OrdinalIgnoreCase)
                               || combined.Contains("一个或多个设备", StringComparison.OrdinalIgnoreCase)
                               || combined.Contains("one or more devices", StringComparison.OrdinalIgnoreCase));

        return (false, inUse, string.IsNullOrWhiteSpace(combined) ? $"卸载失败（错误码 {code}）" : combined);
    }

    public async Task<(bool Success, string Message)> InstallInfAsync(string infOrFolderPath)
    {
        string targetArg;
        if (Directory.Exists(infOrFolderPath))
        {
            targetArg = $"\"{Path.Combine(infOrFolderPath, "*.inf")}\" /subdirs /install";
        }
        else if (File.Exists(infOrFolderPath))
        {
            targetArg = $"\"{infOrFolderPath}\" /install";
        }
        else
        {
            return (false, $"路径不存在：{infOrFolderPath}");
        }

        var (code, output, err) = await AppManagerService.RunProcessAsync(
            "pnputil.exe",
            $"/add-driver {targetArg}",
            TimeSpan.FromMinutes(3));

        var combined = $"{output}\n{err}".Trim();
        return code == 0
            ? (true, string.IsNullOrWhiteSpace(combined) ? "驱动包安装成功" : combined)
            : (false, string.IsNullOrWhiteSpace(combined) ? $"驱动安装失败（错误码 {code}）" : combined);
    }

    public async Task<(bool Success, string Message)> ScanHardwareChangesAsync()
    {
        var (code, _, err) = await AppManagerService.RunProcessAsync(
            "pnputil.exe",
            "/scan-devices",
            TimeSpan.FromSeconds(45));

        return code == 0
            ? (true, "已完成硬件改动扫描")
            : (false, string.IsNullOrWhiteSpace(err) ? $"扫描失败（错误码 {code}）" : err.Trim());
    }

    public List<DriverBackupEntry> ListBackups()
    {
        var list = new List<DriverBackupEntry>();
        if (!Directory.Exists(DriverBackupRoot)) return list;

        foreach (var dir in Directory.GetDirectories(DriverBackupRoot))
        {
            var metaFile = Path.Combine(dir, "winkit-driver-backup.json");
            if (File.Exists(metaFile))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<DriverBackupEntry>(File.ReadAllText(metaFile));
                    if (meta is not null)
                    {
                        meta.FolderPath = dir;
                        list.Add(meta);
                        continue;
                    }
                }
                catch (Exception)
                {
                    // 解析失败走文件夹名兜底
                }
            }

            if (Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Any())
            {
                var info = new DirectoryInfo(dir);
                list.Add(new DriverBackupEntry
                {
                    FolderPath = dir,
                    PublishedName = info.Name,
                    OriginalName = info.Name,
                    Provider = "本地备份",
                    Version = "—",
                    BackupTimeUtc = info.CreationTimeUtc
                });
            }
        }

        return list.OrderByDescending(b => b.BackupTimeUtc).ToList();
    }

    /// <summary>
    /// 通过 Windows Update COM 接口搜索微软官方 WHQL 驱动更新（IsInstalled=0 and Type='Driver'）。
    /// </summary>
    public Task<(List<WindowsDriverUpdateItem> Updates, string? Error)> SearchWindowsDriverUpdatesAsync()
    {
        return Task.Run(() =>
        {
            var list = new List<WindowsDriverUpdateItem>();
            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType is null)
                    return (list, "系统未提供 Microsoft.Update.Session 接口");

                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                dynamic updates = searchResult.Updates;
                int count = (int)updates.Count;

                for (var i = 0; i < count; i++)
                {
                    dynamic u = updates.Item(i);
                    string title = "";
                    string desc = "";
                    string mfg = "";
                    string model = "";
                    string cls = "";
                    string id = "";

                    try { title = (string)u.Title; } catch (Exception) { }
                    try { desc = (string)u.Description; } catch (Exception) { }
                    try { mfg = (string)u.DriverManufacturer; } catch (Exception) { }
                    try { model = (string)u.DriverModel; } catch (Exception) { }
                    try { cls = (string)u.DriverClass; } catch (Exception) { }
                    try { id = (string)u.Identity.UpdateID; } catch (Exception) { }

                    list.Add(new WindowsDriverUpdateItem
                    {
                        UpdateId = id,
                        Title = string.IsNullOrWhiteSpace(title) ? $"{mfg} {model}".Trim() : title,
                        Manufacturer = mfg,
                        Model = model,
                        DriverClass = cls,
                        Description = desc,
                        ComUpdateObject = u
                    });
                }

                return (list, (string?)null);
            }
            catch (Exception ex)
            {
                return (list, $"在线扫描驱动更新失败：{ex.Message}");
            }
        });
    }

    public Task<(bool Success, string Message)> InstallWindowsDriverUpdatesAsync(
        IReadOnlyList<WindowsDriverUpdateItem> items,
        Action<string>? progress = null)
    {
        return Task.Run(() =>
        {
            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                if (sessionType is null || collType is null)
                    return (false, "系统 Windows Update 组件不可用");

                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic toDownload = Activator.CreateInstance(collType)!;

                foreach (var item in items)
                {
                    if (item.ComUpdateObject is null) continue;
                    toDownload.Add(item.ComUpdateObject);
                }

                if ((int)toDownload.Count == 0)
                    return (false, "没有可安装的官方驱动更新项");

                progress?.Invoke($"正在通过 Windows Update 下载 {(int)toDownload.Count} 个官方驱动包…");
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = toDownload;
                dynamic dlResult = downloader.Download();
                int dlCode = (int)dlResult.ResultCode; // 2 = Succeeded, 3 = SucceededWithErrors
                if (dlCode is not (2 or 3))
                    return (false, $"驱动下载未完成（状态码 {dlCode}）");

                progress?.Invoke("下载完成，正在安装官方驱动更新…");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = toDownload;
                dynamic instResult = installer.Install();
                int instCode = (int)instResult.ResultCode;
                bool rebootRequired = false;
                try { rebootRequired = (bool)instResult.RebootRequired; } catch (Exception) { }

                if (instCode is 2 or 3)
                {
                    var suffix = rebootRequired ? "（部分驱动需重启电脑后完全生效）" : "";
                    return (true, $"成功安装 {(int)toDownload.Count} 个官方驱动更新{suffix}");
                }

                return (false, $"驱动安装未完成（状态码 {instCode}）");
            }
            catch (Exception ex)
            {
                return (false, $"安装官方驱动更新失败：{ex.Message}");
            }
        });
    }
}
