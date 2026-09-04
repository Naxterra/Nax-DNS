# Runtime review — 0.2.4

The installed 0.2.3 service exited with Windows service error 1067. Application event 1026 recorded `IPAddress.ScopeId` throwing during first-run configuration serialization. The installer had logged success before this crash.

## Corrections

- Exclude computed hostname/IP objects from JSON. Persist bootstrap addresses as strings.
- Use compact JSON for the line-based named-pipe protocol, with bounded request timeouts. Pretty JSON is only for configuration files.
- Register the router through an explicit factory so dependency injection cannot choose its test constructor with an empty transport collection.
- Write DNS resource record fields into the original buffer; array slices previously discarded the type/class/TTL/length writes.
- Share parsing of DoT/DoQ endpoints with explicit ports, schemes and IPv6 addresses.
- Check upstream transaction IDs and DNS questions before accepting replies.
- Validate configuration before saving; reject direct activation through a configuration save.
- Verify the service through three application-level handshakes, version matching and driver readiness before Setup reports success.
- Test the real installer on a disposable CI Windows machine, including repair and uninstall.
- Retain the immutable, versioned WinDivert files on same-version repair. The real installer test reproduced an access-denied error when replacing an already-loaded driver, despite successful fresh installation and driver readiness.
- Treat an absent BetterDNS firewall rule group as successful cleanup. The uninstall test caught PowerShell returning an error for this normal case.

The integration tests use the same service registration, configuration store, pipe worker and client as the application. They explicitly exclude driver/firewall workers and cannot enable protection. Tests use temporary data and a unique current-user-only pipe.

## Remaining limits

Passing startup/IPC/installer tests is not proof of VPN compatibility or of leak prevention under every failure mode. Real interception and DNS transport still need an end-to-end test with the user's VPN and security products.

The current driver filter excludes loopback and packets injected by other drivers. Applications with their own DoH/DoT/DoQ are not intercepted. TCP/53 is blocked, not proxied. The WinDivert handle's highest priority orders other WinDivert handles; it does not guarantee precedence over all independent WFP drivers. When the service closes/crashes, its diversion handle closes and UDP/53 is no longer intercepted. The current TCP firewall rule does not provide a persistent UDP kill switch.

Earlier messages attributed access-denied/extraction failures to Bitdefender without a detection event proving that attribution. The observed fact was access denied to a driver file and, later, an `Expand-Archive` cleanup exception; those alone do not identify the product responsible. Similarly, a network timeout alone does not identify a filtering product as its cause.
