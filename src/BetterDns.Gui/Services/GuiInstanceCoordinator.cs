using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace BetterDns.Gui.Services;

public sealed class GuiInstanceCoordinator : IDisposable
{
    public const string ActivateCommand = "activate";
    public const string ExitForUpdateCommand = "exit-for-update";
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    private NamedPipeServerStream? server;
    private Task? listening;

    public GuiInstanceCoordinator(string? name = null)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value.Replace('-', '_') ?? Environment.UserName;
        pipeName = name ?? "BetterDNS.Gui.Instance." + user;
    }

    public bool TryOwnInstance()
    {
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public void StartListening(Action<string> commandReceived)
    {
        if (server is null) throw new InvalidOperationException("This process does not own the GUI instance pipe.");
        if (listening is not null) throw new InvalidOperationException("The GUI instance listener is already running.");
        listening = ListenAsync(server, commandReceived, stop.Token);
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { return false; }
    }

    private static async Task ListenAsync(NamedPipeServerStream server, Action<string> commandReceived, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8, false, leaveOpen: true);
                var command = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (command is not null) commandReceived(command);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (IOException) when (token.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
            finally
            {
                try { if (server.IsConnected) server.Disconnect(); }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        server?.Dispose();
        stop.Dispose();
    }
}
