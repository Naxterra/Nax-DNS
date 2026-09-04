# 0.2.5 interception and packaged GUI verification

## Observed issue and correction

After the host permitted WinDivert to load, 0.2.4 passed its service/driver health check. It nevertheless missed a query addressed to the connected IVPN WireGuard DNS resolver: an independent, read-only WinDivert observer recorded the outgoing packet with IsImpostor=true. The service's !impostor filter excluded it.

0.2.5 includes injected queries and loopback. It matches DNS requests (QR=0), so replies cannot be recaptured. Disabled mode uses a no-match filter rather than reinjecting requests, avoiding query reinjection cycles with other filters. Control requests wait for the actual worker mode transition.

## Local live results

- Service and driver health checks passed.
- A fresh randomized hostname sent to the connected IVPN DNS endpoint was answered through HaGeZi DoH3. The client received a valid answer, and the service query log independently matched that hostname to HaGeZi (approximately 33 ms).
- A loopback query to 127.0.0.1:53 was answered by BetterDNS's local bootstrap mapping. Packet observation confirmed an outbound loopback reply.
- Each probe restored the original disabled protection setting.
- The installed WPF GUI initially crashed in the .NET 11 single-file package with a DirectWriteForwarder.dll load error. A folder-based self-contained payload launched successfully and remained open. Setup itself remains a single EXE.

The diagnostic probe records only its requested hostname, its packet metadata and the matching service log entries. It does not save unrelated DNS traffic or profile URLs.

## Automated coverage

The CI installer test now activates protection briefly, verifies a loopback lookup through the real service and driver, restores protection, launches the published GUI, verifies it remains open, and checks repair/uninstall. External DNS availability is still required by the normal activation preflight.

## Limits

The live IVPN result validates that observed DNS path, not every VPN/filter version. Direct probes addressed to other DNS destinations did not all reach the interceptor. App-owned DoH/DoT/DoQ traffic and a persistent UDP kill switch remain outside the current implementation.
