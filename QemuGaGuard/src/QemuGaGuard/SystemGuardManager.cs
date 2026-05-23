using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace QemuGaGuard;

public enum SystemGuardAction
{
    SetPowerButtonDoNothing,
    SetPowerButtonShutdown,
    InstallKeepAwake,
    RemoveKeepAwake,
    HidePowerMenu,
    ShowPowerMenu,
    DisableVmGuestShutdown,
    EnableVmGuestShutdown,
    SetSleepNever,
    SetWindowsUpdateNoAutoRestart,
    ClearWindowsUpdateNoAutoRestart,
    RestoreSleepTimeouts,
    ApplyRecommendedHardening
}

public sealed record SystemGuardSnapshot(
    string PowerButtonAc,
    string PowerButtonDc,
    string SleepAfterAc,
    string SleepAfterDc,
    bool KeepAwakeTaskExists,
    string KeepAwakeTaskStatus,
    string KeepAwakeTaskCommand,
    bool PowerMenuHidden,
    string VmGuestShutdownStatus,
    string VmGuestShutdownStartMode,
    bool WindowsUpdateNoAutoRestart,
    bool MouseMoverTaskExists,
    string MouseMoverTaskStatus);

public static class SystemGuardManager
{
    public const string KeepAwakeTaskName = "AntiIdleKeepAwake";
    public const string MouseMoverTaskName = "AntiShutdownGuardMouseMover";

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const string ExplorerPolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string NoCloseValueName = "NoClose";

    public static SystemGuardSnapshot GetSnapshot()
    {
        var powerButton = QueryPowerButtonAction();
        var sleepAfter = QueryPowerSetting("SUB_SLEEP", "STANDBYIDLE", DescribeSeconds);
        var task = QueryKeepAwakeTask();
        var mouseMoverTask = QueryMouseMoverTask();
        var vmGuestShutdown = QueryServiceState("vmicshutdown");

        return new SystemGuardSnapshot(
            PowerButtonAc: powerButton.Ac,
            PowerButtonDc: powerButton.Dc,
            SleepAfterAc: sleepAfter.Ac,
            SleepAfterDc: sleepAfter.Dc,
            KeepAwakeTaskExists: task.Exists,
            KeepAwakeTaskStatus: task.Status,
            KeepAwakeTaskCommand: task.Command,
            PowerMenuHidden: IsPowerMenuHidden(),
            VmGuestShutdownStatus: vmGuestShutdown.Status,
            VmGuestShutdownStartMode: vmGuestShutdown.StartMode,
            WindowsUpdateNoAutoRestart: IsWindowsUpdateNoAutoRestartEnabled(),
            MouseMoverTaskExists: mouseMoverTask.Exists,
            MouseMoverTaskStatus: mouseMoverTask.Status);
    }

    public static async Task RunActionAsync(SystemGuardAction action)
    {
        switch (action)
        {
            case SystemGuardAction.SetPowerButtonDoNothing:
                await SetPowerButtonAcActionAsync(0);
                break;
            case SystemGuardAction.SetPowerButtonShutdown:
                await SetPowerButtonAcActionAsync(3);
                break;
            case SystemGuardAction.InstallKeepAwake:
                await InstallKeepAwakeTaskAsync();
                break;
            case SystemGuardAction.RemoveKeepAwake:
                await RemoveKeepAwakeTaskAsync();
                break;
            case SystemGuardAction.HidePowerMenu:
                SetPowerMenuHidden(true);
                break;
            case SystemGuardAction.ShowPowerMenu:
                SetPowerMenuHidden(false);
                break;
            case SystemGuardAction.DisableVmGuestShutdown:
                await SetVmGuestShutdownEnabledAsync(false);
                break;
            case SystemGuardAction.EnableVmGuestShutdown:
                await SetVmGuestShutdownEnabledAsync(true);
                break;
            case SystemGuardAction.SetSleepNever:
                await SetSleepNeverAsync();
                break;
            case SystemGuardAction.SetWindowsUpdateNoAutoRestart:
                SetWindowsUpdateNoAutoRestart(true);
                break;
            case SystemGuardAction.ClearWindowsUpdateNoAutoRestart:
                SetWindowsUpdateNoAutoRestart(false);
                break;
            case SystemGuardAction.RestoreSleepTimeouts:
                await RestoreSleepTimeoutsAsync();
                break;
            case SystemGuardAction.ApplyRecommendedHardening:
                await ApplyRecommendedHardeningAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public static async Task RunKeepAwakeLoopAsync(CancellationToken cancellationToken = default)
    {
        var flags = EsContinuous | EsSystemRequired;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = SetThreadExecutionState(flags);
                if (result == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken);
            }
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
        }
    }

