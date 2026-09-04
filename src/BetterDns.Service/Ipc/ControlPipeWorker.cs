using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;

namespace BetterDns.Service.Ipc;

public sealed class ControlPipeWorker(
    ConfigurationStore configurationStore,
    UpstreamHealthTracker health,
    QueryLog queryLog,
    DnsRouter router,
    EnforcementState enforcementState,
    ControlPipeOptions options,
    ILogger<ControlPipeWorker> logger) : BackgroundService
{
    public const string PipeName = "BetterDNS.Control";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = options.Diagnostic
                ? new NamedPipeServerStream(options.Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)
                : NamedPipeServerStreamAcl.Create(
                options.Name,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                CreatePipeSecurity());

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken = timeout.Token;
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<ControlRequest>(line ?? string.Empty, JsonSettings.Wire)
                    ?? throw new InvalidDataException("Control request was empty.");
                var response = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonSettings.Wire).AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Control request failed.");
                var response = new ControlResponse(false, Error: error.Message);
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonSettings.Wire).AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command.ToLowerInvariant())
        {
            case "getstate":
                var configuration = configurationStore.Current;
                var snapshot = new ServiceSnapshot(
                    typeof(ControlPipeWorker).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                    configuration,
                    health.Snapshot(configuration.Upstreams),
                    queryLog.Snapshot(),
                    enforcementState.Snapshot());
                return new(true, snapshot);

            case "saveconfiguration":
                var updated = request.Payload.Deserialize<BetterDnsConfiguration>(configurationStore.JsonOptions)
                    ?? throw new InvalidDataException("Configuration payload was empty.");
                if (updated.Active != configurationStore.Current.Active ||
                    (updated.Active && updated.Enforcement.Enabled != configurationStore.Current.Enforcement.Enabled))
                    throw new InvalidDataException("Use setActive to change protection; configuration saves cannot bypass activation checks.");
                configurationStore.Save(updated);
                return new(true, updated);

            case "setactive":
                var active = request.Payload.GetBoolean();
                if (active)
                {
                    if (options.Diagnostic)
                        throw new InvalidOperationException("Protection cannot be enabled in diagnostic mode.");
                    if (!enforcementState.Snapshot().DriverReady)
                    {
                        throw new InvalidOperationException("Protection was not enabled because the kernel DNS interception driver is not ready.");
                    }

                    var probe = await router.ResolveAsync(
                        DnsWire.CreateQuery("example.com"),
                        configurationStore.Current,
                        cancellationToken).ConfigureAwait(false);
                    if (!DnsWire.IsUsableResponse(probe))
                    {
                        throw new InvalidOperationException("Protection was not enabled because every resolver in the default chain failed its preflight query.");
                    }
                }

                var changed = configurationStore.Current with { Active = active };
                configurationStore.Save(changed);
                return new(true, changed);

            default:
                return new(false, Error: $"Unknown command '{request.Command}'.");
        }
    }
}
