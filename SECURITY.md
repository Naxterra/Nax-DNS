# Security policy and trust model

## Reporting

Please open a private security advisory in the GitHub repository rather than publishing a working exploit in a normal issue.

## Privileged components

The Windows service runs as LocalSystem because loading the signed packet-diversion driver and managing firewall policy require administrative authority. The GUI runs as a standard user. At startup, Windows requests administrator approval for a windowless control helper; this helper alone connects to the existing administrator-only service pipe. A private per-session pipe verifies the helper and GUI process IDs in both directions, and the helper accepts only the defined BetterDNS control and service-management operations. Closing the GUI disconnects the helper. The service pipe ACL and service privileges are unchanged. Do not replace the service binary, driver, or loosen the installation directory ACL.

## Network guarantees

- Upstream DNS payloads are sent only over DoH3, DoQ, DoT, or DoH as selected. There is no automatic plaintext fallback.
- TLS certificates are validated against the configured hostname. Bootstrap IPs choose where to connect; they do not bypass name validation.
- Outbound UDP/53 queries are diverted, including loopback and injected queries; original queries are not reinjected while protection is active. TCP/53 is blocked. UDP diversion stops if the service exits; this is not a persistent UDP kill switch.
- BetterDNS does not intercept arbitrary HTTPS. An application with its own DoH implementation can bypass OS DNS, as it can with other local DNS proxies.
- Another administrator or equal/higher-priority kernel component can alter policy. BetterDNS uses WinDivert's highest priority but is not a security boundary against a hostile administrator.

## Sensitive configuration

Control D and NextDNS profile URLs can identify a resolver profile. Configuration is stored locally in `%ProgramData%\BetterDNS`; query logs are memory-only and limited to the most recent 500 entries. BetterDNS does not send telemetry.