    private static async Task SetPowerButtonAcActionAsync(int actionIndex)
    {
        await RunProcessAsync(
            "powercfg.exe",
            "/setacvalueindex",
            "SCHEME_CURRENT",
            "SUB_BUTTONS",
            "PBUTTONACTION",
            actionIndex.ToString());

        await RunProcessAsync("powercfg.exe", "/setactive", "SCHEME_CURRENT");
    }

    private static async Task SetSleepNeverAsync()
    {
        await RunProcessAsync("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setactive", "SCHEME_CURRENT");
    }

    private static async Task RestoreSleepTimeoutsAsync()
    {
        await RunProcessAsync("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", "2700");
        await RunProcessAsync("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", "1200");
        await RunProcessAsync("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", "0");
        await RunProcessAsync("powercfg.exe", "/setactive", "SCHEME_CURRENT");
    }

    private static async Task ApplyRecommendedHardeningAsync()
    {
        await SetPowerButtonAcActionAsync(0);
        await SetSleepNeverAsync();
        await InstallKeepAwakeTaskAsync();
        await SetVmGuestShutdownEnabledAsync(false);
        SetPowerMenuHidden(true);
        SetWindowsUpdateNoAutoRestart(true);
    }

    private static async Task InstallKeepAwakeTaskAsync()
    {
        var executable = ResolveExecutablePath();
        var taskXml = BuildKeepAwakeTaskXml(executable);
        var taskXmlPath = Path.Combine(Path.GetTempPath(), $"{KeepAwakeTaskName}-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(taskXmlPath, taskXml, Encoding.Unicode);
            await RunProcessAsync("schtasks.exe", "/Create", "/TN", KeepAwakeTaskName, "/XML", taskXmlPath, "/F");
            await RunProcessAsync("schtasks.exe", "/Run", "/TN", KeepAwakeTaskName);
        }
        finally
        {
            File.Delete(taskXmlPath);
        }
    }

    private static async Task RemoveKeepAwakeTaskAsync()
    {
        _ = await RunProcessAsync("schtasks.exe", false, "/End", "/TN", KeepAwakeTaskName);
        await RunProcessAsync("schtasks.exe", "/Delete", "/TN", KeepAwakeTaskName, "/F");
    }

    public static async Task InstallMouseMoverTaskAsync()
    {
        var executable = ResolveExecutablePath();
        var taskXml = BuildMouseMoverTaskXml(executable);
        var taskXmlPath = Path.Combine(Path.GetTempPath(), $"{MouseMoverTaskName}-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(taskXmlPath, taskXml, Encoding.Unicode);
            await RunProcessAsync("schtasks.exe", "/Create", "/TN", MouseMoverTaskName, "/XML", taskXmlPath, "/F");
        }
        finally
        {
            File.Delete(taskXmlPath);
        }
    }

    public static async Task RemoveMouseMoverTaskAsync()
    {
        _ = await RunProcessAsync("schtasks.exe", false, "/End", "/TN", MouseMoverTaskName);
        await RunProcessAsync("schtasks.exe", "/Delete", "/TN", MouseMoverTaskName, "/F");
    }

