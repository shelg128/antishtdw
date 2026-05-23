using System.IO;
using System.Windows;

namespace QemuGaGuard;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var options = StartupOptions.Parse(e.Args);

        try
        {
            if (options.KeepAwake)
            {
                await SystemGuardManager.RunKeepAwakeLoopAsync();
                return;
            }

            if (options.KeepAwake)
            {
                await SystemGuardManager.RunKeepAwakeLoopAsync();
                return;
            }

            if (options.InvokedAction is not null)
            {
                if (options.InvokedAction == GuardAction.Enable)
                {
                    await QemuGaServiceManager.EnableAsync();
                }
                else if (options.InvokedAction == GuardAction.Disable)
                {
                    await QemuGaServiceManager.DisableAsync();
                }
            }

            if (options.SystemAction is not null)
            {
                await SystemGuardManager.RunActionAsync(options.SystemAction.Value);
            }

            if (!string.IsNullOrWhiteSpace(options.ExportStatePath))
            {
                var resolvedPath = Path.GetFullPath(options.ExportStatePath);
                QemuGaServiceManager.ExportSnapshot(resolvedPath);
                if (!options.ShowUi && options.InvokedAction is null && options.SystemAction is null)
                {
                    Shutdown(0);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            if (options.ShowUi || options.InvokedAction is not null)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "QEMU Guest Agent Guard",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }

            Shutdown(1);
            return;
        }

        if (options.HeadlessOnly)
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow(options.StartInTray);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        
        if (!options.StartInTray)
        {
            window.Show();
        }
    }

    private sealed record StartupOptions(
        GuardAction? InvokedAction,
        SystemGuardAction? SystemAction,
        string? ExportStatePath,
        bool ShowUi,
        bool KeepAwake,
        bool StartInTray)
    {
        public bool HeadlessOnly => !ShowUi && !StartInTray && (InvokedAction is not null || SystemAction is not null || !string.IsNullOrWhiteSpace(ExportStatePath) || KeepAwake);

        public static StartupOptions Parse(string[] args)
        {
            GuardAction? invokedAction = null;
            SystemGuardAction? systemAction = null;
            string? exportStatePath = null;
            bool showUi = false;
            bool keepAwake = false;
            bool startInTray = false;

            for (int i = 0; i < args.Length; i++)
            {
                var current = args[i];
                if (string.Equals(current, "--invoke", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    i++;
                    var actionValue = args[i];
                    if (string.Equals(actionValue, "enable", StringComparison.OrdinalIgnoreCase))
                    {
                        invokedAction = GuardAction.Enable;
                    }
                    else if (string.Equals(actionValue, "disable", StringComparison.OrdinalIgnoreCase))
                    {
                        invokedAction = GuardAction.Disable;
                    }
                }
                else if (string.Equals(current, "--system-action", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    i++;
                    var actionValue = args[i].ToLowerInvariant();
                    systemAction = actionValue switch
                    {
                        "set-power-button-do-nothing" => SystemGuardAction.SetPowerButtonDoNothing,
                        "set-power-button-shutdown" => SystemGuardAction.SetPowerButtonShutdown,
                        "install-keep-awake" => SystemGuardAction.InstallKeepAwake,
                        "remove-keep-awake" => SystemGuardAction.RemoveKeepAwake,
                        "hide-power-menu" => SystemGuardAction.HidePowerMenu,
                        "show-power-menu" => SystemGuardAction.ShowPowerMenu,
                        "disable-vm-guest-shutdown" => SystemGuardAction.DisableVmGuestShutdown,
                        "enable-vm-guest-shutdown" => SystemGuardAction.EnableVmGuestShutdown,
                        "set-sleep-never" => SystemGuardAction.SetSleepNever,
                        "restore-sleep-timeouts" => SystemGuardAction.RestoreSleepTimeouts,
                        "set-windows-update-no-auto-restart" => SystemGuardAction.SetWindowsUpdateNoAutoRestart,
                        "clear-windows-update-no-auto-restart" => SystemGuardAction.ClearWindowsUpdateNoAutoRestart,
                        "apply-recommended-hardening" => SystemGuardAction.ApplyRecommendedHardening,
                        _ => throw new ArgumentException($"Unknown system action: {actionValue}")
                    };
                }
                else if (string.Equals(current, "--export-state", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    i++;
                    exportStatePath = args[i];
                }
                else if (string.Equals(current, "--show-ui", StringComparison.OrdinalIgnoreCase))
                {
                    showUi = true;
                }
                else if (string.Equals(current, "--keep-awake", StringComparison.OrdinalIgnoreCase))
                {
                    keepAwake = true;
                }
                else if (string.Equals(current, "--tray", StringComparison.OrdinalIgnoreCase))
                {
                    startInTray = true;
                }
            }

            return new StartupOptions(invokedAction, systemAction, exportStatePath, showUi, keepAwake, startInTray);
        }
    }
}
