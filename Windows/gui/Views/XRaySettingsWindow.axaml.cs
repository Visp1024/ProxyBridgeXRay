using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

public partial class XRaySettingsWindow : Window
{
    private ComboBox? _flowBox;
    private ComboBox? _fpBox;
    private ComboBox? _xudp443Box;

    public XRaySettingsWindow()
    {
        InitializeComponent();

        this.Opened += (s, e) =>
        {
            if (DataContext is not XRaySettingsViewModel vm) return;

            _flowBox = this.FindControl<ComboBox>("FlowComboBox");
            _fpBox   = this.FindControl<ComboBox>("FingerprintComboBox");
            _xudp443Box = this.FindControl<ComboBox>("Xudp443ComboBox");

            SetComboBox(_flowBox, vm.Flow);
            SetComboBox(_fpBox, vm.Fingerprint);
            SetComboBox(_xudp443Box, vm.XudpProxyUDP443);

            if (_flowBox != null)
            {
                _flowBox.SelectionChanged += (_, _) =>
                {
                    if (_flowBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                        vm.Flow = tag;
                };
            }

            if (_fpBox != null)
            {
                _fpBox.SelectionChanged += (_, _) =>
                {
                    if (_fpBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                        vm.Fingerprint = tag;
                };
            }

            if (_xudp443Box != null)
            {
                _xudp443Box.SelectionChanged += (_, _) =>
                {
                    if (_xudp443Box.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                        vm.XudpProxyUDP443 = tag;
                };
            }

            // Keep ComboBoxes in sync when import fills in Flow/Fingerprint
            vm.PropertyChanged += OnViewModelPropertyChanged;
        };

        this.Closed += (s, e) =>
        {
            if (DataContext is XRaySettingsViewModel vm)
                vm.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not XRaySettingsViewModel vm) return;

        if (e.PropertyName == nameof(XRaySettingsViewModel.Flow))
            SetComboBox(_flowBox, vm.Flow);
        else if (e.PropertyName == nameof(XRaySettingsViewModel.Fingerprint))
            SetComboBox(_fpBox, vm.Fingerprint);
        else if (e.PropertyName == nameof(XRaySettingsViewModel.XudpProxyUDP443))
            SetComboBox(_xudp443Box, vm.XudpProxyUDP443);
    }

    private static void SetComboBox(ComboBox? box, string value)
    {
        if (box == null) return;

        foreach (var obj in box.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string tag && tag == value)
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private async void OnBrowseXRayPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not XRaySettingsViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select xray executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable") { Patterns = new[] { "xray.exe", "xray" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0)
            vm.XRayPath = files[0].Path.LocalPath;
    }
}