    public static async Task EndMouseMoverTaskAsync()
    {
        _ = await RunProcessAsync("schtasks.exe", false, "/End", "/TN", MouseMoverTaskName);
    }

    public static async Task StartMouseMoverTaskAsync()
    {
        _ = await RunProcessAsync("schtasks.exe", false, "/Run", "/TN", MouseMoverTaskName);
    }

    private static (string Ac, string Dc) QueryPowerButtonAction()
    {
        try
        {
            var output = RunProcess("powercfg.exe", "/qh", "SCHEME_CURRENT", "SUB_BUTTONS", "PBUTTONACTION");
            var ac = ParsePowerCfgIndex(output, "AC");
            var dc = ParsePowerCfgIndex(output, "DC");
            return (DescribePowerButtonAction(ac), DescribePowerButtonAction(dc));
        }
        catch (Exception ex)
        {
            return ($"Unknown ({ex.Message})", "Unknown");
        }
    }

    private static (string Ac, string Dc) QueryPowerSetting(string subgroup, string setting, Func<int?, string> describe)
    {
        try
        {
            var output = RunProcess("powercfg.exe", "/qh", "SCHEME_CURRENT", subgroup, setting);
            var ac = ParsePowerCfgIndex(output, "AC");
            var dc = ParsePowerCfgIndex(output, "DC");
            return (describe(ac), describe(dc));
        }
        catch (Exception ex)
        {
            return ($"Unknown ({ex.Message})", "Unknown");
        }
    }

    private static int? ParsePowerCfgIndex(string output, string mode)
    {
        var match = Regex.Match(
            output,
            $@"Current {mode} Power Setting Index:\s*0x([0-9a-fA-F]+)",
            RegexOptions.IgnoreCase);

        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null;
    }

    private static string DescribePowerButtonAction(int? index)
    {
        return index switch
        {
            0 => "Do nothing",
            1 => "Sleep",
            2 => "Hibernate",
            3 => "Shut down",
            4 => "Turn off display",
            null => "Unknown",
            _ => $"Other ({index})"
        };
    }

    private static string DescribeSeconds(int? seconds)
    {
        if (seconds is null)
        {
            return "Unknown";
        }

        if (seconds == 0)
        {
            return "Never";
        }

        if (seconds % 3600 == 0)
        {
            return $"{seconds / 3600} hour(s)";
        }

        if (seconds % 60 == 0)
        {
            return $"{seconds / 60} minute(s)";
        }

        return $"{seconds} second(s)";
    }

    private static (bool Exists, string Status, string Command) QueryKeepAwakeTask()
    {
        var output = RunProcess("schtasks.exe", false, "/Query", "/TN", KeepAwakeTaskName, "/FO", "LIST", "/V");
        if (string.IsNullOrWhiteSpace(output))
        {
            return (false, "Not installed", "-");
        }

        var status = FindListValue(output, "Status");
        var command = FindListValue(output, "Task To Run");
        return (true, string.IsNullOrWhiteSpace(status) ? "Installed" : status, string.IsNullOrWhiteSpace(command) ? "-" : command);
    }

    public static (bool Exists, string Status, string Command) QueryMouseMoverTask()
    {
        var output = RunProcess("schtasks.exe", false, "/Query", "/TN", MouseMoverTaskName, "/FO", "LIST", "/V");
        if (string.IsNullOrWhiteSpace(output))
        {
            return (false, "Not installed", "-");
        }

        var status = FindListValue(output, "Status");
        var command = FindListValue(output, "Task To Run");
        return (true, string.IsNullOrWhiteSpace(status) ? "Installed" : status, string.IsNullOrWhiteSpace(command) ? "-" : command);
    }

