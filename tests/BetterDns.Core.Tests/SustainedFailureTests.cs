using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Tests;

public sealed class SustainedFailureTests
{
    private static UpstreamDefinition Server(string id) => new() { Id = id, Name = id, Endpoint = "https://" + id + ".invalid/dns-query", Protocol = DnsProtocol.Doh3 };
    private static BetterDnsConfiguration Config() => new() { Active = true, DefaultChainId = "default", Upstreams = [Server("primary"), Server("backup")], Chains = [new() { Id = "default", Name = "Default", UpstreamIds = ["primary", "backup"] }] };

    [Fact]
    public async Task A_shorter_group_cannot_bypass_confirmation_for_a_shared_provider()
    {
        var clock = new Clock(); var health = new UpstreamHealthTracker(); var transport = new Transport();
        var config = Config();
        config = config with { Chains = [.. config.Chains, new() { Id = "other", Name = "Other", UpstreamIds = ["primary", "backup"], FailoverAfterSeconds = 0, FailureThreshold = 1 }],
            Rules = [new() { Name = "Other route", Pattern = "example.com", ChainId = "other" }] };
        using var router = new DnsRouter(health, new QueryLog(), [transport], clock);
        await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        Assert.DoesNotContain("backup", transport.Calls);
        Assert.Null(health.Snapshot(config.Upstreams)[0].CircuitOpenUntil);
    }

    [Fact]
    public async Task Five_minutes_of_confirmed_failures_are_required_before_any_backup_query()
    {
        var clock = new Clock(); var health = new UpstreamHealthTracker(); var log = new QueryLog(); var transport = new Transport(); var config = Config();
        using var router = new DnsRouter(health, log, [transport], clock);
        var query = DnsWire.CreateQuery("example.com");
        var failed = await router.ResolveAsync(query, config, CancellationToken.None);
        Assert.Equal("SERVFAIL", DnsWire.ResponseCodeName(failed));
        Assert.Equal("FAILOVER PENDING", Assert.Single(log.Snapshot()).Result);
        for (var i = 1; i < 30; i++) { clock.Advance(10); await router.ConfirmPendingFailuresAsync(config, CancellationToken.None); }
        Assert.DoesNotContain("backup", transport.Calls);
        Assert.Null(health.Snapshot(config.Upstreams)[0].CircuitOpenUntil);
        clock.Advance(10);
        await router.ConfirmPendingFailuresAsync(config, CancellationToken.None);
        Assert.NotNull(health.Snapshot(config.Upstreams)[0].CircuitOpenUntil);
        var answer = await router.ResolveAsync(query, config, CancellationToken.None);
        Assert.Equal("NOERROR", DnsWire.ResponseCodeName(answer));
        Assert.Equal("backup", transport.Calls.Last());
    }

    [Fact]
    public async Task Successful_confirmation_resets_failure_window_and_does_not_log_user_traffic()
    {
        var clock = new Clock(); var health = new UpstreamHealthTracker(); var log = new QueryLog(); var transport = new Transport(); var config = Config();
        using var router = new DnsRouter(health, log, [transport], clock);
        await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        clock.Advance(10); transport.PrimaryWorks = true;
        await router.ConfirmPendingFailuresAsync(config, CancellationToken.None);
        Assert.Null(health.Snapshot(config.Upstreams)[0].FailureStartedAt);
        Assert.Single(log.Snapshot());
        transport.PrimaryWorks = false; clock.Advance(10);
        await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), health.Snapshot(config.Upstreams)[0].FailureStartedAt);
        Assert.DoesNotContain("backup", transport.Calls);
    }

    [Fact]
    public void Idle_gap_is_not_mistaken_for_five_minutes_of_failed_observations()
    {
        var clock = new Clock(); var health = new UpstreamHealthTracker(); var server = Server("primary"); var chain = Config().Chains[0];
        Assert.False(health.Fail(health.TryBeginAttempt(server, clock.GetUtcNow())!, chain, "timeout", clock.GetUtcNow()));
        clock.Advance(600);
        Assert.False(health.Fail(health.TryBeginAttempt(server, clock.GetUtcNow())!, chain, "timeout", clock.GetUtcNow()));
        Assert.Equal(clock.GetUtcNow(), health.Snapshot([server])[0].FailureStartedAt);
    }

    [Fact]
    public async Task Healthy_or_inactive_configuration_does_not_trigger_background_DNS_requests()
    {
        var clock = new Clock(); var health = new UpstreamHealthTracker(); var transport = new Transport(); var config = Config();
        using var router = new DnsRouter(health, new QueryLog(), [transport], clock);
        await router.ConfirmPendingFailuresAsync(config, CancellationToken.None);
        Assert.Empty(transport.Calls);
        await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        transport.Calls.Clear(); clock.Advance(10);
        await router.ConfirmPendingFailuresAsync(config with { Active = false }, CancellationToken.None);
        Assert.Empty(transport.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class Transport : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh3;
        public List<string> Calls { get; } = [];
        public bool PrimaryWorks { get; set; }
        public Task<byte[]> QueryAsync(UpstreamDefinition server, ReadOnlyMemory<byte> query, CancellationToken cancellationToken)
        {
            Calls.Add(server.Id);
            if (server.Id == "primary" && !PrimaryWorks) throw new TimeoutException();
            return Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 0));
        }
    }
}
