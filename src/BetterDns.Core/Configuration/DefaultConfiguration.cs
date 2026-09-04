namespace BetterDns.Core.Configuration;

public static class DefaultConfiguration
{
    public static BetterDnsConfiguration Create() => new()
    {
        Active = false,
        DefaultChainId = "privacy-default",
        Upstreams =
        [
            new()
            {
                Id = "hagezi-root",
                Name = "HaGeZi Full Protection (Germany)",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://root.hagezi.org/dns-query",
                BootstrapAddresses = ["188.34.161.210", "2a01:4f8:c17:1c66::1"]
            },
            new()
            {
                Id = "cloudflare-security",
                Name = "Cloudflare Public DNS",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://cloudflare-dns.com/dns-query",
                BootstrapAddresses = ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"]
            },
            new()
            {
                Id = "quad9-secure",
                Name = "Quad9 Secure",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://dns.quad9.net/dns-query",
                BootstrapAddresses = ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"]
            },
            new()
            {
                Id = "google-public",
                Name = "Google Public DNS",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://dns.google/dns-query",
                BootstrapAddresses = ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"]
            },
            new()
            {
                Id = "controld",
                Name = "Control D (replace with your resolver URL)",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://freedns.controld.com/p2",
                BootstrapAddresses = ["76.76.2.2", "76.76.10.2", "2606:1a40::2", "2606:1a40:1::2"]
            },
            new()
            {
                Id = "nextdns",
                Name = "NextDNS (replace with your profile URL)",
                Protocol = DnsProtocol.Doh3,
                Endpoint = "https://dns.nextdns.io",
                BootstrapAddresses = ["45.90.28.0", "45.90.30.0", "2a07:a8c0::", "2a07:a8c1::"]
            }
        ],
        Chains =
        [
            new()
            {
                Id = "privacy-default",
                Name = "Privacy defaults",
                UpstreamIds = ["hagezi-root", "cloudflare-security", "quad9-secure", "google-public"]
            },
            new()
            {
                Id = "controld-nextdns",
                Name = "Control D → NextDNS",
                UpstreamIds = ["controld", "nextdns"]
            }
        ],
        Rules = []
    };
}
