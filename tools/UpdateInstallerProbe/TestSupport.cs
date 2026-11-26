using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinKit.Services;

namespace WinKit.UpdateInstallerProbe;

internal static class Assert
{
    internal static int Count { get; private set; }
    internal static void True(bool value, string message)
    {
        Count++;
        if (!value) throw new InvalidOperationException(message);
    }
    internal static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}; expected {expected}, actual {actual}");
    internal static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { Count++; return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} but operation succeeded.");
    }
    internal static void Blocked(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        { Count++; return; }
        throw new InvalidOperationException("A pinned file or directory unexpectedly allowed mutation.");
    }
    internal static async Task RejectedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        { Count++; return; }
        throw new InvalidOperationException("The unsafe transaction unexpectedly succeeded.");
    }
}

internal sealed record Fixture(string Path, Version Version, string Hash, long Size)
{
    internal static async Task<Fixture> LoadAsync(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        await using var file = File.OpenRead(path);
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        Assert.True(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64
            && pe.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus && !pe.PEHeaders.IsDll,
            "--fixture must be the published AMD64 self-contained WinKit.exe.");
        var info = FileVersionInfo.GetVersionInfo(path);
        Assert.True(info.FilePrivatePart == 0 && info.ProductPrivatePart == 0,
            "The fixture must have a zero fourth version component.");
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        Assert.True(info.ProductMajorPart == version.Major && info.ProductMinorPart == version.Minor
            && info.ProductBuildPart == version.Build, "FileVersion and ProductVersion must agree.");
        file.Position = 0;
        return new(path, version, Convert.ToHexString(await SHA256.HashDataAsync(file)), file.Length);
    }
    internal AppUpdateRelease Release(Version? version = null)
    {
        version ??= Version;
        var text = version.ToString(3);
        var name = $"WinKit-v{text}-win-x64.exe";
        return new(version, text, "Isolated local transaction fixture", DateTimeOffset.UnixEpoch,
            new Uri($"https://github.com/JourneyHua/WinKit/releases/tag/v{text}"),
            new Uri($"https://github.com/JourneyHua/WinKit/releases/download/v{text}/{name}"), name, Hash, Size);
    }
    internal static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }
}

internal sealed class World : IDisposable
{
    private readonly List<string> _junctions = [];
    private readonly string _parent;
    internal string Root { get; }
    internal string AppDirectory { get; }
    internal string PackageDirectory { get; }
    internal string Target { get; }
    internal string Package { get; }
    internal byte[] Original { get; } = Encoding.UTF8.GetBytes("Original isolated application bytes. These bytes must survive every failed transaction.");
    internal string OldHash => Convert.ToHexString(SHA256.HashData(Original));
    internal Fixture Fixture { get; }
    internal AppUpdateRelease Release { get; }
    internal World(string parent, Fixture fixture)
    {
        _parent = Path.GetFullPath(parent);
        Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        AppDirectory = Path.Combine(Root, "original app 中文");
        PackageDirectory = Path.Combine(Root, "download");
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(PackageDirectory);
        Target = Path.Combine(AppDirectory, "My renamed WinKit 中文.exe");
        Package = Path.Combine(PackageDirectory, "candidate.exe");
        File.WriteAllBytes(Target, Original);
        File.Copy(fixture.Path, Package);
        Fixture = fixture;
        Release = fixture.Release();
    }
    internal Task<AppUpdateInstallerTransaction> PrepareAsync(CancellationToken ct = default,
        string? target = null, string? package = null, string? oldHash = null, AppUpdateRelease? release = null) =>
        AppUpdateInstallerTransaction.PrepareAsync(target ?? Target, oldHash ?? OldHash,
            package ?? Package, release ?? Release, Environment.ProcessId, 0, ct);
    internal async Task AssertOriginalAsync()
    {
        Assert.True(File.Exists(Target), "Failure removed the original pathname.");
        Assert.Equal(OldHash, await WinKit.UpdateInstallerProbe.Fixture.HashAsync(Target), "Failure changed the original contents");
    }
    internal void AssertClean() => Assert.Equal(0,
        Directory.EnumerateFiles(AppDirectory, ".winkit-update-*", SearchOption.TopDirectoryOnly).Count(),
        "Transaction temporary/protection files must be cleaned");
    internal string[] TemporaryFiles() => Directory.GetFiles(AppDirectory, ".winkit-update-*");
    internal string JunctionTo(string directory)
    {
        var alias = Path.Combine(Root, "junction-" + Guid.NewGuid().ToString("N"));
        NativeLinks.CreateJunction(alias, directory);
        _junctions.Add(alias);
        return alias;
    }
    public void Dispose()
    {
        Assert.True(string.Equals(Path.GetDirectoryName(Root), _parent, StringComparison.OrdinalIgnoreCase),
            "Cleanup must stay inside this probe's GUID root.");
        foreach (var junction in _junctions) if (Directory.Exists(junction)) Directory.Delete(junction);
        Directory.Delete(Root, recursive: true);
    }
}

// Test fixtures only: create links entirely inside an owned GUID directory. No ACL edits,
// external processes, shell commands, privileges, downloaded code or user files are involved.
internal static class NativeLinks
{
    internal static void HardLink(string link, string existing)
    {
        if (!CreateHardLink(link, existing, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void CreateJunction(string path, string destination)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(destination));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length,
            IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
