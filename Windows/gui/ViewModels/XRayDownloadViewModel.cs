using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ProxyBridge.GUI.Common;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.ViewModels;

public class XRayDownloadViewModel : ViewModelBase
{
    private readonly Loc _loc = Loc.Instance;
    public Loc Loc => _loc;

    private int    _progress;
    private string _statusText  = "Preparing…";
    private bool   _isRunning   = true;
    private bool   _isSuccess;
    private bool   _isFailed;
    private string _errorText   = "";

    private CancellationTokenSource? _cts;

    /// <summary>Set by the window after successful download; null if cancelled/failed.</summary>
    public string? DownloadedPath { get; private set; }

    public int Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        private set => SetProperty(ref _isSuccess, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        private set => SetProperty(ref _isFailed, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public ICommand CancelCommand { get; }

    public XRayDownloadViewModel()
    {
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    /// <summary>Called by the window's Opened handler (and Retry) to kick off the download.</summary>
    public async Task StartAsync(Action closeWindow)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // Reset state for retry
        DownloadedPath = null;
        IsRunning  = true;
        IsSuccess  = false;
        IsFailed   = false;
        ErrorText  = "";
        Progress   = 0;
        StatusText = "Preparing…";

        var reporter = new Progress<(int Percent, string Status)>(t =>
        {
            Progress   = t.Percent;
            StatusText = t.Status;
        });

        try
        {
            var path = await XRayDownloadService.DownloadAsync(reporter, _cts.Token);

            DownloadedPath = path;
            IsRunning = false;
            IsSuccess = true;
            StatusText = $"xray.exe saved to:\n{path}";

            // Brief pause so the user can read the success message, then close
            await Task.Delay(1200, CancellationToken.None);
            closeWindow();
        }
        catch (OperationCanceledException)
        {
            IsRunning = false;
            StatusText = "Download cancelled.";
            closeWindow();
        }
        catch (Exception ex)
        {
            IsRunning  = false;
            IsFailed   = true;
            ErrorText  = ex.Message;
            StatusText = "Download failed.";
        }
    }

    public void Dispose() => _cts?.Dispose();
}
