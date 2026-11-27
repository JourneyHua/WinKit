namespace WinKit.Services;

public enum QuickAccessOrderOutcome { Applied, Changed, NotApplied, Restored, Incomplete }

public sealed record QuickAccessOrderResult(
    QuickAccessOrderOutcome Outcome, string Reason = "", IReadOnlyList<string>? Missing = null);

/// <summary>Reorders only confirmed pins. Explorer has no atomic reorder operation.</summary>
public static class QuickAccessOrder
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static QuickAccessOrderResult Apply(IReadOnlyList<string> expected, IReadOnlyList<string> desired,
        Func<IReadOnlyList<string>> read, Action<string> pin, Action<string> unpin,
        Func<string, bool> canPin, Action wait)
    {
        IReadOnlyList<string> current;
        try { current = read(); }
        catch (Exception ex) { return new(QuickAccessOrderOutcome.NotApplied, ex.Message); }

        if (!current.SequenceEqual(expected, Paths)) return new(QuickAccessOrderOutcome.Changed);
        if (desired.Count != current.Count || desired.Distinct(Paths).Count() != current.Count
            || !desired.ToHashSet(Paths).SetEquals(current))
            return new(QuickAccessOrderOutcome.NotApplied, "新顺序与当前固定文件夹不一致。");
        if (current.SequenceEqual(desired, Paths)) return new(QuickAccessOrderOutcome.Applied);
        if (current.FirstOrDefault(path => !canPin(path)) is { } unavailable)
            return new(QuickAccessOrderOutcome.NotApplied, $"此文件夹目前无法重新固定：{unavailable}");

        var mayBeMissing = new HashSet<string>(Paths);
        var concurrent = false;

        void Step(string path, bool add)
        {
            if (!canPin(path)) throw new InvalidOperationException($"此文件夹目前无法重新固定：{path}");
            var before = read();
            if (!before.SequenceEqual(current, Paths))
            {
                concurrent = true;
                current = before;
                throw new InvalidOperationException("资源管理器中的固定项在调整期间发生变化。");
            }

            var after = before.ToHashSet(Paths);
            if (add) after.Add(path);
            else
            {
                after.Remove(path);
                mayBeMissing.Add(path);
            }

            // Shell dispatch can report success before the list changes, or fail after doing the work.
            string? dispatchError = null;
            try { (add ? pin : unpin)(path); }
            catch (Exception ex) { dispatchError = ex.Message; }
            for (var i = 0; i < 12; i++)
            {
                if (i != 0) wait();
                var observed = read();
                current = observed;
                if (after.SetEquals(observed) && after.Count == observed.Count)
                {
                    if (add) mayBeMissing.Remove(path);
                    if (before.Where(item => !Paths.Equals(item, path))
                        .SequenceEqual(observed.Where(item => !Paths.Equals(item, path)), Paths)) return;
                }
                if (!observed.SequenceEqual(before, Paths))
                {
                    concurrent = true;
                    throw new InvalidOperationException("资源管理器中的固定项在调整期间发生变化。");
                }
            }
            throw new InvalidOperationException(dispatchError ?? $"资源管理器未确认{(add ? "固定" : "取消固定")}：{path}");
        }

        void Rearrange(IReadOnlyList<string> order)
        {
            // Keep the longest already-in-order prefix; only move folders Explorer must append again.
            var keepCount = 0;
            foreach (var path in current)
                if (keepCount < order.Count && Paths.Equals(path, order[keepCount])) keepCount++;
            var keep = order.Take(keepCount).ToHashSet(Paths);
            foreach (var path in current.Where(path => !keep.Contains(path)).ToArray()) Step(path, false);
            foreach (var path in order.Skip(keepCount)) Step(path, true);
            if (!current.SequenceEqual(order, Paths))
                throw new InvalidOperationException("资源管理器没有保留请求的顺序。");
        }

        try
        {
            Rearrange(desired);
            return new(QuickAccessOrderOutcome.Applied);
        }
        catch (Exception ex)
        {
            var failure = ex.Message;
            try
            {
                var latest = read();
                if (!latest.SequenceEqual(current, Paths)) concurrent = true;
                current = latest;
                if (!concurrent && current.All(path => expected.Contains(path, Paths))
                    && expected.All(path => current.Contains(path, Paths) || mayBeMissing.Contains(path))
                    && expected.All(canPin) && !current.SequenceEqual(expected, Paths))
                {
                    // Without signs of another writer, try restoring the exact original order once.
                    try { Rearrange(expected); }
                    catch (Exception) { /* Leave remaining pins alone; repair only missing ones below. */ }
                }

                foreach (var path in expected.Where(mayBeMissing.Contains).ToArray())
                {
                    current = read();
                    if (!current.Contains(path, Paths) && canPin(path))
                    {
                        try { Step(path, true); }
                        catch (Exception) { /* Continue restoring other missing pins. */ }
                    }
                }
                current = read();
                if (current.SequenceEqual(expected, Paths)) return new(QuickAccessOrderOutcome.Restored, failure);
                return new(QuickAccessOrderOutcome.Incomplete, failure, expected.Where(path => !current.Contains(path, Paths)).ToArray());
            }
            catch (Exception recoveryError)
            {
                return new(QuickAccessOrderOutcome.Incomplete, $"{failure}\n无法复查恢复结果：{recoveryError.Message}");
            }
        }
    }
}
