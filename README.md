# Nax-DNSManager

Nax-DNSManager is a system-wide DNS manager for Windows 11 in the spirit of YogaDNS. It intercepts the plain DNS queries that programs on this PC send on port 53, including queries addressed to a VPN's DNS server or to a local resolver on `127.0.0.1`, and answers them over encrypted DNS (DoH, DoH3, DoT or DoQ). It offers ordered failover, domain rules, a cache and a live query log.

## How it works

```
application ──UDP/TCP 53──▶ any DNS server (router, VPN DNS, 127.0.0.1 …)
                 │
       WinDivert (kernel network layer, before a VPN tunnel)
                 │
                 ▼
        Nax-DNSService  ── built-ins ─ rules ─ cache ─ server chain
                 │                                   (DoH · DoH3 · DoT · DoQ)
                 ▼
   reply injected as if it came from the server the application asked
```

- **No adapter changes.** Windows and VPN DNS settings stay untouched, and nothing binds port 53.
- **UDP and TCP.** UDP queries are answered in place. TCP connections on port 53, including those to `127.0.0.1` and `::1`, are redirected to a built-in DNS-over-TCP proxy on port 48653, or blocked or allowed, as chosen in Settings. The proxy accepts only connections whose first packet the interceptor redirected, so other devices on the network cannot use it.
- **Failover.** The first enabled server answers every query. Only a connection error or a timeout (3 seconds) moves that query to the next server. Any DNS answer, SERVFAIL included, is final, so each query normally goes to one provider. After three consecutive failures a server is skipped and checked in the background until it answers again. Settings can also ask the next server in parallel when the first one is slow, which is off by default. If every server fails, a cached answer that expired less than six hours ago is served before anything else happens, in strict mode and outside it. The cache is cleared whenever settings change or the service restarts.
- **Strict mode (default).** When no encrypted server is reachable and no expired cached answer is left, the query fails instead of going to the original DNS server. Strict mode cannot be combined with letting TCP DNS pass unencrypted. Settings offers a fallback to the original DNS server for setups where a VPN client must resolve names before any encrypted server is reachable.
- **Kill switch.** In strict mode a second Windows service, `NaxDNSGuard`, holds a low-priority WinDivert handle that drops every outbound DNS packet on port 53 that the resolver service has not handled. The resolver's handle has the highest priority, so normally the guard sees nothing. If the resolver crashes, is stopped or is restarting its capture, DNS fails instead of reaching another server. The guard follows the saved configuration and turns itself off when protection is off or strict mode is not used. It also remembers its last state in the registry, where only SYSTEM and Administrators can write, and keeps that state if the data folder cannot be trusted.
- **Local names stay local.** Single-label names, `.lan` and `.local` style names and private reverse lookups are answered with NXDOMAIN by default instead of being forwarded. Change that rule's action to "original DNS server" if you rely on names your router hands out. Outside strict mode such a UDP query then continues to its original server unchanged. In strict mode, and for TCP, the service sends it again itself from a reserved range of local ports (48600 to 48999), which the interceptor and the guard let through.
- **Bootstrap.** The service needs the IP addresses of your DNS servers' host names. It asks DoH endpoints addressed by IP (Quad9 first, then Cloudflare) instead of the system resolver, one after another, and caches the results. These bootstrap lookups go to providers outside your server list and only reveal which DNS servers you use. Enter bootstrap IP addresses for a server to avoid them for that server.
- **Plain servers.** A server added with the protocol "Plain DNS" (`udp://`) is unencrypted. It is meant for a resolver on your own network, such as a router that forwards DNS over an encrypted protocol itself. Its queries are sent from the reserved ports, so strict mode and the guard let them through. An answer too large for UDP is fetched again over TCP from the same port range.
- **Recovery.** Each save is flushed to disk before it replaces `config.json`, and the version it replaces is kept as `config.json.last-good`. If `config.json` is damaged or missing at startup, a copy of the damaged file is kept under a name ending in `.broken-` and the date and time, the last working copy is restored, and the app shows what happened. A damaged working copy is kept the same way, so no damaged file is ever overwritten. A save from the app over a file that became damaged while the service ran keeps a copy of it the same way, and a save over a file that cannot be read is refused. A file that is only locked by another program, such as a virus scanner, is read again for a few seconds and never replaced by the older copy. A file written by an older version is adapted to the current rules when it is loaded: numbers outside their allowed range are set to the nearest limit, the minimum cache time is kept below the maximum, strict mode with TCP DNS allowed becomes strict mode with TCP DNS proxied, and parallel backup queries, which versions before 0.3.0 saved as on, are switched off. Changes made in the app are checked against the same rules and rejected if they break them.
- **Data folder.** `%ProgramData%\NaxDNS` belongs to SYSTEM and only SYSTEM and Administrators can access it. At every start, the services repair its permissions, delete links and files planted by other accounts, and replace a folder that another account created. If the folder cannot be secured, the resolver refuses to run rather than trust it, the guard keeps its remembered state, and Windows restarts the resolver to try again. The reason is written to the Windows event log under the source "Nax-DNSManager", because the data folder cannot be used for it.

## Build and install

The build needs the machine-wide Go installation. WinDivert 2.2.2 (signed driver) is in `third_party/`.

