using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Media;

namespace QemuGaGuard;

public enum GuardAction
{
    Enable,
    Disable
}

public sealed record ServiceSnapshot(
    bool Exists,
    bool IsElevated,
    string DisplayName,
    string BinaryPath,
    string StatusText,
    string StartModeText,
    string BadgeText,
    Color BadgeColor);

public static class QemuGaServiceManager
{
    public const string TargetServiceName = "QEMU-GA";
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 0x00000002;
    private const uint ServiceDisabled = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;

    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static ServiceSnapshot GetSnapshot()
    {
        var isElevated = IsProcessElevated();
        ServiceConfiguration? configuration = null;

        try
        {
            configuration = QueryConfiguration(TargetServiceName);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceDoesNotExist)
        {
            return BuildMissingSnapshot(isElevated);
        }

        using var controller = new ServiceController(TargetServiceName);
        controller.Refresh();

        return new ServiceSnapshot(
            Exists: true,
            IsElevated: isElevated,
            DisplayName: string.IsNullOrWhiteSpace(configuration.DisplayName) ? controller.DisplayName : configuration.DisplayName,
            BinaryPath: configuration.BinaryPath,
            StatusText: controller.Status.ToString(),
            StartModeText: DescribeStartType(configuration.StartType),
            BadgeText: BuildBadgeText(controller.Status, configuration.StartType),
            BadgeColor: BuildBadgeColor(controller.Status, configuration.StartType));
    }

    public static async Task EnableAsync()
    {
        EnsureElevated();
        await Task.Run(() =>
        {
            ChangeStartType(TargetServiceName, ServiceAutoStart);
            using var controller = new ServiceController(TargetServiceName);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Running &&
                controller.Status != ServiceControllerStatus.StartPending)
            {
                controller.Start();
            }

            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        });
    }

    public static async Task DisableAsync()
    {
        EnsureElevated();
        await Task.Run(() =>
        {
            using var controller = new ServiceController(TargetServiceName);
            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Stopped &&
                controller.Status != ServiceControllerStatus.StopPending)
            {
                if (!controller.CanStop)
                {
                    throw new InvalidOperationException("QEMU-GA service cannot be stopped from ServiceController.");
                }

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            ChangeStartType(TargetServiceName, ServiceDisabled);
        });
    }

    public static void ExportSnapshot(string outputPath)
    {
        var snapshot = GetSnapshot();
        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            serviceName = TargetServiceName,
            exists = snapshot.Exists,
            isElevated = snapshot.IsElevated,
            displayName = snapshot.DisplayName,
            binaryPath = snapshot.BinaryPath,
            status = snapshot.StatusText,
            startMode = snapshot.StartModeText,
            badge = snapshot.BadgeText
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, json);
    }

    public static void RelaunchElevated(string arguments)
    {
        var executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot resolve current executable path.");

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static void EnsureElevated()
    {
        if (!IsProcessElevated())
        {
            throw new InvalidOperationException("Administrator rights are required to change QEMU-GA service configuration.");
        }
    }

    private static ServiceSnapshot BuildMissingSnapshot(bool isElevated)
    {
        return new ServiceSnapshot(
            Exists: false,
            IsElevated: isElevated,
            DisplayName: "QEMU Guest Agent",
            BinaryPath: string.Empty,
            StatusText: "Missing",
            StartModeText: "Unknown",
            BadgeText: "Missing",
            BadgeColor: Color.FromRgb(243, 201, 105));
    }

    private static string DescribeStartType(uint startType)
    {
        return startType switch
        {
            ServiceAutoStart => "Automatic",
            0x00000003 => "Manual",
            ServiceDisabled => "Disabled",
            _ => $"Other ({startType})"
        };
    }

    private static string BuildBadgeText(ServiceControllerStatus status, uint startType)
    {
        if (startType == ServiceDisabled)
        {
            return "Disabled";
        }

        return status switch
        {
            ServiceControllerStatus.Running => "Running",
            ServiceControllerStatus.Stopped => "Stopped",
            ServiceControllerStatus.StartPending => "Starting",
            ServiceControllerStatus.StopPending => "Stopping",
            _ => status.ToString()
        };
    }

    private static Color BuildBadgeColor(ServiceControllerStatus status, uint startType)
    {
        if (startType == ServiceDisabled)
        {
            return Color.FromRgb(255, 125, 114);
        }

        return status == ServiceControllerStatus.Running
            ? Color.FromRgb(57, 208, 165)
            : Color.FromRgb(243, 201, 105);
    }

    private static ServiceConfiguration QueryConfiguration(string serviceName)
    {
        var scmHandle = OpenSCManagerW(null, null, ScManagerConnect);
        if (scmHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var serviceHandle = OpenServiceW(scmHandle, serviceName, ServiceQueryConfig);
            if (serviceHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                QueryServiceConfigW(serviceHandle, IntPtr.Zero, 0, out var bytesNeeded);
                var lastError = Marshal.GetLastWin32Error();
                if (lastError != ErrorInsufficientBuffer)
                {
                    throw new Win32Exception(lastError);
                }

                var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
                try
                {
                    if (!QueryServiceConfigW(serviceHandle, buffer, bytesNeeded, out _))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    var config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buffer);
                    return new ServiceConfiguration(
                        BinaryPath: Marshal.PtrToStringUni(config.lpBinaryPathName) ?? string.Empty,
                        DisplayName: Marshal.PtrToStringUni(config.lpDisplayName) ?? string.Empty,
                        StartType: config.dwStartType);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            CloseServiceHandle(scmHandle);
        }
    }

    private static void ChangeStartType(string serviceName, uint startType)
    {
        var scmHandle = OpenSCManagerW(null, null, ScManagerConnect);
        if (scmHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var serviceHandle = OpenServiceW(scmHandle, serviceName, ServiceChangeConfig);
            if (serviceHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!ChangeServiceConfigW(
                        serviceHandle,
                        ServiceNoChange,
                        startType,
                        ServiceNoChange,
                        null,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null,
                        null))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseServiceHandle(serviceHandle);
            }
        }
        finally
        {
            CloseServiceHandle(scmHandle);
        }
    }

    private sealed record ServiceConfiguration(string BinaryPath, string DisplayName, uint StartType);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr serviceControlManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfigW(
        IntPtr serviceHandle,
        IntPtr queryServiceConfigPtr,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(
        IntPtr serviceHandle,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);
}
