using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using static WinKit.Services.AppUpdateInstallerNative;

namespace WinKit.Services;

internal sealed record AppUpdateInstallerMessage
{
    public string Kind { get; init; } = "";
    public string Capability { get; init; } = "";
    public int Pid { get; init; }
    public long StartedUtcTicks { get; init; }
    public int ParentPid { get; init; }
    public long ParentStartedUtcTicks { get; init; }
    public string Target { get; init; } = "";
    public string OriginalHash { get; init; } = "";
    public string PackagePath { get; init; } = "";
    public string VersionText { get; init; } = "";
    public string Code { get; init; } = "";
    public string TransactionId { get; init; } = "";
    public string[] RecoveryPaths { get; init; } = [];
}

internal sealed class AppUpdateInstallerPipe : IDisposable
{
    private const int MaximumFrame = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly PipeStream _stream;
    private readonly string _capability;
    internal SafePipeHandle Handle => _stream.SafePipeHandle;
    internal AppUpdateInstallerPipe(PipeStream stream, string capability)
    { _stream = stream; _capability = capability; }

    internal async Task SendAsync(AppUpdateInstallerMessage message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message with { Capability = _capability }, JsonOptions);
        if (body.Length > MaximumFrame) throw new InvalidDataException("更新握手数据过长。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
        await _stream.WriteAsync(body, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal async Task<AppUpdateInstallerMessage?> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        var first = await _stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (first == 0) return null;
        await _stream.ReadExactlyAsync(header.AsMemory(1), ct).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > MaximumFrame) throw new InvalidDataException("更新握手帧无效。");
        var body = new byte[count];
        await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<AppUpdateInstallerMessage>(body, JsonOptions)
            ?? throw new InvalidDataException("更新握手消息无效。");
        if (!IsHex(message.Capability, 64) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(message.Capability), Convert.FromHexString(_capability)) ||
            message.Kind.Length > 32 || message.Code.Length > 64 || message.VersionText.Length > 128 ||
            message.Target.Length > 30000 || message.PackagePath.Length > 30000 || message.OriginalHash.Length > 64 ||
            message.TransactionId.Length > 32 || message.RecoveryPaths is null || message.RecoveryPaths.Length > 2 ||
            message.RecoveryPaths.Any(path => path is null || path.Length > 30000))
            throw new InvalidDataException("更新握手认证失败。");
        return message;
    }

    internal async Task<AppUpdateInstallerMessage> ExpectAsync(string kind, CancellationToken ct)
    {
        var message = await ReadAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("更新助手在准备完成前已断开。");
        if (message.Kind == "error") throw new IOException(AppUpdateInstaller.MessageForCode(message.Code));
        if (message.Kind != kind) throw new InvalidDataException("更新助手返回了错误的握手顺序。");
        return message;
    }

    public void Dispose() => _stream.Dispose();
    internal static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    internal static string PipeName() => "WinKit.Update.Handshake." + Secret();

    internal static NamedPipeServerStream CreateServer(string name, bool elevation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("更新器命名管道仅支持 Windows。");
        if (!elevation) return new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // A credential UAC may use a different administrator account. Give authenticated local users
        // only pipe IO rights (not FILE_CREATE_PIPE_INSTANCE); PID/image/capability checks remain mandatory.
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前用户。");
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{sid})(A;;GA;;;BA)(A;;0x12019b;;;BU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000 | 0x00080000,
                8, 1, MaximumFrame, MaximumFrame, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { _ = LocalFree(descriptor); }
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(string name, bool elevation, CancellationToken ct)
    {
        if (!elevation)
        {
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            try { await client.ConnectAsync(ct).ConfigureAwait(false); return client; }
            catch { client.Dispose(); throw; }
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var handle = OpenPipe(@"\\.\pipe\" + name, 0x12019b, 0, IntPtr.Zero, 3,
                0x40000000 | 0x00100000 | 0x00010000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try { return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle); }
                catch { handle.Dispose(); throw; }
            }
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is not (2 or 231)) throw new Win32Exception(error);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    internal static async Task AuthenticateClientAsync(NamedPipeServerStream server, Process child,
        string childPath, string hash, CancellationToken ct)
    {
        using var peer = new ProcessIdentity(child); // duplicates the actual Process.Start/ShellExecute handle
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connection = server.WaitForConnectionAsync(waiting.Token);
        var exited = WaitForExitAsync(peer, waiting.Token);
        try
        {
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) != connection)
                throw new IOException("更新助手未能启动；当前窗口会保留。");
            await connection.ConfigureAwait(false);
            if (peer.HasExited || PipePeerPid(server.SafePipeHandle, true) != peer.Pid)
                throw new InvalidDataException("更新助手的实际进程身份不符。");
            if (!SamePath(peer.Path, childPath)) throw new InvalidDataException("更新助手的实际路径不符。");
            using var image = OpenReadPinned(peer.Path);
            if (!string.Equals(await HashAsync(image, ct).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase) || peer.HasExited)
                throw new InvalidDataException("更新助手文件或进程身份已变化。");
        }
        finally
        {
            waiting.Cancel();
            try { await exited.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await connection.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maximum, int outputSize, int inputSize, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle OpenPipe(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}

internal sealed record AppUpdateInstallerArguments(string Role, string PipeName, string Capability,
    int ParentPid, long ParentStartedUtcTicks, string OriginalHash)
{
    internal static bool TryParse(string[] args, out AppUpdateInstallerArguments? result)
    {
        result = null;
        if (args.Length != 7 || args[0] != AppUpdateInstaller.HelperSwitch || args[1] is not ("coordinator" or "applier") ||
            !args[2].StartsWith("WinKit.Update.Handshake.", StringComparison.Ordinal) ||
            !AppUpdateInstallerPipe.IsHex(args[2]["WinKit.Update.Handshake.".Length..], 64) ||
            !AppUpdateInstallerPipe.IsHex(args[3], 64) || !AppUpdateInstallerPipe.IsHex(args[6], 64) ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4 ||
            !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
            return false;
        result = new(args[1], args[2], args[3], pid, ticks, args[6]);
        return true;
    }

    internal void AddTo(ProcessStartInfo start)
    {
        foreach (var value in new[] { AppUpdateInstaller.HelperSwitch, Role, PipeName, Capability,
            ParentPid.ToString(CultureInfo.InvariantCulture), ParentStartedUtcTicks.ToString(CultureInfo.InvariantCulture), OriginalHash })
            start.ArgumentList.Add(value);
    }
}

// Mutex ownership belongs to one dedicated thread, since an async continuation may run on another thread.
internal sealed class AppUpdateInstallerLease : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AppUpdateInstallerLease> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal static string NameFor(string target) => @"Global\WinKit.Update." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));

    internal static async Task<AppUpdateInstallerLease> AcquireAsync(string target, CancellationToken ct)
    {
        var lease = new AppUpdateInstallerLease();
        var thread = new Thread(() => lease.Hold(target)) { IsBackground = true, Name = "WinKit update path guard" };
        thread.Start();
        try { return await lease._started.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch { lease.Dispose(); throw; }
    }

    private void Hold(string target)
    {
        Mutex? mutex = null; var owns = false;
        try
        {
            mutex = new Mutex(false, NameFor(target));
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new IOException("此路径已有更新正在进行，请等待其完成。");
            _started.TrySetResult(this);
            _release.Wait();
        }
        catch (Exception ex) { _started.TrySetException(ex); }
        finally
        {
            if (owns) mutex!.ReleaseMutex();
            mutex?.Dispose(); _released.TrySetResult();
        }
    }

    internal static bool IsHeld(string target)
    {
        try
        {
            if (!Mutex.TryOpenExisting(NameFor(target), out var mutex)) return false;
            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        _released.Task.GetAwaiter().GetResult();
        _release.Dispose();
    }
}
