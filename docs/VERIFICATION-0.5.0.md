# 0.5.0 strict DoH3 transport and sustained-failure policy

## Findings and correction

The previous DoH transport left connection address selection to HttpClient and did not explicitly retry the configured bootstrap addresses. The revised transport connects to provider IPs while retaining the configured provider Host/SNI identity and normal certificate validation. Up to two requests to addresses of the same provider may overlap; losers are canceled and observed, working addresses are preferred, and redirects are disabled. No HTTP/2 or protocol fallback is permitted for DoH3.

Quad9's HTTP/3 GET request succeeded where the HTTP/3 POST request timed out in local comparisons. Normal-sized DoH3 requests now use the standard GET form. DNS IDs are zero on the HTTP wire and restored before returning the answer. Both forms are defined by [RFC 8484](https://datatracker.ietf.org/doc/html/rfc8484#section-4.1); Host/SNI handling is documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-sni).

Queries over 1024 bytes still use POST to avoid unbounded request targets. POST interoperability with Quad9 on this machine remains a known limitation; normal-sized GET queries were the live-tested case. The changes do not claim universal network or VPN compatibility.

## Live transport comparison

- Before the final transport change, Control D HTTP/3 produced intermittent timeouts, while TCP-based DoH and raw QUIC/TLS connection checks succeeded. The legacy public Control D preset also retained addresses other than its published encrypted-DNS bootstrap endpoints.
- With direct address handling, 12/12 requests succeeded for personal Control D, 12/12 for public Control D and 12/12 for NextDNS. Quad9 POST requests still timed out.
- With the final GET-based normal-query path, 48/48 strict-HTTP/3 requests succeeded: 12 each for Quad9, public Control D, the user's Control D profile and the user's NextDNS profile. Tests included fresh and reused connections. Profile URLs were not printed or uploaded, and configuration bytes remained unchanged.

## Five-minute policy

Provider failover defaults to 300 seconds of repeated failures without a successful reply. The per-query timeout is separate and remains short. Before confirmation, no request is sent to a fallback provider; a failed query gets local SERVFAIL. This deliberately prioritizes staying with the selected provider over hiding brief connectivity failures.

Automatic same-provider checks fill observation gaps only after a failure. Successful replies reset the timer; a long idle/sleep gap cannot be counted as proof of a continuous outage. After confirmed failure, the existing cooldown and single-flight recovery behavior applies. Manual latency tests remain separate from routing state.

## Automated coverage

94 local tests passed before packaging. Coverage includes strict HTTP/3 request policy and response-version rejection, preserved Host/profile identity, normalized/restored transaction IDs, alternate-address retries and cancellation, retained DNS refusals, legacy-preset correction, five-minute simulated-time gating, timer reset on success, no fallback before confirmation, observation gaps, shared-group policy protection, and no automatic DNS traffic for healthy or inactive configurations. Existing WPF, control-pipe and installer tests remain in place.

The first CI activation attempt could not obtain a successful preflight answer from its default primary. Installer service/driver readiness had passed. The disposable CI fixture now probes its strict-DoH3 defaults and selects a responding provider before the kernel loopback test. This changes only the test machine's route; production defaults and five-minute confirmation are not bypassed. The measured selection is included in installer-test-logs.
