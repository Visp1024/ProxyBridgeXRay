using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using ProxyBridge.GUI.ViewModels;
using ProxyBridge.GUI.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyBridge.GUI;

public class App : Application
{
    public static bool StartMinimized { get; set; }
    private EventWaitHandle? _showWindowEvent;
    private CancellationTokenSource? _eventListenerCts;
    private const string EventName = "Global\\ProxyBridge_ShowWindow_Event_v3.1";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };

            if (StartMinimized)
            {
                // One-shot: спрятать окно только при первом Opened. Avalonia ре-фаерит
                // Opened и при восстановлении из трея — без отписки окно снова прячется.
                EventHandler? hideOnce = null;
                hideOnce = (s, e) =>
                {
                    desktop.MainWindow!.Opened -= hideOnce!;
                    desktop.MainWindow!.Hide();
                    desktop.MainWindow!.ShowInTaskbar = false;
                };
                desktop.MainWindow.Opened += hideOnce;
            }

            try
            {
                _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                _eventListenerCts = new CancellationTokenSource();
                Task.Run(() => ListenForActivationSignal(_eventListenerCts.Token));
            }
            catch { }

            desktop.ShutdownRequested += (s, e) =>
            {
                _eventListenerCts?.Cancel();
                _showWindowEvent?.Dispose();
                (desktop.MainWindow?.DataContext as MainWindowViewModel)?.Cleanup();
            };

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task ListenForActivationSignal(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var signaled = await Task.Run(() => _showWindowEvent?.WaitOne(1000) ?? false, token);
                if (signaled && !token.IsCancellationRequested)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        TrayIcon_Show(null, EventArgs.Empty));
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void TrayIcon_Show(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.ShowInTaskbar = true;
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }
    }

    private DateTime _lastTrayClick = DateTime.MinValue;
    public void TrayIcon_Clicked(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTrayClick).TotalMilliseconds < 500)
        {
            _lastTrayClick = DateTime.MinValue;
            TrayIcon_Show(sender, e);
        }
        else
        {
            _lastTrayClick = now;
        }
    }

    public void TrayIcon_Exit(object? sender, EventArgs e)
    {
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
