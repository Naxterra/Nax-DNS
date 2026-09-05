using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace BetterDns.Gui.Services;

// The visible UI stays at medium integrity. Only this explicitly approved,
// windowless session can connect to the existing administrator-only service pipe.
public sealed class PrivilegedControlSession : IControlClient, IWindowsServiceManager, IDisposable
{
    private const string Prefix = "BetterDNS.Broker.";
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Process helper;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly WindowsServiceManager serviceManager = new();
    private bool disposed;
    private bool loggedConnection;

    private PrivilegedControlSession(NamedPipeServerStream pipe, Process helper)
    {
        this.pipe = pipe;
        this.helper = helper;
        reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public static async Task<PrivilegedControlSession> StartAsync()
    {
        var name = Prefix + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        Process? helper = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("BetterDNS executable path unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--control-broker");
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            helper = await Task.Run(() => Process.Start(start)) ?? throw new IOException("Could not start the BetterDNS administrative helper.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Ignore unrelated clients without giving them a request channel.
            while (true)
            {
                await server.WaitForConnectionAsync(timeout.Token);
                if (GetNamedPipeClientProcessId(server.SafePipeHandle, out var id) && id == helper.Id) break;
                server.Disconnect();
            }
            return new PrivilegedControlSession(server, helper);
        }
        catch
        {
            server.Dispose();
            helper?.Dispose();
            throw;
        }
    }

    public bool CanManage => serviceManager.CanManage;
    public WindowsServiceState GetState() => serviceManager.GetState();
    public async Task ExecuteAsync(ServiceOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        await SendAsync<bool>("serviceOperation", operation);
    }

    public async Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsAllowedCommand(command)) throw new ArgumentException("Unsupported administrative command.", nameof(command));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var request = new Request(command, JsonSerializer.SerializeToElement(payload ?? false, JsonSettings.Wire));
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonSettings.Wire).AsMemory(), timeout.Token);
            var response = JsonSerializer.Deserialize<Response>(await reader.ReadLineAsync(timeout.Token)
                ?? throw new EndOfStreamException("BetterDNS administrative session ended."), JsonSettings.Wire)
                ?? throw new InvalidDataException("Empty administrative response.");
            if (!response.Success)
            {
                var error = new InvalidOperationException(response.Error ?? "Administrative operation failed.");
                error.Data["ErrorCode"] = response.ErrorCode ?? "operation-rejected";
                throw error;
            }
            var result = response.Data.Deserialize<T>(JsonSettings.Wire) ?? throw new InvalidDataException("Empty response data.");
            if (command == "getState" && !loggedConnection)
            {
                loggedConnection = true;
                App.LogStartup("service snapshot received through authenticated administrative helper");
            }
            return result;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException)
        {
            // Never reuse a stream after a cancelled request: its late response
            // could otherwise be mistaken for the next operation's result.
            Dispose();
            throw;
        }
        finally { gate.Release(); }
    }

    public static bool IsAllowedCommand(string command) => command is
        "getState" or "testUpstreams" or "saveConfiguration" or "setActive" or "serviceOperation";

    public static async Task<int> RunBrokerAsync(string pipeName, int parentId)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        if (!pipeName.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName[Prefix.Length..], "N", out _) || parentId <= 0) return 87;
        using var parent = Process.GetProcessById(parentId);
        // Only the same BetterDNS executable may request this fixed-operation broker.
        if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 5;
        using var life = new CancellationTokenSource();
        parent.EnableRaisingEvents = true;
        parent.Exited += (_, _) => life.Cancel();
        if (parent.HasExited) return 0;
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(life.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(connect.Token);
        }
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverId) || serverId != parentId) return 5;
        using var input = new StreamReader(client, Encoding.UTF8, false, leaveOpen: true);
        await using var output = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var control = new ControlClient();
        var manager = new WindowsServiceManager();
        while (!life.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(life.Token);
            if (line is null) break;
            Response response;
            try
            {
                var request = JsonSerializer.Deserialize<Request>(line, JsonSettings.Wire)
                    ?? throw new InvalidDataException("Empty request.");
                if (!IsAllowedCommand(request.Command)) throw new InvalidDataException("Unsupported administrative command.");
                JsonElement data;
                if (request.Command == "serviceOperation")
                {
                    var operation = request.Payload.Deserialize<ServiceOperation>(JsonSettings.Wire);
                    if (!Enum.IsDefined(operation)) throw new InvalidDataException("Unsupported service operation.");
                    await manager.ExecuteAsync(operation);
                    data = JsonSerializer.SerializeToElement(true);
                }
                else data = await control.SendAsync<JsonElement>(request.Command, request.Payload, life.Token);
                response = new Response(true, data, null, null);
            }
            catch (Exception error) when (!life.IsCancellationRequested)
            {
                response = new Response(false, JsonSerializer.SerializeToElement(false), error.Message,
                    error.Data["ErrorCode"] as string);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(response, JsonSettings.Wire).AsMemory(), life.Token);
        }
        return 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pipe.Dispose(); // EOF ends the helper; it must not survive the UI session.
        helper.Dispose();
    }

    private sealed record Request(string Command, JsonElement Payload);
    private sealed record Response(bool Success, JsonElement Data, string? Error, string? ErrorCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
}
