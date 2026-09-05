using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Ipc;
using Divert.Windows;

if (args.Length == 2 && args[0] == "--ci-select-primary")
{
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return 2;
    var control = new ControlClient();
    var state = await control.SendAsync<ServiceSnapshot>("getState", false);
    var probes = await control.SendAsync<IReadOnlyList<ResolverProbeResult>>("testUpstreams", false);
    var chain = state.Configuration.Chains.Single(value => value.Id == state.Configuration.DefaultChainId);
    var usable = chain.UpstreamIds.FirstOrDefault(id => probes.Any(probe => probe.UpstreamId == id && probe.FailureCode is null && probe.DnsResponse is "NOERROR" or "NXDOMAIN"));
    if (usable is null) throw new InvalidOperationException("No strict-DoH3 resolver answered on the CI network.");
    var configuration = state.Configuration with
    {
        Chains = state.Configuration.Chains.Select(value => value.Id == chain.Id
            ? value with { UpstreamIds = new[] { usable }.Concat(value.UpstreamIds.Where(id => id != usable)).ToArray() } : value).ToArray()
    };
    await control.SendAsync<BetterDnsConfiguration>("saveConfiguration", configuration);
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { Selected = usable, Probes = probes }, JsonSettings.File));
    return 0;
}

if (args.Length is < 2 or > 3 || !IPAddress.TryParse(args[0], out var targetAddress))
{
    Console.Error.WriteLine("Usage (elevated): BetterDns.InterceptionProbe <DNS target IP> <result.json> [query name]");
    return 2;
}

var records = new List<object>();
var client = new ControlClient();
var queryName = args.Length == 3 ? args[2] : $"bdns-{Guid.NewGuid():N}.example.com";
var query = DnsWire.CreateQuery(queryName);
var result = new Dictionary<string, object?> { ["Domain"] = queryName, ["Target"] = args[0], ["CapturedPackets"] = records };
var restore = false;
using var totalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
var token = totalTimeout.Token;
try
{
    NativeLibrary.SetDllImportResolver(typeof(DivertService).Assembly, (name, _, _) =>
        name.Equals("WinDivert.dll", StringComparison.OrdinalIgnoreCase)
            ? NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "BetterDNS", "Service", "WinDivert-2.2.2", "runtimes", "win-x64", "native", "WinDivert.dll"))
            : IntPtr.Zero);
    using var observer = new DivertService((DivertFilter)"udp and (udp.DstPort == 53 or udp.SrcPort == 53)",
        DivertLayer.Network, (short)(DivertService.HighestPriority - 1), DivertFlags.Sniff | DivertFlags.ReceiveOnly);
    using var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    var capture = Task.Run(async () =>
    {
        try
        {
            while (!captureTimeout.IsCancellationRequested)
            {
                var packet = new byte[65535];
                var addresses = new DivertAddress[1];
                var (length, count) = await observer.ReceiveAsync(packet, addresses, captureTimeout.Token);
                if (count != 1 || !UdpPacketRewriter.TryGetDnsPayload(packet.AsSpan(0, length), out var payload)) continue;
                try
                {
                    if (DnsWire.ReadFirstQuestion(payload).Name != queryName) continue;
                    var v6 = (packet[0] >> 4) == 6;
                    var source = new IPAddress(packet.AsSpan(v6 ? 8 : 12, v6 ? 16 : 4));
                    var destination = new IPAddress(packet.AsSpan(v6 ? 24 : 16, v6 ? 16 : 4));
                    var address = addresses[0];
                    records.Add(new { Source = source.ToString(), Destination = destination.ToString(),
                        address.IsOutbound, address.IsImpostor, address.IsLoopback,
                        Interface = address.GetNetworkData().InterfaceIndex, Response = (payload[2] & 128) != 0 });
                }
                catch (InvalidDataException) { }
            }
        }
        catch (OperationCanceledException) { }
    }, token);

    try
    {
        var initial = await client.SendAsync<JsonElement>("getState", false, token);
        result["Version"] = initial.GetProperty("version").GetString();
        result["OriginalActive"] = initial.GetProperty("configuration").GetProperty("active").GetBoolean();
        if (!(bool)result["OriginalActive"]!)
        {
            restore = true;
            await client.SendAsync<JsonElement>("setActive", true, token);
        }
        result["ActiveBeforeQuery"] = (await client.SendAsync<JsonElement>("getState", false, token))
            .GetProperty("configuration").GetProperty("active").GetBoolean();
        var sentAt = DateTimeOffset.UtcNow;
        using var udp = new UdpClient(targetAddress.AddressFamily);
        await udp.SendAsync(query, new IPEndPoint(targetAddress, 53), token);
        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var reply = await udp.ReceiveAsync(receiveTimeout.Token);
            result["ReplyEndpoint"] = reply.RemoteEndPoint.ToString();
            result["ValidReply"] = DnsWire.MatchesQuestion(query, reply.Buffer);
            result["Rcode"] = DnsWire.ResponseCodeName(reply.Buffer);
        }
        catch (OperationCanceledException) { result["ReceiveTimeout"] = true; }
        await Task.Delay(250, token);
        var after = await client.SendAsync<JsonElement>("getState", false, token);
        result["Enforcement"] = after.GetProperty("enforcement");
        var entries = after.GetProperty("queries").EnumerateArray()
            .Where(entry => entry.GetProperty("domain").GetString() == queryName &&
                entry.GetProperty("timestamp").GetDateTimeOffset() >= sentAt)
            .Select(entry => entry.Clone()).ToArray();
        result["QueryLog"] = entries;
        result["InterceptionVerified"] = result.TryGetValue("ValidReply", out var valid) && valid is true && entries.Length > 0;
    }
    finally
    {
        captureTimeout.Cancel();
        await capture;
    }
}
catch (Exception error) { result["Error"] = error.ToString(); }
finally
{
    if (restore)
    {
        try
        {
            await client.SendAsync<JsonElement>("setActive", false);
            result["Restored"] = true;
        }
        catch (Exception error) { result["RestoreError"] = error.ToString(); }
    }
    File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonSettings.File));
}
return result.TryGetValue("InterceptionVerified", out var verified) && verified is true ? 0 : 1;
