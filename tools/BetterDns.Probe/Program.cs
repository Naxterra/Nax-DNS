using System.Diagnostics;
using System.Text.Json;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Transports;

var defaults = DefaultConfiguration.Create();
if (args is ["--saved-primary"])
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterDNS", "config.json");
    var configuration = JsonSerializer.Deserialize<BetterDnsConfiguration>(File.ReadAllText(path), JsonSettings.File)
        ?? throw new InvalidDataException("BetterDNS configuration is empty.");
    var chain = configuration.Chains.Single(value => value.Id == configuration.DefaultChainId);
    var upstream = configuration.Upstreams.Single(value => value.Id == chain.UpstreamIds[0]);
    using var transport = new DohTransport(upstream.Protocol);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var clock = Stopwatch.StartNew();
    try
    {
        var response = await transport.QueryAsync(upstream, DnsWire.CreateQuery("checkip.windscribe.com"), timeout.Token);
        Console.WriteLine($"PASS {upstream.Protocol} {upstream.Name} {DnsWire.ResponseCodeName(response)} {clock.ElapsedMilliseconds} ms");
        return 0;
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {upstream.Protocol} {upstream.Name} {error.GetType().Name}: {error.Message}");
        return 1;
    }
}
if (args is ["--saved-primary-paths"])
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterDNS", "config.json");
    var configuration = JsonSerializer.Deserialize<BetterDnsConfiguration>(File.ReadAllText(path), JsonSettings.File)
        ?? throw new InvalidDataException("BetterDNS configuration is empty.");
    var chain = configuration.Chains.Single(value => value.Id == configuration.DefaultChainId);
    var upstream = configuration.Upstreams.Single(value => value.Id == chain.UpstreamIds[0]);
    var pathFailures = 0;
    foreach (var address in upstream.ParsedBootstrapAddresses)
    {
        foreach (var protocol in new[] { DnsProtocol.Doh3, DnsProtocol.Doh })
        {
            using var transport = new DohTransport(protocol);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var clock = Stopwatch.StartNew();
            try
            {
                var response = await transport.QueryAsync(upstream with { Protocol = protocol, BootstrapAddresses = [address.ToString()], TimeoutMilliseconds = 4000 },
                    DnsWire.CreateQuery("checkip.windscribe.com"), timeout.Token);
                Console.WriteLine($"PASS {protocol} {address} {DnsWire.ResponseCodeName(response)} {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception error)
            {
                pathFailures++;
                Console.WriteLine($"FAIL {protocol} {address} {error.GetType().Name}: {error.Message}");
            }
        }
    }
    return pathFailures;
}
var hagezi = defaults.Upstreams.Single(value => value.Id == "hagezi-root");
var probes = new List<UpstreamDefinition>
{
    hagezi,
    defaults.Upstreams.Single(value => value.Id == "cloudflare-security"),
    defaults.Upstreams.Single(value => value.Id == "quad9-secure"),
    defaults.Upstreams.Single(value => value.Id == "google-public"),
    hagezi with { Id = "hagezi-doh", Name = "HaGeZi DoH", Protocol = DnsProtocol.Doh },
    hagezi with { Id = "hagezi-dot", Name = "HaGeZi DoT", Protocol = DnsProtocol.Dot, Endpoint = "root.hagezi.org" },
    hagezi with { Id = "hagezi-doq", Name = "HaGeZi DoQ", Protocol = DnsProtocol.Doq, Endpoint = "root.hagezi.org" }
};

var transports = new Dictionary<DnsProtocol, IDnsTransport>
{
    [DnsProtocol.Doh] = new DohTransport(DnsProtocol.Doh),
    [DnsProtocol.Doh3] = new DohTransport(DnsProtocol.Doh3),
    [DnsProtocol.Dot] = new DotTransport(),
    [DnsProtocol.Doq] = new DoqTransport()
};
var failures = 0;

foreach (var upstream in probes)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var response = await transports[upstream.Protocol]
            .QueryAsync(upstream, DnsWire.CreateQuery("example.com"), timeout.Token)
            .ConfigureAwait(false);
        Console.WriteLine($"PASS {upstream.Protocol,-4} {upstream.Name,-34} {DnsWire.ResponseCodeName(response),-8} {stopwatch.ElapsedMilliseconds,5} ms");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL {upstream.Protocol,-4} {upstream.Name,-34} {error.GetType().Name}: {error.Message}");
    }
}

foreach (var disposable in transports.Values.OfType<IDisposable>()) disposable.Dispose();
return failures == 0 ? 0 : 1;
