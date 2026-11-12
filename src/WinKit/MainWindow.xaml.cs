using System.Windows;
using WinKit.ViewModels;

namespace WinKit;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Loaded += (_, _) => (DataContext as MainViewModel)?.Initialize();
    }
}