```
build.cmd
dist\Nax-DNSService.exe install
```

Run `install` from an elevated terminal. It copies the new files next to the installed ones while everything keeps running. It then stops only the resolver service, swaps the files and starts the new version. The guard keeps blocking plain DNS in strict mode during the swap and is only restarted once the new version intercepts. If the swap, the start or the final readiness check fails, the previous files and service settings are restored and the previous version is started again. In an upgrade the files are swapped back by renaming them while the services run, so the guard keeps blocking during the rollback. The previous files are only deleted once the new version is ready. A failed first installation removes everything it added. The installer first registers the event log source and then secures the data folder, before it changes anything else. A failed run removes an event log source that it registered. Both services start automatically, depend on the Windows base filtering engine, and restart after any failure. The installer also adds a firewall rule for the TCP proxy port, a Start menu entry and tray autostart, writes its messages to `install.log` in the data folder, and finally checks that the new version answers, that it intercepts when protection is on, and that the guard runs. Protection starts off on a new installation, so open Nax-DNSManager, add your servers and turn it on.

Only SYSTEM, Administrators and the account that installed the app can use the service's control pipe. That account may read and send requests but not create pipe instances, and the app only talks to a pipe served by the installed service in the Windows services session. The tray app's "Install and start service" button passes the signed-in account and reports the installer's result, and `install --user <SID>` names another account. `uninstall` removes both services, the firewall rule, autostart, the Start menu entry, the event log source, the program files and the uninstalling account's app data. If the guard does not stop, uninstall removes nothing, so DNS is never left blocked. Files still in use are renamed and deleted at the next restart, so a reinstallation before that restart is not affected. The configuration and logs in `%ProgramData%\NaxDNS` are kept, and other accounts' app data in their `%LOCALAPPDATA%\NaxDNS` stays.

`Nax-DNSService test [url…]` measures servers from the current network. A server passes only if it actually resolves the test names. Without URLs it measures the configured servers, which requires an elevated terminal because only administrators may read the configuration. `Nax-DNSService run` and `Nax-DNSService guard` run the two services in the foreground of an elevated console. The app does not connect to such a console run, because it only accepts the control pipe of the installed service.

## Server URLs

| Protocol | NextDNS | Control D |
|---|---|---|
| DoH3 | `h3://dns.nextdns.io/<id>` | `h3://dns.controld.com/<id>` |
| DoH | `https://dns.nextdns.io/<id>` | `https://dns.controld.com/<id>` |
| DoT | `tls://<id>.dns.nextdns.io` | `tls://<id>.dns.controld.com` |
| DoQ | `quic://<id>.dns.nextdns.io` | `quic://<id>.dns.controld.com` |

The Add server dialog builds these from the ID. DoH3 is strict HTTP/3 and needs UDP port 443, which some VPN setups and security products block. DoQ uses UDP port 853 and DoT uses TCP port 853.

## Limits

- Applications with their own encrypted DNS, such as a browser whose secure DNS is set to a custom provider, do not use port 53. Nax-DNSManager cannot see those queries without breaking TLS. It answers Windows' resolver discovery and Firefox's canary domain so that their defaults stay on system DNS.
- Only this PC's queries are visible. Devices behind the router, such as IoT devices, need their own logging, for example in the router's DNS proxy or the DNS provider's dashboard.
- The query log only lists traffic that was captured, so an empty log is not proof that no DNS left the PC by another route.
- A VPN firewall that blocks everything until the tunnel is up also blocks the encrypted servers. In strict mode, lookups fail until the tunnel is up.
- The guard exempts the reserved port range 48600 to 48999, so a program that deliberately sends DNS from those ports is not blocked. Any local account can also occupy those ports, which makes rules that use the original DNS server and plain servers fail.
- The control pipe refuses connections over the network, but any program running as the account that installed the app can change the configuration, just as that account can in the app.
- The query log does not show the requesting process.
- Only DNS sent by this PC's own network stack is intercepted. Virtual machines and containers with their own network stack, and devices that share this PC's connection through Windows' NAT or a mobile hotspot, are not covered unless their DNS goes through Windows on this PC.
- If another account creates `%ProgramData%\NaxDNS` before the first installation and keeps a file in it open, the installer cannot replace the folder and stops without installing anything.

## Layout

| Path | Purpose |
|---|---|
| `cmd/naxdns-service` | Windows services, installer and command line |
| `cmd/naxdns` | Tray icon and app window (runs as the signed-in user) |
| `internal/intercept`, `internal/divert` | WinDivert capture, reply injection and TCP redirection |
| `internal/guard` | Strict-mode kill switch |
| `internal/resolver` | Rules, cache, failover, health and query log |
| `internal/upstream` | DoH, DoH3, DoT and DoQ clients (AdGuard dnsproxy) and bootstrap |
| `internal/service` | Engine and control API on `\\.\pipe\NaxDNS.Control` |

## License

This software is under the Nax Source-Available License 1.0. You may install and use the official release builds, privately and at work, and you may read the source code. Modifying the software, redistributing it and selling it need the written permission of the owner. See [LICENSE](LICENSE). The licenses of the bundled third-party components are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

