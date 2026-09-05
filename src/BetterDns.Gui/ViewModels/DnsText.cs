using BetterDns.Gui.Localization;

namespace BetterDns.Gui.ViewModels;

public static class DnsText
{
    public static string Failure(string? code)
    {
        if (string.IsNullOrEmpty(code)) return string.Empty;
        if (code.StartsWith("http:", StringComparison.Ordinal)) return LocalizationManager.Get("Error.Http", code[5..]);
        var key = code switch
        {
            "timeout" => "Error.Timeout", "network" => "Error.Network", "tls" => "Error.Tls",
            "invalid-response" => "Error.InvalidResponse", "cooldown" => "Error.Cooldown",
            "disabled" => "Health.Disabled", "profile-required" => "Provider.ProfileRequired",
            "probe-busy" => "Probe.Busy", _ => "Error.Unknown"
        };
        return LocalizationManager.Get(key);
    }

    public static string Response(string? code) => LocalizationManager.Get(code switch
    {
        "NOERROR" => "Dns.NoError", "NXDOMAIN" => "Dns.NxDomain", "REFUSED" => "Dns.Refused",
        "SERVFAIL" => "Dns.ServFail", "FORMERR" => "Dns.FormErr", "NOTIMP" => "Dns.NotImp",
        "BLOCKED" => "Dns.Blocked", "NO UPSTREAM" => "Setup.RouteFailed", "FAILOVER EXHAUSTED" => "Setup.RouteFailed", "FAILOVER PENDING" => "Routing.Pending",
        _ => "Dns.Other"
    }, code ?? "—");
}
