namespace WinKit.Core;

/// <summary>Shell 自动化在自己的 STA 上创建、使用和释放 COM 对象，完成后仅返回普通数据。</summary>
internal static class ShellReadWorker
{
    public static Task<T> RunAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "WinKit Shell STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
