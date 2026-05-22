using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace QemuGaGuard;

public partial class MainWindow : Window
{
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshUiAsync("State loaded.");
        Activated += async (_, _) =>
        {
            if (!_busy)
            {
                await RefreshUiAsync();
            }
        };
    }

    private async Task RefreshUiAsync(string? lastActionOverride = null)
    {
        _busy = true;
        SetButtonsEnabled(false);

        try
        {
            var snapshot = await Task.Run(QemuGaServiceManager.GetSnapshot);
            ApplySnapshot(snapshot, lastActionOverride);
        }
        catch (Exception ex)
        {
            LastActionText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot, string? lastActionOverride = null)
    {
        ServiceBadgeText.Text = snapshot.BadgeText;
        ServiceBadgeText.Foreground = new SolidColorBrush(snapshot.BadgeColor);
        AdminStateText.Text = snapshot.IsElevated
            ? "Administrator session. Service start mode and stop/start actions can be changed from this window."
            : "Standard session. Enable/Disable will trigger UAC because service configuration needs administrator rights.";
        DisplayNameText.Text = snapshot.Exists ? snapshot.DisplayName : "QEMU-GA service not found on this machine.";
        StatusText.Text = snapshot.Exists ? snapshot.StatusText : "Missing";
        StartModeText.Text = snapshot.Exists ? snapshot.StartModeText : "Unknown";
        BinaryPathText.Text = snapshot.Exists ? snapshot.BinaryPath : "-";
        GuidanceText.Text = snapshot.Exists
            ? snapshot.IsElevated
                ? "Use Disable when you need to stop guest-triggered shutdown requests from QEMU Guest Agent. Use Enable to restore automatic startup."
                : "Use Relaunch as Admin first, or click Enable/Disable directly and accept the UAC prompt."
            : "The service is not installed here, so this tool has nothing to toggle.";
        LastActionText.Text = lastActionOverride ?? "Ready.";

        EnableButton.IsEnabled = snapshot.Exists && !_busy;
        DisableButton.IsEnabled = snapshot.Exists && !_busy;
        ElevateButton.IsEnabled = !snapshot.IsElevated && !_busy;
    }

    private async Task RunActionAsync(GuardAction action)
    {
        if (_busy)
        {
            return;
        }

        if (!QemuGaServiceManager.IsProcessElevated())
        {
            TryRelaunchElevated(action);
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);
        LastActionText.Text = action == GuardAction.Enable
            ? "Enabling QEMU Guest Agent..."
            : "Disabling QEMU Guest Agent...";

        try
        {
            if (action == GuardAction.Enable)
            {
                await QemuGaServiceManager.EnableAsync();
                await RefreshUiAsync("QEMU Guest Agent enabled. Startup mode restored to Automatic.");
            }
            else
            {
                await QemuGaServiceManager.DisableAsync();
                await RefreshUiAsync("QEMU Guest Agent disabled. Service stopped and startup mode set to Disabled.");
            }
        }
        catch (Exception ex)
        {
            LastActionText.Text = ex.Message;
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void TryRelaunchElevated(GuardAction? action = null)
    {
        try
        {
            var arguments = action is null
                ? "--show-ui"
                : $"--invoke {(action == GuardAction.Enable ? "enable" : "disable")} --show-ui";

            QemuGaServiceManager.RelaunchElevated(arguments);
            LastActionText.Text = "UAC prompt opened.";
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastActionText.Text = "UAC prompt canceled.";
        }
        catch (Exception ex)
        {
            LastActionText.Text = ex.Message;
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        EnableButton.IsEnabled = enabled;
        DisableButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        ServicesButton.IsEnabled = enabled;
        ElevateButton.IsEnabled = enabled && !QemuGaServiceManager.IsProcessElevated();
    }

    private async void EnableButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(GuardAction.Enable);
    }

    private async void DisableButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(GuardAction.Disable);
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshUiAsync("State refreshed.");
    }

    private void ElevateButton_OnClick(object sender, RoutedEventArgs e)
    {
        TryRelaunchElevated();
    }

    private void ServicesButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "services.msc",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LastActionText.Text = ex.Message;
        }
    }
}
