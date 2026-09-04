using System.Diagnostics;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Transports;

var defaults = DefaultConfiguration.Create();
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
