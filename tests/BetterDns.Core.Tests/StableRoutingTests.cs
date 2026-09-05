using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;
using BetterDns.Core.Routing;
using BetterDns.Core.Transports;

namespace BetterDns.Core.Tests;

public sealed class StableRoutingTests
{
    private static UpstreamDefinition Server(string id) => new() { Id = id, Name = id, Protocol = DnsProtocol.Doh, Endpoint = "https://" + id + ".invalid/dns-query", TimeoutMilliseconds = 250 };
    private static FailoverChain Chain(params string[] ids) => new() { Id = "default", Name = "Default", UpstreamIds = ids, FailureThreshold = 1, CooldownSeconds = 30 };
    private static BetterDnsConfiguration Config(params UpstreamDefinition[] servers) => new() { DefaultChainId = "default", Upstreams = servers, Chains = [Chain(servers.Select(server => server.Id).ToArray())] };

    [Theory]
    [InlineData(0)] [InlineData(3)] [InlineData(5)] [InlineData(2)]
    public async Task Valid_DNS_answers_do_not_trigger_fallback_or_mark_server_down(byte code)
    {
        var transport = new FakeTransport((_, query, _) => Task.FromResult(DnsWire.CreateErrorResponse(query.Span, code)));
        var log = new QueryLog(); var health = new UpstreamHealthTracker();
        var config = Config(Server("primary"), Server("backup"));
        using var router = new DnsRouter(health, log, [transport]);
        var response = await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        Assert.Equal(code, response[3] & 15);
        Assert.Equal(["primary"], transport.Calls);
        Assert.Null(health.Snapshot(config.Upstreams)[0].CircuitOpenUntil);
        Assert.Equal(DnsWire.ResponseCodeName(response), Assert.Single(Assert.Single(log.Snapshot()).Attempts!).DnsResponse);
    }

    [Fact]
    public void Late_success_cannot_clear_an_open_circuit_and_only_one_recovery_is_allowed()
    {
        var health = new UpstreamHealthTracker(); var server = Server("primary"); var chain = Chain("primary");
        var now = DateTimeOffset.UtcNow;
        var failing = health.TryBeginAttempt(server, now)!;
        var oldSuccess = health.TryBeginAttempt(server, now)!;
        health.Fail(failing, chain, "timeout", now);
        health.Succeed(oldSuccess, TimeSpan.FromMilliseconds(10), now.AddSeconds(1), "NOERROR");
        Assert.Equal(now.AddSeconds(30), Assert.Single(health.Snapshot([server])).CircuitOpenUntil);
        Assert.Null(health.TryBeginAttempt(server, now.AddSeconds(5)));
        var recovery = health.TryBeginAttempt(server, now.AddSeconds(31));
        Assert.NotNull(recovery);
        Assert.True(recovery.Recovery);
        Assert.Null(health.TryBeginAttempt(server, now.AddSeconds(31)));
        health.Succeed(recovery, TimeSpan.FromMilliseconds(12), now.AddSeconds(31), "NOERROR");
        Assert.Null(Assert.Single(health.Snapshot([server])).CircuitOpenUntil);
    }

    [Fact]
    public void Late_failures_cannot_extend_a_cooldown_or_poison_a_recovered_server()
    {
        var health = new UpstreamHealthTracker(); var server = Server("primary"); var chain = Chain("primary");
        var now = DateTimeOffset.UtcNow;
        var first = health.TryBeginAttempt(server, now)!; var late = health.TryBeginAttempt(server, now)!;
        health.Fail(first, chain, "timeout", now);
        health.Fail(late, chain, "network", now.AddSeconds(20));
        Assert.Equal(now.AddSeconds(30), Assert.Single(health.Snapshot([server])).CircuitOpenUntil);
        var recovery = health.TryBeginAttempt(server, now.AddSeconds(31))!;
        health.Succeed(recovery, TimeSpan.FromMilliseconds(10), now.AddSeconds(31), "NOERROR");
        health.Fail(late, chain, "timeout", now.AddSeconds(32));
        Assert.Null(Assert.Single(health.Snapshot([server])).FailureCode);
    }

