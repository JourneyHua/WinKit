using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WinKit.Services;

namespace WinKit.Views;

internal static class EnvironmentVariableDialog
{
    internal static string? Show(Window? owner, Func<string, string, Task> create)
    {
        var window = new Window
        {
            Title = "新增当前用户变量",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Window");
        var root = new StackPanel { Margin = new Thickness(20) };
        var name = AddField("变量名（例如 JAVA_HOME）");
        var value = AddField("变量值（可引用 %SystemRoot% 等变量）");

        var error = new TextBlock { Style = (Style)window.FindResource("ErrorText") };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var errorCard = new Border
        {
            Style = (Style)window.FindResource("ErrorBar"), Child = error,
            Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed,
        };
        root.Children.Add(errorCard);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)window.FindResource("BtnBase"),
        };
        var save = new Button
        {
            Content = "新增", IsDefault = true, MinWidth = 80,
            Style = (Style)window.FindResource("BtnPrimary"),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        window.Content = root;

        var saving = false;
        string? createdName = null;
        window.Loaded += (_, _) => name.Focus();
        window.Closing += (_, e) => e.Cancel = saving;
        name.TextChanged += (_, _) => errorCard.Visibility = Visibility.Collapsed;
        save.Click += async (_, _) =>
        {
            if (saving) return;
            var candidate = name.Text.Trim();
            if (EnvironmentService.ValidateVariableName(candidate) is { } validation)
            {
                ShowError(validation);
                name.Focus();
                return;
            }

            errorCard.Visibility = Visibility.Collapsed;
            SetSaving(true);
            try
            {
                await create(candidate, value.Text);
                createdName = candidate;
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { SetSaving(false); }
            if (createdName is not null) window.DialogResult = true;
            else name.Focus();
        };
        return window.ShowDialog() == true ? createdName : null;

        TextBox AddField(string label)
        {
            var field = new TextBox { Style = (Style)window.FindResource("SearchBox") };
            AutomationProperties.SetName(field, label);
            root.Children.Add(new TextBlock
            {
                Text = label, Style = (Style)window.FindResource("Muted"),
                Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 12, 0, 4),
            });
            root.Children.Add(field);
            return field;
        }

        void SetSaving(bool active)
        {
            saving = active;
            name.IsReadOnly = value.IsReadOnly = active;
            save.IsEnabled = cancel.IsEnabled = !active;
            save.Content = active ? "正在保存…" : "新增";
        }

        void ShowError(string message)
        {
            error.Text = message;
            errorCard.Visibility = Visibility.Visible;
        }
    }
}
