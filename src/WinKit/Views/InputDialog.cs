using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinKit.Views;

public static class InputDialog
{
    public static string? Prompt(Window? owner, string title, string message, string initial)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Brush.Window"],
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = false
        };

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["Brush.TextMuted"],
            Margin = new Thickness(0, 0, 0, 10)
        });

        var input = new TextBox
        {
            Text = initial,
            Padding = new Thickness(8, 6, 8, 6),
            Style = (Style)Application.Current.Resources["SearchBox"]
        };
        root.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["BtnBase"]
        };
        cancel.Click += (_, _) => window.DialogResult = false;

        var ok = new Button
        {
            Content = "确定",
            MinWidth = 80,
            IsDefault = true,
            Style = (Style)Application.Current.Resources["BtnPrimary"]
        };
        ok.Click += (_, _) => window.DialogResult = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return window.ShowDialog() == true ? input.Text : null;
    }
}
