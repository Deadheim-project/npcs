using System.Windows;
using DeadheimLauncher.ViewModels;

namespace DeadheimLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                await vm.InitializeAsync();
        };
    }
}
