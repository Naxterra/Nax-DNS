using System.Text.RegularExpressions;
using BetterDns.Core.Configuration;

namespace BetterDns.Core.Routing;

public sealed class DomainRuleMatcher
{
    public DnsRule? Match(string domain, IEnumerable<DnsRule> rules)
    {
        var normalized = Normalize(domain);
        return rules.FirstOrDefault(rule => rule.Enabled && Matches(normalized, rule));
    }

    public bool Matches(string domain, DnsRule rule)
    {
        var normalized = Normalize(domain);
        var pattern = Normalize(rule.Pattern);

        return rule.MatchKind switch
        {
            DomainMatchKind.Exact => normalized.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            DomainMatchKind.Suffix => normalized.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                                      normalized.EndsWith('.' + pattern, StringComparison.OrdinalIgnoreCase),
            DomainMatchKind.Wildcard => Regex.IsMatch(
                normalized,
                "^" + Regex.Escape(pattern).Replace("\\*", "[^.]*", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)),
            DomainMatchKind.Regex => Regex.IsMatch(
                normalized,
                rule.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)),
            _ => false
        };
    }

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}
