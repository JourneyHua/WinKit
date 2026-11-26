using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WinKit.Services;

public sealed record AppUpdateRelease(
    Version Version,
    string VersionText,
    string ReleaseNotes,
    DateTimeOffset PublishedAt,
    Uri ReleaseUri,
    Uri DownloadUri,
    string FileName,
    string Sha256,
    long Size);

public sealed record AppUpdateDownload(string FilePath, AppUpdateRelease Release);

public sealed record AppUpdateProgress(long BytesReceived, long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesReceived / TotalBytes * 100, 0, 100);
}

internal static class AppUpdateVersion
{
    private static readonly Regex SemVer = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool TryParse(string? text, out Version version, out bool prerelease)
    {
        version = new Version(0, 0, 0);
        prerelease = false;
        if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
        var match = SemVer.Match(text);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;
        prerelease = match.Groups[4].Success;
        if (prerelease)
        {
            foreach (var part in match.Groups[4].Value.Split('.'))
                if (part.Length > 1 && part[0] == '0' && IsDigits(part)) return false;
        }
        version = new Version(major, minor, patch);
        return true;
    }

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static string StableText(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0 || version.Revision > 0)
            throw new ArgumentException("更新版本必须是 major.minor.patch，第四段只能为 0。", nameof(version));
        return FormattableString.Invariant($"{version.Major}.{version.Minor}.{version.Build}");
    }

    private static bool IsDigits(string value)
    {
        foreach (var character in value)
            if (character is < '0' or > '9') return false;
        return true;
    }
}
