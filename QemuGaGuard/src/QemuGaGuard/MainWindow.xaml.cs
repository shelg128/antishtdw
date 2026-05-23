using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace QemuGaGuard;

public partial class MainWindow : Window
{
    private bool _busy;
    private ServiceSnapshot? _lastServiceSnapshot;

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
            var qemuSnapshotTask = Task.Run(QemuGaServiceManager.GetSnapshot);
            var systemSnapshotTask = Task.Run(SystemGuardManager.GetSnapshot);

            await Task.WhenAll(qemuSnapshotTask, systemSnapshotTask);
            ApplySnapshot(qemuSnapshotTask.Result, lastActionOverride);
            ApplySystemSnapshot(systemSnapshotTask.Result);
        }
        catch (Exception ex)
        {
            LastActionText.Text = ex.Message;
            SystemActionText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void ApplySnapshot(ServiceSnapshot snapshot, string? lastActionOverride = null)
    {
        _lastServiceSnapshot = snapshot;
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

    private void ApplySystemSnapshot(SystemGuardSnapshot snapshot)
    {
        PowerButtonAcText.Text = snapshot.PowerButtonAc;
        PowerButtonDcText.Text = snapshot.PowerButtonDc;
        SleepAfterText.Text = $"AC: {snapshot.SleepAfterAc} / DC: {snapshot.SleepAfterDc}";

        KeepAwakeTaskText.Text = snapshot.KeepAwakeTaskExists
            ? $"{snapshot.KeepAwakeTaskStatus}\n{snapshot.KeepAwakeTaskCommand}"
            : "Not installed";

        VmGuestShutdownText.Text = $"{snapshot.VmGuestShutdownStatus} / {snapshot.VmGuestShutdownStartMode}";
        WindowsUpdateRestartText.Text = snapshot.WindowsUpdateNoAutoRestart
            ? "Blocked while a user is logged on"
            : "Not blocked by this policy";

        PowerMenuPolicyText.Text = snapshot.PowerMenuHidden
            ? "Hidden for current user"
            : "Visible for current user";

        SystemActionText.Text = "Ready.";
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
        var serviceExists = _lastServiceSnapshot?.Exists ?? false;
        EnableButton.IsEnabled = enabled && serviceExists;
        DisableButton.IsEnabled = enabled && serviceExists;
        RefreshButton.IsEnabled = enabled;
        ServicesButton.IsEnabled = enabled;
        ElevateButton.IsEnabled = enabled && !QemuGaServiceManager.IsProcessElevated();
        SetPowerButtonDoNothingButton.IsEnabled = enabled;
        RestorePowerButtonShutdownButton.IsEnabled = enabled;
        InstallKeepAwakeButton.IsEnabled = enabled;
        RemoveKeepAwakeButton.IsEnabled = enabled;
        HidePowerMenuButton.IsEnabled = enabled;
        ShowPowerMenuButton.IsEnabled = enabled;
        ApplyHardeningButton.IsEnabled = enabled;
        DisableVmGuestShutdownButton.IsEnabled = enabled;
        SetSleepNeverButton.IsEnabled = enabled;
        WindowsUpdateNoRestartButton.IsEnabled = enabled;
        RefreshSystemButton.IsEnabled = enabled;
    }

    private async Task RunSystemActionAsync(SystemGuardAction action, string busyText, string successText)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);
        SystemActionText.Text = busyText;

        try
        {
            await SystemGuardManager.RunActionAsync(action);
            await RefreshUiAsync();
            SystemActionText.Text = successText;
        }
        catch (Exception ex)
        {
            SystemActionText.Text = ex.Message;
            _busy = false;
            SetButtonsEnabled(true);
        }
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

    private async void RefreshSystemButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshUiAsync("State refreshed.");
        SystemActionText.Text = "State refreshed.";
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

    private async void SetPowerButtonDoNothingButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.SetPowerButtonDoNothing,
            "Setting AC power button to Do nothing...",
            "AC power button is now Do nothing.");
    }

    private async void ApplyHardeningButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.ApplyRecommendedHardening,
            "Applying recommended guest-side shutdown hardening...",
            "Recommended guest-side shutdown hardening applied.");
    }

    private async void DisableVmGuestShutdownButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.DisableVmGuestShutdown,
            "Disabling VM guest shutdown integration service...",
            "VM guest shutdown integration service disabled.");
    }

    private async void SetSleepNeverButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.SetSleepNever,
            "Setting sleep and hibernate timeouts to Never...",
            "Sleep and hibernate timeouts are now Never.");
    }

    private async void WindowsUpdateNoRestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.SetWindowsUpdateNoAutoRestart,
            "Setting Windows Update no auto-restart policy...",
            "Windows Update no auto-restart policy enabled.");
    }

    private async void RestorePowerButtonShutdownButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.SetPowerButtonShutdown,
            "Setting AC power button to Shut down...",
            "AC power button is now Shut down.");
    }

    private async void InstallKeepAwakeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.InstallKeepAwake,
            "Installing keep-awake scheduled task...",
            "Keep-awake task installed and started.");
    }

    private async void RemoveKeepAwakeButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.RemoveKeepAwake,
            "Removing keep-awake scheduled task...",
            "Keep-awake task removed.");
    }

    private async void HidePowerMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.HidePowerMenu,
            "Hiding Windows power menu for current user...",
            "Windows power menu hidden for current user.");
    }

    private async void ShowPowerMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunSystemActionAsync(
            SystemGuardAction.ShowPowerMenu,
            "Showing Windows power menu for current user...",
            "Windows power menu restored for current user.");
    }
}
