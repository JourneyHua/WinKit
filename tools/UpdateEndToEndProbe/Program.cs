using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinKit.Services;

namespace WinKit.UpdateEndToEndProbe;

internal static class Program
{
    private const string RunSwitch = "--run";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (AppUpdateInstaller.IsHelperInvocation(args))
            return await AppUpdateInstaller.RunHelperAsync(args).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("This end-to-end probe requires Windows x64.");
            return 2;
        }
        if (args.Length != 3 || args[0] != RunSwitch)
        {
            Console.Error.WriteLine("Usage: WinKit.exe --run <expected-version> <report-path>");
            return 2;
        }

        try
        {
            var expectedText = args[1];
            if (!AppUpdateVersion.TryParse(expectedText, out var expectedVersion, out var prerelease) || prerelease ||
                AppUpdateVersion.StableText(expectedVersion) != expectedText)
                throw new ArgumentException("Expected version must be a canonical stable major.minor.patch value.");

            var target = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable path."));
            var runRoot = ValidateOwnedTarget(target);
            var reportPath = Path.GetFullPath(args[2]);
            if (!string.Equals(reportPath, Path.Combine(runRoot, "report.json"), StringComparison.OrdinalIgnoreCase) ||
                File.Exists(reportPath))
                throw new InvalidOperationException("Report path must be the unused report.json inside this probe's owned temporary directory.");

            var oldFileVersion = FileVersionInfo.GetVersionInfo(target).ProductVersion;
            if (!Version.TryParse(oldFileVersion?.Split('+')[0], out var oldVersion) || oldVersion >= expectedVersion)
                throw new InvalidOperationException("The isolated probe executable must have a lower version than the requested release.");

            using var runTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            var cancellationToken = runTimeout.Token;
            var oldFileSha256 = await HashFileAsync(target).ConfigureAwait(false);
            var identity = ProbeIdentity.Current();
            using var process = Process.GetCurrentProcess();
            var startedUtc = process.StartTime.ToUniversalTime();
            var storage = Path.Combine(runRoot, "probe-data");
            using var service = new AppUpdateService(storage);
            var release = await service.GetReleaseAsync(expectedVersion, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Official stable release {expectedText} was not found.");
            if (release.VersionText != expectedText)
                throw new InvalidDataException("The official release version differs from the requested version.");

            var download = await service.DownloadAsync(release, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!await service.ValidateDownloadAsync(download, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The production update service did not validate the downloaded release.");

            var installer = new AppUpdateInstaller();
            await using var restart = await installer.PrepareRestartAsync(download, cancellationToken).ConfigureAwait(false);
            if (!restart.Ready)
                throw new InvalidOperationException("The production updater did not complete its Ready handshake.");

            await restart.AllowExitAsync(cancellationToken).ConfigureAwait(false);
            var report = new ProbeReport(
                SchemaVersion: 1,
                ExpectedVersion: expectedText,
                ReleaseVersion: release.VersionText,
                ReleaseUri: release.ReleaseUri.AbsoluteUri,
                AssetName: release.FileName,
                AssetSha256: release.Sha256,
                AssetSize: release.Size,
                DownloadPath: download.FilePath,
                TargetPath: target,
                OldVersion: oldVersion.ToString(3),
                OldFileSha256: oldFileSha256,
                ProcessId: process.Id,
                ProcessStartedUtc: startedUtc,
                UserSid: identity.Sid,
                IntegrityRid: identity.IntegrityRid,
                IntegrityLevel: identity.IntegrityLevel,
                ReadyHandshake: true,
                ExitAllowed: true,
                RunRoot: runRoot,
                ReportPath: reportPath,
                PreparedUtc: DateTimeOffset.UtcNow);
            await WriteReportAsync(reportPath, report).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ValidateOwnedTarget(string target)
    {
        if (!Path.GetFileName(target).Equals("WinKit.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The isolated probe must run as WinKit.exe.");
        var appDirectory = Path.GetDirectoryName(target)!;
        var root = Path.GetDirectoryName(appDirectory)!;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!canonicalRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to update an executable outside the system temporary directory.");
        if (!Path.GetFileName(appDirectory).Equals("app", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(canonicalRoot), temp.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(canonicalRoot), @"\AWinKit-UpdateEndToEndProbe-[0-9a-f]{32}\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidOperationException("The executable is not inside a script-created GUID probe directory.");
        if ((File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(appDirectory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(target) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Probe target paths must be ordinary directories and a regular executable.");
        return canonicalRoot;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static async Task WriteReportAsync(string path, ProbeReport report)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record ProbeReport(
        int SchemaVersion,
        string ExpectedVersion,
        string ReleaseVersion,
        string ReleaseUri,
        string AssetName,
        string AssetSha256,
        long AssetSize,
        string DownloadPath,
        string TargetPath,
        string OldVersion,
        string OldFileSha256,
        int ProcessId,
        DateTime ProcessStartedUtc,
        string UserSid,
        int IntegrityRid,
        string IntegrityLevel,
        bool ReadyHandshake,
        bool ExitAllowed,
        string RunRoot,
        string ReportPath,
        DateTimeOffset PreparedUtc);
}

internal sealed record ProcessTokenIdentity(string Sid, int IntegrityRid, string IntegrityLevel);

internal static class ProbeIdentity
{
    internal static ProcessTokenIdentity Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Current process has no user SID.");
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            _ = GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
            if (length <= 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var sidPointer = Marshal.ReadIntPtr(buffer);
                var count = Marshal.ReadByte(sidPointer, 1);
                if (count == 0) throw new InvalidDataException("Current process integrity SID is invalid.");
                var rid = Marshal.ReadInt32(sidPointer, 8 + (count - 1) * sizeof(int));
                return new ProcessTokenIdentity(sid, rid, IntegrityName(rid));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { _ = CloseHandle(token); }
    }

    private static string IntegrityName(int rid) => rid switch
    {
        >= 0x5000 => "Protected",
        >= 0x4000 => "System",
        >= 0x3000 => "High",
        >= 0x2100 => "MediumPlus",
        >= 0x2000 => "Medium",
        >= 0x1000 => "Low",
        _ => "Untrusted"
    };

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int informationLength, out int returnLength);
}