    private static string BuildKeepAwakeTaskXml(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userName = WindowsIdentity.GetCurrent().Name;
        var workingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory;

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", userName),
                    new XElement(ns + "Description", "Keeps Windows from entering system idle using SetThreadExecutionState; does not simulate keyboard or mouse input.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userName),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--keep-awake"),
                        new XElement(ns + "WorkingDirectory", workingDirectory)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildMouseMoverTaskXml(string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var userName = WindowsIdentity.GetCurrent().Name;
        var workingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory;

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task",
                new XAttribute("version", "1.4"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Author", userName),
                    new XElement(ns + "Description", "Starts QemuGaGuard in system tray on logon.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userName),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executable),
                        new XElement(ns + "Arguments", "--tray"),
                        new XElement(ns + "WorkingDirectory", workingDirectory)))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string FindListValue(string output, string key)
    {
        foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var index = line.IndexOf(':');
            if (index < 0)
            {
                continue;
            }

            var name = line[..index].Trim();
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(index + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static bool IsPowerMenuHidden()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ExplorerPolicyPath, false);
        return key?.GetValue(NoCloseValueName) is int value && value != 0;
    }

    private static (string Status, string StartMode) QueryServiceState(string serviceName)
    {
        var output = RunProcess("sc.exe", false, "query", serviceName);
        if (output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase))
        {
            return ("Missing", "Unknown");
        }

        var qc = RunProcess("sc.exe", false, "qc", serviceName);
        return (ParseScState(output), ParseScStartMode(qc));
    }

    private static string ParseScState(string output)
    {
        var match = Regex.Match(output, @"STATE\s*:\s*\d+\s+(\S+)", RegexOptions.IgnoreCase);
        return match.Success ? ToTitleCase(match.Groups[1].Value) : "Unknown";
    }

    private static string ParseScStartMode(string output)
    {
        var match = Regex.Match(output, @"START_TYPE\s*:\s*\d+\s+(\S+)", RegexOptions.IgnoreCase);
        return match.Success ? ToTitleCase(match.Groups[1].Value) : "Unknown";
    }

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = value.Replace("_", " ", StringComparison.Ordinal).ToLowerInvariant();
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static async Task SetVmGuestShutdownEnabledAsync(bool enabled)
    {
        var service = QueryServiceState("vmicshutdown");
        if (service.Status == "Missing")
        {
            return;
        }

        if (enabled)
        {
            await RunProcessAsync("sc.exe", "config", "vmicshutdown", "start=", "demand");
        }
        else
        {
            _ = await RunProcessAsync("sc.exe", false, "stop", "vmicshutdown");
            await RunProcessAsync("sc.exe", "config", "vmicshutdown", "start=", "disabled");
        }
    }

    private static bool IsWindowsUpdateNoAutoRestartEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", false);
        return key?.GetValue("NoAutoRebootWithLoggedOnUsers") is int value && value != 0;
    }

    private static void SetWindowsUpdateNoAutoRestart(bool enabled)
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", true);
        if (key is null)
        {
            throw new InvalidOperationException("Cannot open Windows Update policy registry key.");
        }

        if (enabled)
        {
            key.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("NoAutoRebootWithLoggedOnUsers", false);
        }
    }

    private static void SetPowerMenuHidden(bool hidden)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ExplorerPolicyPath, true);
        if (key is null)
        {
            throw new InvalidOperationException("Cannot open Explorer policy registry key.");
        }

        if (hidden)
        {
            key.SetValue(NoCloseValueName, 1, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(NoCloseValueName, false);
        }
    }

    private static string ResolveExecutablePath()
    {
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? throw new InvalidOperationException("Cannot resolve current executable path.");
    }

    private static string RunProcess(string fileName, params string[] arguments)
    {
        return RunProcess(fileName, true, arguments);
    }

    private static string RunProcess(string fileName, bool throwOnFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static Task RunProcessAsync(string fileName, params string[] arguments)
    {
        return RunProcessAsync(fileName, true, arguments);
    }

    private static async Task<string> RunProcessAsync(string fileName, bool throwOnFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }

        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
