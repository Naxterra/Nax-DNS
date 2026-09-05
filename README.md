# BetterDNS

BetterDNS is an experimental Windows 11 DNS policy manager with a native GUI, encrypted upstream transports, ordered failover and domain rules. Version 0.2.5 was tested intercepting an injected DNS request on a connected IVPN WireGuard tunnel and answering it through HaGeZi DoH3. Universal VPN compatibility and persistent leak prevention are not claimed. See the [interception verification](docs/VERIFICATION-0.2.5.md).

## What is implemented

0.5.3 makes the Activity query log useful for transferring domain lists. Select multiple rows with Ctrl or Shift and choose **Copy selected domains / Ausgewählte Domains kopieren**, or press Ctrl+C while the grid has focus. BetterDNS copies only unique domain names, one per line. A single domain cell also supports direct text selection and copying.

0.5.2 keeps BetterDNS running in the Windows notification area. Minimizing the window or pressing its title-bar close button hides the GUI without stopping DNS routing or losing unsaved edits. Left-click the tray icon to reopen BetterDNS, or use its localized menu to open or fully exit. Full exit still protects an unsaved draft with a confirmation.

0.5.1 adds a real DNS listener on **127.0.0.1 and ::1, UDP and TCP port 53**, so VPN clients such as Windscribe can use BetterDNS as a local DNS server. Localhost traffic uses the listener; the WinDivert kernel path continues to intercept other outbound UDP/53 queries. Both paths use the same encrypted router, rules, failover state and query log. The listener never binds a LAN or wildcard address, refuses queries while routing is off, and reports a port conflict without stopping another DNS program.

