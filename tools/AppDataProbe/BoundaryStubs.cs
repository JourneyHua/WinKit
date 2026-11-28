// Source-linked production services exercise real parsing and cache logic.
// These fail-closed external boundaries are never used for system changes.
using WinKit.Core;

namespace WinKit.Core
{
    public sealed class ChangeJournal
    {
        public void Record(string description, (HiveKind Hive, string SubKey)[] tracked, Action change)
            => throw new InvalidOperationException("Registry mutation is forbidden in AppDataProbe.");
    }
    public static class Native
    {
        public static string ResolveIndirectString(string value) => value.StartsWith('@') ? "" : value;
        public static void NotifyAssociationsChanged() => throw new InvalidOperationException("Shell mutation is forbidden.");
        public static void OpenPath(string value) => throw new InvalidOperationException("Launching is forbidden.");
    }
}
namespace WinKit.Services
{
    public static class ContextMenuService
    {
        public static string? ExtractExecutable(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var text = command.Trim();
            if (text.StartsWith('"'))
            {
                var end = text.IndexOf('"', 1);
                return end > 0 ? text[1..end] : null;
            }
            var comma = text.IndexOf(',');
            return comma >= 0 ? text[..comma] : text;
        }
    }
    public static class ThisPcService
    {
        public static string FormatSize(long bytes) => bytes.ToString();
    }
}
