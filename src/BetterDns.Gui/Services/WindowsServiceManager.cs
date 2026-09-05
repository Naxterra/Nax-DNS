using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BetterDns.Gui.Services;

public enum WindowsServiceState { Unknown, Missing, Stopped, Running, Pending }
public enum ServiceOperation { Start, Stop, Install, Uninstall }

public interface IWindowsServiceManager
{
    bool CanManage { get; }
    WindowsServiceState GetState();
    Task ExecuteAsync(ServiceOperation operation);
}

public sealed class WindowsServiceManager : IWindowsServiceManager
{
    private readonly string installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private string ScriptPath => Path.Combine(installRoot, "Installer", "manage-service.ps1");
    public bool CanManage => File.Exists(ScriptPath) && File.Exists(Path.Combine(installRoot, "Service", "BetterDns.Service.exe"));

    public WindowsServiceState GetState()
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(scm, "BetterDNS", 4);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return WindowsServiceState.Missing;
                throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState switch { 1 => WindowsServiceState.Stopped, 4 => WindowsServiceState.Running, _ => WindowsServiceState.Pending };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public async Task ExecuteAsync(ServiceOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (!CanManage) throw new InvalidOperationException("The installed BetterDNS service payload is missing.");
        var log = Path.Combine(Path.GetTempPath(), "BetterDNS-service-" + Guid.NewGuid().ToString("N") + ".log");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath,
            "-InstallRoot", installRoot, "-Action", operation.ToString(), "-LogPath", log }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch service management.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"Service operation failed (exit {process.ExitCode}). Log: {log}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
