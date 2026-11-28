using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinKit.Core
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

namespace WinKit.Services
{
    public static class ThisPcService
    {
        public static string FormatSize(long bytes) => $"{bytes} B";
    }

    public static class AppManagerService
    {
        public static Task<(int Code, string Output, string Error)> RunProcessAsync(
            string fileName, string arguments, TimeSpan timeout) => Task.FromResult((0, string.Empty, string.Empty));
    }
}
