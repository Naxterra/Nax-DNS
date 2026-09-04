# BetterDNS

BetterDNS is an experimental Windows 11 DNS policy manager with a native GUI, encrypted upstream transports, ordered failover and domain rules. VPN coexistence and full leak prevention are not yet validated. See the [runtime review](docs/REVIEW-0.2.4.md) before using it as your DNS enforcement layer.

## What is implemented

- DNS-over-HTTP/3 (DoH3), DNS-over-QUIC (DoQ), DNS-over-TLS (DoT), and DNS-over-HTTPS (DoH).
- A signed WinDivert kernel driver that captures outbound UDP DNS before it reaches a VPN tunnel or a competing local port 53 listener.
- Ordered failover with timeouts, failure thresholds, cooldown circuits, live health, and no fallback to unencrypted DNS.
- Exact, suffix, single-label wildcard, and regular-expression domain rules. Rules can route to another failover chain or block a domain.
- A colorful dark-mode WPF GUI for resolvers, failover chains, rules, enforcement, health, and recent queries.
- Complete English and German UI resources with automatic Windows-language detection and a live Deutsch/English selector.
- A LocalSystem Windows service with an administrator-only named-pipe control channel.
- No adapter DNS rewrite: current DHCP, static, VPN, Portmaster, and other resolver settings stay intact while intercepted replies are returned as if they came from the originally addressed DNS server.
- Fail-closed TCP/53 blocking through Windows Filtering Platform-backed Windows Firewall policy. UDP/53 is diverted by the signed driver and never leaves the machine while active.
- Bootstrap IP answers for configured resolver hostnames, preventing recursive interception when an encrypted transport resolves its own endpoint.
- A single modern, dark, bilingual Setup executable with upgrade/repair support, service recovery, optional Desktop shortcut, language seeding, and safe uninstall lifecycle.

## Defaults

The first-run privacy chain is ordered exactly as follows. All four entries request HTTP/3 exactly; BetterDNS does not silently downgrade a DoH3 entry to HTTP/2.

| Priority | Resolver | Endpoint | Transport |
|---:|---|---|---|
| 1 | HaGeZi Full Protection | `https://root.hagezi.org/dns-query` | DoH3 |
| 2 | Cloudflare Public DNS | `https://cloudflare-dns.com/dns-query` | DoH3 |
| 3 | Quad9 Secure | `https://dns.quad9.net/dns-query` | DoH3 |
| 4 | Google Public DNS | `https://dns.google/dns-query` | DoH3 |

A second included chain is `Control D → NextDNS`. Replace the included public endpoints with your profile URLs in the GUI:

- Control D: `https://dns.controld.com/<resolver-id>`
- NextDNS: `https://dns.nextdns.io/<profile-id>`

Provider and network HTTP/3 support can vary. Exact HTTP/3 failures are visible in Resolver health and cause the next resolver to be tried. Use `tools/BetterDns.Probe` to verify the current network before activating a chain.

## Install

Download `BetterDNS-Setup-0.2.4-win-x64.exe` from the latest successful GitHub Actions artifact. Setup offers German and English. It checks configuration, the GUI control connection and kernel driver readiness before reporting service startup success. CI exercises installation, repair and uninstall, in addition to the core and service integration tests.

To build the single-file Setup executable locally, install Inno Setup 7 and run:

```powershell
./scripts/build.ps1 -BuildInstaller
```

The installer is written to `artifacts/Installer`. The underlying self-contained application bundle remains in `artifacts/BetterDNS` for development and manual diagnostics.

The legacy script-based installation remains available for troubleshooting from an elevated PowerShell prompt in `artifacts/BetterDNS`:

```powershell
./install-service.ps1
```

Setup copies the self-contained .NET 11 service and GUI to `%ProgramFiles%\BetterDNS`, extracts the signed WinDivert 2.2.2 driver, creates the automatic `BetterDNS` service, configures service recovery, adds Start Menu and optional Desktop shortcuts, and can open the GUI. Protection starts disabled so the configuration can be reviewed first.

Use **Settings → Apps → Installed apps → BetterDNS → Uninstall** for the normal bilingual uninstaller. For the legacy package, run:

```powershell
./uninstall-service.ps1
```

The normal uninstaller retains `%ProgramData%\BetterDNS` so resolver profiles survive reinstallations. The legacy script can remove it when `-RemoveConfiguration` is supplied.

## Build and test

Requirements for a source build are Windows 11 and the .NET 11 Preview 7 SDK pinned in `global.json`. Published packages are self-contained and do not require a separately installed runtime.

```powershell
dotnet build BetterDns.slnx -c Release
dotnet test tests/BetterDns.Core.Tests/BetterDns.Core.Tests.csproj -c Release
dotnet run --project tools/BetterDns.Probe/BetterDns.Probe.csproj -c Release
```

The live probe tests the four default DoH3 resolvers plus HaGeZi over DoH, DoT, and DoQ. A failed live probe can mean either missing provider support or local UDP/443 and port 853 filtering; unit tests do not require network access.

## Kernel enforcement: precise boundary

DNS protocol parsing, TLS, QUIC, rules, and failover run in the isolated LocalSystem service. Moving those parsers into a custom kernel driver would increase crash and attack risk without improving DNS privacy.

WinDivert captures matching outbound UDP/53 packets at the network layer. The service resolves the payload over the selected encrypted chain and injects a reply. The filter excludes loopback and packets injected by other drivers. Highest WinDivert priority does not guarantee precedence over another product's WFP driver. TCP/53 is blocked rather than proxied.

Current limits:

1. An application that implements its own DoH on port 443 is indistinguishable from ordinary HTTPS without TLS interception. BetterDNS does not break HTTPS to inspect it.
2. Native TCP DNS is blocked rather than proxied. Normal Windows DNS uses UDP; oversized encrypted answers are returned in the intercepted UDP response. Applications that insist on TCP/53 fail closed.
3. Loopback queries, other drivers' injected packets and app-owned encrypted DNS can bypass this implementation. VPN/Portmaster compatibility requires live testing.
4. UDP interception ends if the service crashes or stops; there is currently no persistent UDP kill switch.

See [Architecture](docs/ARCHITECTURE.md) and [Security](SECURITY.md) for the trust and failure model.

## Configuration

The service owns `%ProgramData%\BetterDNS\config.json`; the elevated GUI updates it over the local named pipe. Resolver entries need:

- a unique ID and display name;
- one of `Doh3`, `Doq`, `Dot`, or `Doh`;
- an HTTPS URL for DoH/DoH3, or a host with optional port for DoT/DoQ;
- one or more bootstrap IP addresses so resolving the encrypted resolver itself never recurses;
- a timeout.

Failover treats valid `NXDOMAIN` answers as final. Transport failures, invalid replies, `SERVFAIL`, and `REFUSED` continue to the next resolver.

## License

MIT