    [Fact]
    public void Failed_attempt_clears_stale_latency_and_records_a_timestamp()
    {
        var health = new UpstreamHealthTracker(); var server = Server("primary"); var now = DateTimeOffset.UtcNow;
        health.RecordSuccess(server, TimeSpan.FromMilliseconds(15));
        health.Fail(health.TryBeginAttempt(server, now)!, Chain("primary"), "timeout", now);
        var result = Assert.Single(health.Snapshot([server]));
        Assert.Null(result.LastLatencyMilliseconds); Assert.Equal(now, result.LastChecked); Assert.Equal("timeout", result.FailureCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_provider_failure()
    {
        using var canceled = new CancellationTokenSource();
        var transport = new FakeTransport((_, _, _) => { canceled.Cancel(); throw new OperationCanceledException(canceled.Token); });
        var health = new UpstreamHealthTracker(); var server = Server("primary");
        using var router = new DnsRouter(health, new QueryLog(), [transport]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.ResolveAsync(DnsWire.CreateQuery("example.com"), Config(server), canceled.Token));
        Assert.Equal(0, Assert.Single(health.Snapshot([server])).ConsecutiveFailures);
    }

    [Fact]
    public async Task Local_cancellation_followed_by_IO_error_does_not_mark_provider_down()
    {
        using var canceled = new CancellationTokenSource();
        var transport = new FakeTransport((_, _, _) => { canceled.Cancel(); throw new IOException("Local connection disposed."); });
        var health = new UpstreamHealthTracker(); var server = Server("primary");
        using var router = new DnsRouter(health, new QueryLog(), [transport]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.ResolveAsync(DnsWire.CreateQuery("example.com"), Config(server), canceled.Token));
        Assert.Null(Assert.Single(health.Snapshot([server])).FailureCode);
    }

    [Fact]
    public async Task Transport_timeout_falls_back_with_a_recorded_reason()
    {
        var transport = new FakeTransport((server, query, _) => server.Id == "primary"
            ? Task.FromException<byte[]>(new TaskCanceledException()) : Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 0)));
        var log = new QueryLog();
        using var router = new DnsRouter(new UpstreamHealthTracker(), log, [transport]);
        await router.ResolveAsync(DnsWire.CreateQuery("example.com"), Config(Server("primary"), Server("backup")), CancellationToken.None);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal("backup", entry.UpstreamId);
        Assert.Equal("timeout", entry.Attempts![0].FailureCode);
        Assert.Equal("NOERROR", entry.Attempts[1].DnsResponse);
    }

    [Fact]
    public async Task All_circuits_open_fail_closed_instead_of_hammering_primary()
    {
        var server = Server("primary"); var config = Config(server); var health = new UpstreamHealthTracker();
        health.RecordFailure(server, config.Chains[0], new IOException(), DateTimeOffset.UtcNow);
        var transport = new FakeTransport((_, _, _) => throw new InvalidOperationException("Must not call server during cooldown."));
        using var router = new DnsRouter(health, new QueryLog(), [transport]);
        var response = await router.ResolveAsync(DnsWire.CreateQuery("example.com"), config, CancellationToken.None);
        Assert.Equal("SERVFAIL", DnsWire.ResponseCodeName(response)); Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Manual_probe_measures_real_transport_without_clearing_circuit_or_logging_traffic()
    {
        var server = Server("primary"); var health = new UpstreamHealthTracker(); var log = new QueryLog();
        health.RecordFailure(server, Chain("primary"), new IOException(), DateTimeOffset.UtcNow);
        var transport = new FakeTransport((_, query, _) => Task.FromResult(DnsWire.CreateErrorResponse(query.Span, 0)));
        using var router = new DnsRouter(health, log, [transport]);
        var result = await router.ProbeAsync(server, CancellationToken.None);
        Assert.Equal(["primary"], transport.Calls); Assert.NotNull(result.LatencyMilliseconds); Assert.Null(result.FailureCode);
        Assert.NotNull(Assert.Single(health.Snapshot([server])).CircuitOpenUntil); Assert.Empty(log.Snapshot());
        Assert.Equal(result, Assert.Single(router.ProbeSnapshot([server])));
        Assert.Empty(router.ProbeSnapshot([server with { Endpoint = "https://other.invalid/dns-query" }]));
    }

    [Fact]
    public async Task Manual_probe_timeout_has_no_stale_latency()
    {
        var transport = new FakeTransport((_, _, _) => Task.FromException<byte[]>(new TaskCanceledException()));
        using var router = new DnsRouter(new UpstreamHealthTracker(), new QueryLog(), [transport]);
        var result = await router.ProbeAsync(Server("primary"), CancellationToken.None);
        Assert.Null(result.LatencyMilliseconds); Assert.Equal("timeout", result.FailureCode);
    }

    [Fact]
    public async Task Disabled_probe_never_uses_the_network()
    {
        var transport = new FakeTransport((_, _, _) => throw new InvalidOperationException());
        using var router = new DnsRouter(new UpstreamHealthTracker(), new QueryLog(), [transport]);
        var result = await router.ProbeAsync(Server("primary") with { Enabled = false }, CancellationToken.None);
        Assert.Equal("disabled", result.FailureCode); Assert.Empty(transport.Calls);
    }

    private sealed class FakeTransport(Func<UpstreamDefinition, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> query) : IDnsTransport
    {
        public DnsProtocol Protocol => DnsProtocol.Doh;
        public List<string> Calls { get; } = [];
        public Task<byte[]> QueryAsync(UpstreamDefinition upstream, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        { Calls.Add(upstream.Id); return query(upstream, request, cancellationToken); }
    }
}
