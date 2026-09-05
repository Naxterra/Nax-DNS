using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterDns.Core.Configuration;
using BetterDns.Core.Ipc;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Service.Configuration;
using BetterDns.Service.Enforcement;
using BetterDns.Service.LocalDns;

namespace BetterDns.Service.Ipc;

public sealed class ControlPipeWorker(
    ConfigurationStore configurationStore,
    UpstreamHealthTracker health,
    QueryLog queryLog,
    DnsRouter router,
    EnforcementState enforcementState,
    LocalDnsState localDnsState,
    ControlPipeOptions options,
    ILogger<ControlPipeWorker> logger) : BackgroundService
{
    public const string PipeName = "BetterDNS.Control";
    private readonly SemaphoreSlim probeGate = new(1, 1);

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
                var response = new ControlResponse(false, Error: error.Message,
                    ErrorCode: error is InvalidDataException or ArgumentException ? "configuration-invalid" : "operation-rejected");
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
                    enforcementState.Snapshot(),
                    router.ProbeSnapshot(configuration.Upstreams),
                    localDnsState.Snapshot());
                return new(true, snapshot);

            case "testupstreams":
                if (!await probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return new(false, Error: "probe-busy", ErrorCode: "probe-busy");
                try
                {
                    var providers = configurationStore.Current.Upstreams;
                    using var concurrency = new SemaphoreSlim(4, 4);
                    var results = await Task.WhenAll(providers.Select(async upstream =>
                    {
                        await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try { return await router.ProbeAsync(upstream, cancellationToken).ConfigureAwait(false); }
                        finally { concurrency.Release(); }
                    })).ConfigureAwait(false);
                    return new(true, results);
                }
                finally { probeGate.Release(); }

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
                    if (!configurationStore.Current.Enforcement.Enabled)
                        throw new InvalidOperationException("Enable DNS enforcement in configuration before activating protection.");
                    if (!enforcementState.Snapshot().DriverReady)
                    {
                        throw new InvalidOperationException("Protection was not enabled because the kernel DNS interception driver is not ready.");
                    }
                    if (!localDnsState.Snapshot().Ready)
                        return new(false, Error: "Local DNS listener is not ready.", ErrorCode: "local-dns-unavailable");

                    var probe = await router.ResolveAsync(
                        DnsWire.CreateQuery("example.com"),
                        configurationStore.Current,
                        cancellationToken).ConfigureAwait(false);
                    if (!DnsWire.IsUsableResponse(probe))
                    {
                        throw new InvalidOperationException("Protection was not enabled because the default route did not return a successful DNS answer to its preflight query.");
                    }
                }

                var changed = configurationStore.Current with { Active = active };
                configurationStore.Save(changed);
                if (!options.Diagnostic)
                {
                    try
                    {
                        using var transition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        transition.CancelAfter(TimeSpan.FromSeconds(10));
                        while (enforcementState.Snapshot() is var status &&
                               (!status.DriverReady || status.Active != active))
                        {
                            if (active && status.LastError is not null)
                                throw new InvalidOperationException(status.LastError);
                            await Task.Delay(50, transition.Token).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        if (active) configurationStore.Save(changed with { Active = false });
                        throw;
                    }
                }
                return new(true, changed);

            default:
                return new(false, Error: $"Unknown command '{request.Command}'.");
        }
    }
}
