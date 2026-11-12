using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinKit.Views;

public static class ChoiceDialog
{
    public static string? Pick(Window? owner, string title, string message, IReadOnlyList<string> options)
    {
        if (options.Count == 0) return null;

        var window = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 520,
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

        var list = new ListBox { MaxHeight = 300, SelectedIndex = 0 };
        foreach (var option in options) list.Items.Add(option);
        root.Children.Add(list);

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

        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }
}
