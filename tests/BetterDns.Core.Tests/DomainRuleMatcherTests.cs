using BetterDns.Core.Configuration;
using BetterDns.Core.Routing;

namespace BetterDns.Core.Tests;

public sealed class DomainRuleMatcherTests
{
    private readonly DomainRuleMatcher matcher = new();

    [Theory]
    [InlineData("example.com", DomainMatchKind.Exact, "example.com", true)]
    [InlineData("www.example.com", DomainMatchKind.Exact, "example.com", false)]
    [InlineData("www.example.com", DomainMatchKind.Suffix, "example.com", true)]
    [InlineData("notexample.com", DomainMatchKind.Suffix, "example.com", false)]
    [InlineData("api.example.com", DomainMatchKind.Wildcard, "*.example.com", true)]
    [InlineData("deep.api.example.com", DomainMatchKind.Wildcard, "*.example.com", false)]
    public void Match_modes_respect_dns_label_boundaries(
        string domain,
        DomainMatchKind kind,
        string pattern,
        bool expected)
    {
        var rule = new DnsRule { Name = "test", Pattern = pattern, MatchKind = kind };

        Assert.Equal(expected, matcher.Matches(domain, rule));
    }
}
