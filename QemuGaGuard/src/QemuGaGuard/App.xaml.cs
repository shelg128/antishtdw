using System.IO;
using System.Windows;

namespace QemuGaGuard;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var options = StartupOptions.Parse(e.Args);

        try
        {
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

            if (!string.IsNullOrWhiteSpace(options.ExportStatePath))
            {
                var resolvedPath = Path.GetFullPath(options.ExportStatePath);
                QemuGaServiceManager.ExportSnapshot(resolvedPath);
                if (!options.ShowUi && options.InvokedAction is null)
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
                MessageBox.Show(
                    ex.Message,
                    "QEMU Guest Agent Guard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown(1);
            return;
        }

        if (options.HeadlessOnly)
        {
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private sealed record StartupOptions(GuardAction? InvokedAction, string? ExportStatePath, bool ShowUi)
    {
        public bool HeadlessOnly => !ShowUi && (InvokedAction is not null || !string.IsNullOrWhiteSpace(ExportStatePath));

        public static StartupOptions Parse(string[] args)
        {
            GuardAction? invokedAction = null;
            string? exportStatePath = null;
            bool showUi = false;

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
                else if (string.Equals(current, "--export-state", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    i++;
                    exportStatePath = args[i];
                }
                else if (string.Equals(current, "--show-ui", StringComparison.OrdinalIgnoreCase))
                {
                    showUi = true;
                }
            }

            return new StartupOptions(invokedAction, exportStatePath, showUi);
        }
    }
}
