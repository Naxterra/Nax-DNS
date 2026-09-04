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

Each query selects the first matching rule, then its chain (or the default chain). Upstreams are tried in configured order. A successful DNS response, including `NXDOMAIN`, closes the circuit. Transport errors, malformed responses, `SERVFAIL`, and `REFUSED` increment the failure count. Reaching the threshold opens that resolver's circuit for the configured cooldown.

If every circuit is open, the first resolver is probed once so a chain can recover without a background plaintext health dependency. When every upstream fails, the client gets local `SERVFAIL`; there is no OS or plaintext leak fallback.

## Bootstrap

Resolving an upstream hostname itself produces another intercepted DNS packet. Packet handling is concurrent, and the router recognizes enabled upstream hostnames and synthesizes A/AAAA answers from their configured bootstrap addresses. TLS still validates the original hostname; the IP is not accepted as the authentication identity.

## Windows enforcement

The service performs two independent actions while protection is active:

1. WinDivert diverts non-loopback outbound UDP/53 at the kernel network layer. Inactive packets are reinjected unchanged. Active packets are held, resolved over the selected encrypted transport, and replaced with an inbound response that preserves the DNS transaction ID and reverses the original IP/UDP endpoints.
2. A grouped outbound TCP/53 block rule covers every non-loopback destination. Windows Firewall compiles it to Windows Filtering Platform policy, enforced in the kernel network/transport layers.

The service uses DoH3/DoH on port 443 and DoQ/DoT on port 853, so port 53 enforcement never blocks an upstream. BetterDNS intentionally does not globally block port 853 because that would also block its own DoQ/DoT clients without a separate application-identity callout policy.

This model does not bind local port 53 and does not modify adapter DNS. Portmaster, Internet Connection Sharing, and other local services may continue owning port 53; VPN DNS address changes do not change which UDP queries the kernel handle diverts.

## IPC and privilege

The `BetterDNS.Control` named pipe grants access only to LocalSystem and the built-in Administrators group. The GUI requests elevation in its application manifest. Configuration validation rejects duplicate IDs, missing default chains, and unknown resolver references before committing an atomic replacement file.

## Shutdown and recovery

The installed service has automatic restart recovery. The uninstaller stops the service, runs `BetterDns.Service.exe --restore`, removes WFP-backed firewall rules and any legacy adapter backup from earlier builds, and then deletes the service. Configuration remains in ProgramData by default.
