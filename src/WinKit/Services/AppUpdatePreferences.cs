using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinKit.Services;

/// <summary>更新器独立偏好；保存失败向调用方抛出，不借用会吞异常的界面设置。网页/下载均不会写入此文件。</summary>
internal sealed class AppUpdatePreferences
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _filePath;
    private readonly string _lockPath;

    internal AppUpdatePreferences(string directory)
    {
        _directory = directory;
        _filePath = Path.Combine(directory, "app-update-preferences.json");
        _lockPath = Path.Combine(directory, "app-update-preferences.lock");
    }

    internal bool AutomaticChecksEnabled
    {
        get
        {
            lock (_sync)
            {
                using var fileLock = AcquireLock();
                return Load().AutomaticChecksEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                using var fileLock = AcquireLock();
                var state = Load();
                if (state.AutomaticChecksEnabled != value)
                    Save(state with { AutomaticChecksEnabled = value });
            }
        }
    }

    internal bool IsAutomaticCheckDue(DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            using var fileLock = AcquireLock();
            return IsDue(Load(), utcNow.ToUniversalTime());
        }
    }

    internal bool TryReserveAutomaticCheck(DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            using var fileLock = AcquireLock();
            var state = Load();
            utcNow = utcNow.ToUniversalTime();
            if (!IsDue(state, utcNow)) return false;
            // 在发网前原子登记尝试时间；失败、取消或新进程都不能造成后台重试风暴。
            Save(state with { LastAutomaticCheckUtc = utcNow });
            return true;
        }
    }

    private static bool IsDue(State state, DateTimeOffset utcNow) =>
        state.AutomaticChecksEnabled
        && (state.LastAutomaticCheckUtc is null
            || utcNow >= state.LastAutomaticCheckUtc.Value
            && utcNow - state.LastAutomaticCheckUtc.Value >= CheckInterval);

    private FileStream AcquireLock()
    {
        AppUpdateStorage.EnsureDirectory(_directory);
        AppUpdateStorage.EnsureRegularFileIfPresent(_lockPath);
        // 不删除锁文件：删除再重建会让两个进程各自持有不同的锁。
        return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private State Load()
    {
        AppUpdateStorage.EnsureRegularFileIfPresent(_filePath);
        if (!File.Exists(_filePath)) return new State(true, null);
        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 16 * 1024)
                throw new InvalidDataException("更新偏好文件大小无效。");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("更新偏好格式无效。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new InvalidDataException("更新偏好含有重复字段。");
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
                || !root.TryGetProperty("automaticChecksEnabled", out var enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("lastAutomaticCheckUtc", out var last))
                throw new InvalidDataException("更新偏好版本或字段无效。");
            DateTimeOffset? lastCheck = null;
            if (last.ValueKind != JsonValueKind.Null)
            {
                if (last.ValueKind != JsonValueKind.String || !last.TryGetDateTimeOffset(out var parsed))
                    throw new InvalidDataException("更新偏好中的检查时间无效。");
                lastCheck = parsed.ToUniversalTime();
            }
            return new State(enabled.GetBoolean(), lastCheck);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("更新偏好文件已损坏；请修复或移除此文件后重试。", exception);
        }
    }

    private void Save(State state)
    {
        var temporaryPath = Path.Combine(_directory, $".app-update-preferences.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new
                {
                    schemaVersion = 1,
                    automaticChecksEnabled = state.AutomaticChecksEnabled,
                    lastAutomaticCheckUtc = state.LastAutomaticCheckUtc
                });
                stream.Flush(flushToDisk: true);
            }
            AppUpdateStorage.EnsureRegularFileIfPresent(_filePath);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            // 临时文件属于本次写入；清理失败也应可见，不能伪装已保存。
            File.Delete(temporaryPath);
        }
    }

    private sealed record State(bool AutomaticChecksEnabled, DateTimeOffset? LastAutomaticCheckUtc);
}

internal static class AppUpdateStorage
{
    internal static void EnsureDirectory(string directory)
    {
        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);
    }

    internal static void EnsureRegularFileIfPresent(string filePath)
    {
        EnsureNoReparsePoints(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        try
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("更新文件位置不能是目录或符号链接。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static void EnsureNoReparsePoints(string directory)
    {
        for (string? current = Path.GetFullPath(directory); !string.IsNullOrEmpty(current);
             current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("更新目录不能经过符号链接或普通文件。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
