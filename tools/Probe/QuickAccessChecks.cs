using WinKit.Services;

namespace WinKitProbe;

internal static class QuickAccessChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string label, bool condition)
        {
            Console.WriteLine($"  [{(condition ? "OK" : "FAIL")}] 快速访问：{label}");
            if (!condition) failures++;
        }

        string[] original = ["A", "B", "C"];
        string[] desired = ["B", "C", "A"];
        var live = original.ToList();
        var mutations = new List<string>();
        var pin = new Action<string>(path => { mutations.Add("+" + path); live.Add(path); });
        var unpin = new Action<string>(path => { mutations.Add("-" + path); live.Remove(path); });
        void Reset(params string[] paths) { live = paths.ToList(); mutations.Clear(); }
        QuickAccessOrderResult Apply(Action<string>? add = null, Action<string>? remove = null,
            Func<string, bool>? canPin = null, Func<IReadOnlyList<string>>? read = null, Action? wait = null)
            => QuickAccessOrder.Apply(original, desired, read ?? (() => live.ToArray()),
                add ?? pin, remove ?? unpin, canPin ?? (_ => true), wait ?? (() => { }));

        Reset("A", "B", "C", "外部新固定");
        Check("外部新增后零写入", Apply().Outcome == QuickAccessOrderOutcome.Changed && mutations.Count == 0);
        Reset("B", "A", "C");
        Check("外部重排后零写入", Apply().Outcome == QuickAccessOrderOutcome.Changed && mutations.Count == 0);
        Reset(original);
        Check("无法确认快照时零写入", Apply(read: () => throw new InvalidOperationException("不可用")).Outcome
            == QuickAccessOrderOutcome.NotApplied && mutations.Count == 0);
        Check("目标无法重新固定时零写入", Apply(canPin: path => path != "C").Outcome
            == QuickAccessOrderOutcome.NotApplied && mutations.Count == 0);
        Check("只移动必要项目并核对准确顺序", Apply().Outcome == QuickAccessOrderOutcome.Applied
            && live.SequenceEqual(desired) && mutations.SequenceEqual(["-A", "+A"]));

        Reset(original);
        string? delayedRemoval = null;
        var delayed = Apply(remove: path => delayedRemoval = path, wait: () =>
        {
            if (delayedRemoval is not { } path) return;
            delayedRemoval = null;
            unpin(path);
        });
        Check("Shell 延迟更新时等到实际状态再继续", delayed.Outcome == QuickAccessOrderOutcome.Applied
            && live.SequenceEqual(desired));

        Reset(original);
        var failureOnce = true;
        var restored = Apply(add: path =>
        {
            if (failureOnce) { failureOnce = false; throw new InvalidOperationException("模拟固定失败"); }
            pin(path);
        });
        Check("固定失败后恢复原顺序", restored.Outcome == QuickAccessOrderOutcome.Restored && live.SequenceEqual(original));

        Reset(original);
        var stillAccessible = true;
        var missing = Apply(remove: path => { unpin(path); stillAccessible = false; },
            canPin: path => path != "A" || stillAccessible);
        Check("恢复受阻时列出真实缺失路径", missing.Outcome == QuickAccessOrderOutcome.Incomplete
            && missing.Missing is { } missingPaths && missingPaths.SequenceEqual(["A"]) && mutations.SequenceEqual(["-A"]));

        Reset(original);
        var concurrent = Apply(remove: path => { unpin(path); live.Add("外部新固定"); });
        Check("操作中外部新增时仅补回自身取消的项", concurrent.Outcome == QuickAccessOrderOutcome.Incomplete
            && concurrent.Missing is { Count: 0 } && live.Contains("外部新固定") && mutations.SequenceEqual(["-A", "+A"]));

        return failures;
    }
}