For Windscribe, set Connected DNS to 127.0.0.1. Strict DoH3 also requires **Unlock Streaming to be disabled in the Windscribe account**: Windscribe documents that this account feature prevents HTTP/3 from working through the VPN. BetterDNS deliberately does not downgrade DoH3 to DoH. See [Windscribe's known issue](https://github.com/Windscribe/Desktop-App/wiki/Known-Issues#http3-not-working) and [0.5.1 verification](docs/VERIFICATION-0.5.1.md).

0.5.1 also moves DNS status, latency results and the test button into **DNS Servers / DNS-Server**. **Activity / Aktivität** now contains only the DNS query log and selected-query attempt details. Primary/fallback selection stays on **My DNS / Mein DNS**; the duplicate server-toolbar actions and runtime badge are removed.

**Advanced → Windows service / Erweitert → Windows-Dienst** provides Start, Stop, Install and Uninstall with live Windows service status and confirmation prompts. It works without the resolver control connection. Stop and Uninstall turn routing off and restore BetterDNS network policy; app files and saved server/profile/rule configuration are preserved. Install also starts the service. Starting uses the saved routing setting; after a managed Stop/Uninstall, enable routing separately under My DNS. The installed service payload is required, and unsaved drafts must be saved before a service action.

0.5.0 keeps DoH3 strict and defaults to **five minutes of sustained connection failure before provider failover**. Individual queries still have short timeouts; before the confirmation window completes, unsuccessful queries receive local SERVFAIL rather than being sent to another provider. Successful replies reset the timer. Automatic checks run only after an observed failure and only when traffic has not supplied a recent result. See [0.5.0 verification](docs/VERIFICATION-0.5.0.md).

DoH3 now uses explicit provider bootstrap addresses with the original hostname retained for TLS validation and HTTP authority. It retries alternate addresses of the same provider with bounded overlap, remembers working addresses, and never downgrades HTTP version. Normal-sized DoH3 requests use the standard GET form; DNS IDs are normalized on the wire and restored for the caller. The exact legacy public Control D preset is corrected without overriding arbitrary custom bootstrap settings.

The DNS test introduced in 0.4.0 is now under **DNS Servers → Test DNS servers / DNS-Server → DNS-Server prüfen**. This sends a DNS query directly to every enabled, saved server and reports the result, latency and timestamp. It does not reorder providers, change configuration or reset routing cooldowns. Status refresh alone still only reads current state. The query log shows localized per-query attempt/fallback reasons. **Advanced → Fallback groups** now edits named groups and ordered server lists instead of raw IDs. See [0.4.0 verification](docs/VERIFICATION-0.4.0.md).

0.3.1 fixes server enable/disable checkboxes, dark editor frames and scrollbars, tab selection colors, protocol capitalization and generated placeholder labels. Known providers, including personal Control D profiles, now receive bootstrap addresses automatically in the service. Existing profiles and explicit custom IPs are retained. See [0.3.1 verification](docs/VERIFICATION-0.3.1.md).

Version 0.3.0 replaces the configuration-first dashboard with a primary/backup workflow:

1. Open **My DNS / Mein DNS** and choose **Primary DNS / Primärer DNS** and **Backup DNS / Ersatz-DNS** by provider name. Existing additional fallbacks remain under **More backups / Weitere Ersatzanbieter**.
2. To add your own profile, choose **Add server**, select Control D, NextDNS, or another template, and paste your complete resolver URL. Choose whether to use it as primary, backup, or only keep it in the provider library.
3. Click **Save & enable / Speichern & aktivieren**. The app saves the visible choices before checking and activating interception. **Save changes** alone does not turn routing on.
4. The live panel identifies the provider and protocol of the most recent default-route response and highlights backup use. It shows Off or Waiting instead of claiming an idle/tested connection. Domain-rule traffic can use a different route.

Provider names, protocols and usage roles have explicit display templates; the UI does not display internal class names. Unsaved edits and failed saves remain visible during refresh. Rules, manual chains and enforcement details are under **Advanced / Erweitert**. Editing the default route does not rewrite a chain still used by a domain rule. See [0.3.0 verification](docs/VERIFICATION-0.3.0.md).

- DNS-over-HTTP/3 (DoH3), DNS-over-QUIC (DoQ), DNS-over-TLS (DoT), and DNS-over-HTTPS (DoH).
- A WinDivert-based kernel interception path for outbound UDP DNS, including loopback and queries injected by another filter.
- Ordered failover with timeouts, failure thresholds, cooldown circuits, live health, and no fallback to unencrypted DNS.
- Exact, suffix, single-label wildcard, and regular-expression domain rules. Rules can route to another failover chain or block a domain.
- A colorful dark-mode WPF GUI for resolvers, failover chains, rules, enforcement, health, and recent queries.
- Complete English and German UI resources with automatic Windows-language detection and a live Deutsch/English selector.
- A LocalSystem Windows service with an administrator-only named-pipe control channel.
- No adapter DNS rewrite: current DHCP, static, VPN, Portmaster, and other resolver settings stay intact while intercepted replies are returned as if they came from the originally addressed DNS server.
- TCP/53 blocking through Windows Filtering Platform-backed Windows Firewall policy. UDP/53 queries reaching the active interceptor are diverted by the signed driver; precedence over other VPN/filter drivers is not guaranteed.
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

Provider and network HTTP/3 support can vary. Exact HTTP/3 failures are visible in DNS server status; the next provider is tried only after the configured failure-confirmation window. Use `tools/BetterDns.Probe` to verify the current network before activating a chain.

## Install

Download `BetterDNS-Setup-0.5.1-win-x64.exe` from the latest successful GitHub Actions artifact. Setup offers German and English. It checks configuration, the GUI control connection and kernel driver readiness before reporting service startup success. CI also tests loopback interception and launches the actual published GUI during the install/repair/uninstall test. Existing resolver profiles and rules are retained on upgrade.

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

WinDivert captures outbound UDP/53 DNS queries, including loopback and queries injected by other drivers. The service resolves the payload over the selected encrypted chain and injects a reply. The DNS QR flag excludes replies from recapture. While disabled, a no-match driver handle captures no traffic. Activation/deactivation waits for the kernel worker to complete its transition. Highest WinDivert priority does not guarantee precedence over another product's WFP driver. TCP/53 is blocked rather than proxied.

Current limits:

1. An application that implements its own DoH on port 443 is indistinguishable from ordinary HTTPS without TLS interception. BetterDNS does not break HTTPS to inspect it.
2. Native TCP DNS is blocked rather than proxied. Normal Windows DNS uses UDP; oversized encrypted answers are returned in the intercepted UDP response. Applications that insist on TCP/53 fail closed.
3. App-owned encrypted DNS is outside this filter. Other VPN/firewall combinations still require live testing; traffic blocked before reaching this interception layer cannot be recovered here.
4. UDP interception ends if the service crashes or stops; there is currently no persistent UDP kill switch.

See [Architecture](docs/ARCHITECTURE.md) and [Security](SECURITY.md) for the trust and failure model.

## Configuration

The service owns `%ProgramData%\BetterDNS\config.json`; the elevated GUI updates it over the local named pipe. Resolver entries need:

- a unique ID and display name;
- one of `Doh3`, `Doq`, `Dot`, or `Doh`;
- an HTTPS URL for DoH/DoH3, or a host with optional port for DoT/DoQ;
- bootstrap IP addresses for custom hostname-based providers; known providers use automatic bootstrap addresses when the field is empty;
- a timeout.

Failover requires sustained connection/transport failures or invalid replies for the configured confirmation window (default 300 seconds), not just a few slow queries. Valid DNS replies, including `NXDOMAIN`, `REFUSED` and `SERVFAIL`, are returned unchanged and reset the health timer. A provider's per-domain policy/error response does not automatically send the query elsewhere. Successful confirmation resets the timer; an observation gap greater than 45 seconds starts a new window rather than counting idle/sleep time as an outage. Shared providers use the longest confirmation window configured by their groups. Once an outage is confirmed, cooldown and single-request recovery apply. All-unavailable requests fail locally, with no plaintext or protocol fallback.

The server-list checkboxes edit whether a server is enabled. Click Save changes to apply. Disabled servers are skipped without losing their fallback order. While routing is active, the GUI prevents disabling every server in the default route; turn routing off first if that is intended. Bootstrap IPs are connection addresses for the encrypted endpoint, not additional DNS providers in the failover chain. For Control D you can leave the field empty; manual addresses are optional overrides.

## License

MIT
