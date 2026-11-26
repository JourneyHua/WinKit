using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinKit.Services;

// These helpers deliberately do not use WPF, shell scripts, ACL mutations or process termination.
internal static class AppUpdateInstallerNative
{
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint ReadAttributes = 0x80;

    internal sealed class ProcessIdentity : IDisposable
    {
        internal SafeProcessHandle Handle { get; }
        internal int Pid { get; }
        internal long StartedUtcTicks { get; }
        internal string Path { get; }
        internal bool HasExited => WaitForSingleObject(Handle, 0) switch
        {
            0 => true,
            258 => false,
            _ => throw new Win32Exception(Marshal.GetLastWin32Error())
        };

        internal ProcessIdentity(int pid, long? expectedStart = null)
            : this(OpenProcess(0x00101000, false, pid), pid, expectedStart) { }

        // ShellExecute can return a valid handle across a credential-UAC boundary even when opening
        // that PID again would be denied by the administrator account's process DACL.
        internal ProcessIdentity(Process child)
            : this(DuplicateProcessHandle(child.SafeHandle), child.Id, null) { }

        private ProcessIdentity(SafeProcessHandle handle, int expectedPid, long? expectedStart)
        {
            Handle = handle;
            if (Handle.IsInvalid) { Handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var actualPid = GetProcessId(Handle);
            if (actualPid == 0 || actualPid != (uint)expectedPid)
            { Handle.Dispose(); throw new InvalidDataException("更新进程的固定句柄身份不符。"); }
            Pid = expectedPid;
            try
            {
                if (!GetProcessTimes(Handle, out var created, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                StartedUtcTicks = DateTime.FromFileTimeUtc(created).Ticks;
                if (expectedStart.HasValue && StartedUtcTicks != expectedStart.Value)
                    throw new InvalidDataException("更新进程身份已变化，请重新开始更新。");
                var buffer = new StringBuilder(32768);
                var count = buffer.Capacity;
                if (!QueryFullProcessImageName(Handle, 0, buffer, ref count))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Path = NormalizePath(buffer.ToString());
                if (HasExited) throw new InvalidDataException("更新进程已经退出。");
            }
            catch { Handle.Dispose(); throw; }
        }

        public void Dispose() => Handle.Dispose();
    }

    private static SafeProcessHandle DuplicateProcessHandle(SafeProcessHandle source)
    {
        if (!DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return duplicate;
    }

    internal static async Task WaitForExitAsync(ProcessIdentity process, CancellationToken ct)
    {
        while (!process.HasExited) await Task.Delay(100, ct).ConfigureAwait(false);
    }

    internal static void ShowUpdateFailure(string target, IReadOnlyList<string> protectionPaths, bool confirmedPaths)
    {
        var text = new StringBuilder("WinKit 更新未能完成，且无法安全地恢复原窗口。\n\n");
        text.Append("原程序路径：\n").Append(target).Append("\n\n");
        if (protectionPaths.Count != 0)
        {
            text.Append(confirmedPaths ? "本次更新保留的原文件保护副本：\n" : "请在以下本次事务位置查找原文件保护副本（助手状态未能确认）：\n");
            foreach (var path in protectionPaths) text.Append(path).Append('\n');
            text.Append('\n');
        }
        text.Append("检查目录：\n").Append(System.IO.Path.GetDirectoryName(target))
            .Append("\n\n请等待仍在运行的更新助手结束，再检查原路径。未执行路径中不明的文件，也未终止其它进程。")
            .Append("\n按 Ctrl+C 可复制本窗口的完整文字及路径。");
        _ = MessageBox(IntPtr.Zero, text.ToString(), "WinKit 更新未完成", 0x10 | 0x10000 | 0x40000);
    }

    internal readonly record struct FileIdentity(uint Volume, ulong Index, long Length);

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 30000 ||
            path.StartsWith(@"\", StringComparison.Ordinal) || path.Contains('/'))
            throw new InvalidDataException("更新仅支持本地磁盘上的普通文件路径。");
        var full = System.IO.Path.GetFullPath(path);
        if (full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' ||
            full.AsSpan(2).Contains(':'))
            throw new InvalidDataException("更新文件路径无效。");
        if (full.Length == 3) return full;
        foreach (var part in full[3..].Split('\\'))
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(['<', '>', '|', '"', '*', '?']) >= 0)
                throw new InvalidDataException("更新文件路径无效。");
        return full;
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static string HandlePath(SafeFileHandle handle)
    {
        var text = new StringBuilder(32768);
        var size = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        if (size == 0 || size >= text.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var result = text.ToString();
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        return NormalizePath(result);
    }

    internal static FileIdentity Inspect(FileStream stream, string path, bool allowHardLink = false)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var actualPath = HandlePath(stream.SafeFileHandle);
        if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 ||
            (!allowHardLink && info.Links != 1) || !SamePath(actualPath, path))
        {
            var failure = new InvalidDataException("更新拒绝链接、重解析点或身份不明确的文件。");
            failure.Data["ExpectedPath"] = path;
            failure.Data["HandlePath"] = actualPath;
            failure.Data["Attributes"] = info.Attributes.ToString("X8");
            failure.Data["Links"] = info.Links;
            throw failure;
        }
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((long)info.SizeHigh << 32) | info.SizeLow);
    }

    internal static FileStream OpenReadPinned(string path, bool allowDelete = false)
    {
        // OPEN_REPARSE_POINT checks the object itself; inspecting attributes before File.Open is racy.
        var handle = CreateFile(path, 0x80000000, allowDelete ? 1u | 4u : 1u,
            IntPtr.Zero, OpenExisting, OpenReparsePoint | 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var stream = new FileStream(handle, FileAccess.Read, 128 * 1024, false);
            try { _ = Inspect(stream, path); return stream; }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream OpenForRename(string path)
    {
        var handle = CreateFile(path, 0x80000000 | 0x10000, 1, IntPtr.Zero,
            OpenExisting, OpenReparsePoint | 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var file = new FileStream(handle, FileAccess.Read, 128 * 1024, false);
            try { _ = Inspect(file, path); return file; }
            catch { file.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream CreateNewPinned(string path)
    {
        var handle = CreateFile(path, 0x80000000 | 0x40000000 | 0x10000, 1, IntPtr.Zero,
            1, OpenReparsePoint | 0x80000000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            var file = new FileStream(handle, FileAccess.ReadWrite, 128 * 1024, false);
            try { _ = Inspect(file, path); return file; }
            catch { file.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void DeletePinned(FileStream file)
    {
        var flags = 1u; // FILE_DISPOSITION_DELETE; never bypass an active image or another process's sharing
        if (!SetFileInformationByHandle(file.SafeFileHandle, 21, ref flags, sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void RenamePinned(FileStream source, string destination)
    {
        var name = Encoding.Unicode.GetBytes(destination);
        var nameOffset = IntPtr.Size == 8 ? 20 : 12;
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        // The Win32 API normalizes a full DOS path before issuing the native rename. Supply a
        // terminated WCHAR string as well as FileNameLength; otherwise that normalization reads
        // past the buffer and can append arbitrary heap data to the destination filename.
        var bufferSize = nameOffset + name.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, 0); // Never replace a destination entry; concurrent injection must fail closed.
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, rootOffset + IntPtr.Size, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            Marshal.WriteInt16(buffer, nameOffset + name.Length, 0);
            if (!SetFileInformationBuffer(source.SafeFileHandle, 22, buffer, bufferSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal DirectoryPins(string filePath)
        {
            var directory = System.IO.Path.GetDirectoryName(NormalizePath(filePath))!;
            var paths = new Stack<string>();
            for (var current = directory; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
                paths.Push(current);
            try
            {
                while (paths.TryPop(out var path))
                {
                    var handle = CreateFile(path, ReadAttributes, 1 | 2, IntPtr.Zero,
                        OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    _handles.Add(handle); // no FILE_SHARE_DELETE: ancestors cannot be renamed/relinked underneath us
                    if (!GetFileInformationByHandle(handle, out var info) ||
                        (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                        !SamePath(HandlePath(handle).TrimEnd('\\'), path.TrimEnd('\\')))
                        throw new InvalidDataException("更新拒绝包含链接或重解析点的目录。");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    }

    internal static async Task<string> HashAsync(FileStream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash);
    }

    internal static int PipePeerPid(SafePipeHandle handle, bool server)
    {
        uint id;
        var ok = server ? GetNamedPipeClientProcessId(handle, out id) : GetNamedPipeServerProcessId(handle, out id);
        if (!ok || id == 0 || id > int.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)id;
    }

    internal static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!GetTokenInformation(token, 20, out var value, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return value != 0;
        }
    }

    // AccessCheck does not open the running image for DELETE (which can be denied merely because it is mapped).
    // Actual create/flush/delete of the staging file follows this check, before telling the UI it may close.
    internal static bool CanReplace(string target)
    {
        var directory = System.IO.Path.GetDirectoryName(target)!;
        return HasAccess(directory, 2) && (HasAccess(directory, 0x40) || HasAccess(target, 0x10000));
    }

    private static bool HasAccess(string path, uint access)
    {
        GetFileSecurity(path, 7, null, 0, out var length);
        if (length == 0 || length > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error());
        var descriptor = new byte[length];
        if (!GetFileSecurity(path, 7, descriptor, length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!OpenProcessToken(GetCurrentProcess(), 8 | 2, out var processToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (processToken)
        {
            if (!DuplicateToken(processToken, 2, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200a0, All = 0x1f01ff };
                uint privilegeLength = 4096;
                var privileges = new byte[privilegeLength];
                if (!AccessCheck(descriptor, token, access, ref mapping, privileges, ref privilegeLength, out _, out var allowed))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return allowed;
            }
        }
    }

    internal static void EnsureNoOtherInstances(string target, int parentPid, int coordinatorPid = 0)
    {
        var result = RmStartSession(out var session, 0, new StringBuilder(33));
        if (result != 0) throw new Win32Exception(result);
        try
        {
            result = RmRegisterResources(session, 1, [target], 0, null, 0, null);
            if (result != 0) throw new Win32Exception(result);
            uint needed = 0, count = 0, reasons = 0;
            result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != 0 && result != 234) throw new Win32Exception(result);
            if (needed > 4096) throw new InvalidDataException("无法确认更新文件的占用状态。");
            if (needed != 0)
            {
                var entries = new RmProcessInfo[needed]; count = needed;
                result = RmGetList(session, out needed, ref count, entries, ref reasons);
                if (result != 0) throw new Win32Exception(result);
                for (var i = 0; i < count; i++)
                {
                    var pid = entries[i].Process.Pid;
                    // Our own read-only validation handles are not independent application instances.
                    if (pid != parentPid && pid != Environment.ProcessId && pid != coordinatorPid)
                        throw new IOException("另一个 WinKit 窗口、维护任务或其它程序仍在使用原文件；请先正常退出它，再重试更新。");
                }
            }
        }
        finally { _ = RmEndSession(session); }
    }

    internal static void AtomicMove(string from, string to)
    {
        // Renaming the source directory entry replaces a destination link itself; it does not open/follow it.
        // Both paths are generated in one pinned directory, so no cross-volume fallback/copy is possible.
        if (!MoveFileEx(from, to, 1 | 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void MarkOwnImageForDeletion(string path)
    {
        // POSIX disposition marks this exact open file object for unlink when its last image reference closes.
        using var handle = CreateFile(path, 0x10000 | ReadAttributes, 1 | 2 | 4, IntPtr.Zero,
            OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return;
        if (!SamePath(HandlePath(handle), path) || !GetFileInformationByHandle(handle, out var info) ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || info.Links != 1) return;
        var flags = 1u | 2u; // FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS
        _ = SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInfo
    {
        public uint Attributes; public long Created; public long Accessed; public long Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping { public uint Read, Write, Execute, All; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out SafeProcessHandle target, uint access, bool inherit, uint options);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string title, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint count, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateToken(SafeAccessTokenHandle original, int level, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int info, int length, out int returned);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetFileSecurity(string file, uint information, byte[]? descriptor, uint length, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle token, uint desired, ref GenericMapping mapping, byte[] privileges, ref uint length, out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool allowed);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount, RmUniqueProcess[]? apps, uint serviceCount, string[]? services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? apps, ref uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existing, string? replacement, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref uint information, int size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] private static extern bool SetFileInformationBuffer(SafeFileHandle handle, int kind, IntPtr information, int size);
}
