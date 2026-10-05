# Nax-DNSManager

A system-wide DNS manager for Windows 11 in the spirit of YogaDNS. It intercepts
every plain DNS query on the PC — including queries applications send to a
VPN's DNS server — and answers it over encrypted DNS (DoH, DoH3, DoT, DoQ) with
ordered failover, domain rules, a cache and a live query log.

## How it works

```
application ──UDP/TCP 53──▶ any DNS server (router, VPN DNS, 127.0.0.1 …)
                 │
       WinDivert (kernel, network layer — sees the packet before it
                 │           enters a VPN tunnel)
                 ▼
        Nax-DNSService  ── built-ins ─ rules ─ cache ─ server chain
                 │                                   (DoH · DoH3 · DoT · DoQ)
                 ▼
   reply injected as if it came from the server the application asked
```

* **No adapter changes.** Windows and VPN DNS settings stay untouched; nothing
  binds port 53. When the service stops, DNS simply flows as it did before.
* **UDP/53** queries are answered in place. **TCP/53** is transparently
  proxied (or blocked/allowed, see Settings).
* **Failover.** The first enabled server answers. An error moves to the next
  server immediately; silence does so after the hedge delay (1.5 s). After
  three consecutive failures a server is skipped and re-checked in the
  background until it answers again.
* **No other DNS, ever (default).** When no encrypted server is reachable
  the query fails rather than leaving unencrypted; expired cache entries are
  still served. Settings offers a fallback to the original DNS server for
  setups where a VPN client must resolve names before any server is reachable.
* **Local names stay local.** Single-label names, `.lan`/`.local`-style
  suffixes and private reverse lookups are answered with NXDOMAIN by default
  instead of being forwarded anywhere. Change the rule's action to "original
  DNS server" if you rely on names your router hands out.
* **Bootstrap without plaintext.** Server host names are resolved over DoH
  endpoints addressed by IP (9.9.9.9, 1.1.1.1), never through the system
  resolver.

## Build and install

Requires Go 1.27+. WinDivert 2.2.2 (signed driver) is in `third_party/`.

```
build.cmd
dist\Nax-DNSService.exe install      (elevated terminal)
```

`install` copies the files to `%ProgramFiles%\Nax-DNSManager`, registers the `NaxDNS`
service (automatic start, restart on failure), adds a firewall rule for the
TCP proxy, a Start menu entry and tray autostart. Protection starts **off**;
open Nax-DNSManager, add your servers and turn it on. `uninstall` reverses this and
keeps the configuration in `%ProgramData%\NaxDNS`.

Other commands: `Nax-DNSService test [url…]` measures servers from the current
network; `Nax-DNSService run` runs in the foreground of an elevated console.

## Server URLs

| Protocol | NextDNS | Control D |
|---|---|---|
| DoH3 | `h3://dns.nextdns.io/<id>` | `h3://dns.controld.com/<id>` |
| DoH | `https://dns.nextdns.io/<id>` | `https://dns.controld.com/<id>` |
| DoT | `tls://<id>.dns.nextdns.io` | `tls://<id>.dns.controld.com` |
| DoQ | `quic://<id>.dns.nextdns.io` | `quic://<id>.dns.controld.com` |

The Add server dialog builds these from the ID. `h3://` is strict HTTP/3 and
needs UDP/443, which some VPNs block (Windscribe with "Unlock Streaming");
DoQ uses UDP/853 and is usually unaffected.

## Limits

* Applications with their own encrypted DNS (a browser's "secure DNS" set to a
  custom provider) do not use port 53 and cannot be seen without breaking TLS.
  Nax-DNSManager answers Windows' resolver discovery and Firefox's canary domain so
  the defaults stay on system DNS.
* A VPN firewall that blocks everything until the tunnel is up also blocks the
  encrypted servers; queries then use the passthrough fallback.
* The query log does not show the requesting process.

## Layout

| Path | Purpose |
|---|---|
| `cmd/naxdns-service` | Windows service, installer, CLI |
| `cmd/naxdns` | Tray icon and embedded web UI (runs as the user) |
| `internal/intercept`, `internal/divert` | WinDivert capture, reply injection, TCP reflection |
| `internal/resolver` | Rules, cache, failover, health, query log |
| `internal/upstream` | DoH/DoH3/DoT/DoQ clients (AdGuard dnsproxy), bootstrap |
| `internal/service` | Engine and control API on `\.\pipe\NaxDNS.Control` |
