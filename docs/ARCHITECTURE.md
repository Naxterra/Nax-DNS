# Architecture

## Data path

```text
Windows application sends UDP/53 to any configured DNS IP
      │
      ▼
WinDivert signed kernel driver (highest priority, original packet held)
      │ DNS payload
      ▼
BetterDNS service ── domain rule ── failover chain
      │                                  │
      ├─ bootstrap hostname answer       ├─ DoH3 (HTTP/3 over MsQuic)
      │                                  ├─ DoQ  (RFC 9250 over MsQuic)
      │                                  ├─ DoT  (TLS 1.2/1.3)
      │                                  └─ DoH  (RFC 8484)
      │ encrypted DNS response
      ▼
Checksum-valid spoofed reply injected into the inbound Windows stack
```

`BetterDns.Core` owns DNS wire and IP/UDP handling, matching, transport clients, health circuits, and routing. `BetterDns.Service` owns kernel packet diversion, persistent configuration, enforcement, and IPC. `BetterDns.Gui` is an administrator control plane; it never handles live DNS packets.

## Failover

Each query selects the first matching rule, then its chain (or the default chain). Upstreams are tried in configured order. Valid DNS replies, including `NXDOMAIN`, `REFUSED` and `SERVFAIL`, are passed through without provider fallback. Connection failures and invalid/mismatched messages count toward the cooldown threshold. Attempts carry circuit-generation leases: completions from older generations cannot clear or extend an opened circuit. After expiry, exactly one request can try recovery while other requests continue to fallback. A canceled local request does not mark the provider down.

Since 0.5.0, an unsuccessful query does not proceed to the next provider until the provider has failed continuously for FailoverAfterSeconds (300 by default). DNS queries keep their short per-request deadlines and return local SERVFAIL while failure confirmation is pending. The health worker checks pending failures when no fresh traffic result has arrived for 10 seconds; healthy providers are not probed in the background. Successful provider replies reset the window, and gaps over 45 seconds do not establish continuity. The longest group confirmation delay applies to shared provider health. Existing configurations lacking the field acquire the new default without losing their profiles.

If every circuit is open, requests receive local `SERVFAIL` until a recovery attempt becomes eligible. The router does not bypass cooldowns by hammering the first provider. Each query retains typed attempt outcomes (server, protocol, duration, failure category or DNS result), including cooldown skips, for GUI diagnosis. There is no OS or plaintext leak fallback.

Manual DNS probes use the same encrypted transport directly, not the routing/circuit path. They query example.com with bounded concurrency and store results separately, keyed by exact resolver settings. A successful manual probe does not reset a circuit, change configuration, activate interception or appear as normal DNS traffic. Error codes are stable and localized in the GUI; measurement timestamps distinguish traffic results from manual tests.

## Bootstrap

Resolving an upstream hostname itself produces another intercepted DNS packet. Packet handling is concurrent, and the router recognizes enabled upstream hostnames and synthesizes A/AAAA answers from their configured bootstrap addresses. TLS still validates the original hostname; the IP is not accepted as the authentication identity.

## Windows enforcement

The service performs two independent actions while protection is active:

1. WinDivert diverts outbound UDP/53 queries (QR=0), including loopback and injected queries. Active queries are resolved over the selected encrypted transport and replaced with a reply preserving the transaction ID and reversing the endpoints. Network replies are injected inbound; loopback replies are injected outbound, as required by Windows loopback classification. QR=1 replies are excluded from the filter. Inactive mode uses a no-match handle and never captures or reinjects packets. Mode changes cancel pending reads, await queries and dispose their handle before opening the new mode.
2. A grouped outbound TCP/53 block rule covers every non-loopback destination. Windows Firewall compiles it to Windows Filtering Platform policy, enforced in the kernel network/transport layers.

The service uses DoH3/DoH on port 443 and DoQ/DoT on port 853, so port 53 enforcement never blocks an upstream. BetterDNS intentionally does not globally block port 853 because that would also block its own DoQ/DoT clients without a separate application-identity callout policy.

This model does not bind local port 53 and does not modify adapter DNS. Portmaster, Internet Connection Sharing, and other local services may continue owning port 53; VPN DNS address changes do not change which UDP queries the kernel handle diverts.

## IPC and privilege

The `BetterDNS.Control` named pipe grants access only to LocalSystem and the built-in Administrators group. The GUI requests elevation in its application manifest. Configuration validation rejects duplicate IDs, missing default chains, and unknown resolver references before committing an atomic replacement file.

## Shutdown and recovery

The installed service has automatic restart recovery. The uninstaller stops the service, runs `BetterDns.Service.exe --restore`, removes WFP-backed firewall rules and any legacy adapter backup from earlier builds, and then deletes the service. Configuration remains in ProgramData by default.

The WinDivert native package is extracted directly into a versioned final directory and loaded through a managed native-library resolver. No post-extraction copy of the `.sys` file is performed, because endpoint protection may deliberately deny user-mode reads of driver images after they are written. LocalSystem still loads the signed driver from that final directory.
