using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

public partial class XRayDownloadWindow : Window
{
    public XRayDownloadWindow()
    {
        InitializeComponent();

        this.Opened += async (s, e) =>
        {
            if (DataContext is XRayDownloadViewModel vm)
                await vm.StartAsync(() => Close());
        };
    }

    private void OnRetryClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is XRayDownloadViewModel vm)
        {
            _ = vm.StartAsync(() => Close());
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
