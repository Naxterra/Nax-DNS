# BetterDNS 0.5.1 verification

## Local DNS compatibility

- The service binds only 127.0.0.1:53 and [::1]:53, over UDP and TCP.
- Local-to-local traffic is excluded from BetterDNS's WinDivert interception filter, preventing the socket listener and kernel interception path from handling the same query. Remote DNS addresses supplied by a VPN remain intercepted.
- Both entry paths invoke the same DnsRouter, preserving encrypted transports, domain rules, five-minute failover confirmation, health tracking, and query logging.
- Localhost requests receive REFUSED while DNS routing is disabled. They are never sent through an upstream in that state.
- An occupied or denied port produces a typed listener state; BetterDNS does not stop or reconfigure the process using the port.
- A client may bind its source socket to a VPN interface and still use the loopback destination. The loopback-only listener binding, rather than the client's source address, is the exposure boundary.

## Automated checks

The service tests use real IPv4 and IPv6 loopback sockets and cover:

- connected UDP requests;
- TCP length framing, partial reads, pipelined requests, and connection reuse;
- classic 512-byte UDP truncation with complete TCP retry;
- advertised EDNS UDP size;
- malformed packets, upstream failure, idle-client timeout, shutdown, and socket release;
- the real DnsRouter, DoH3 provider selection, query logging, and routing-off refusal;
- all-or-nothing startup if one required bind fails.

The disposable Windows installer test additionally starts the installed service and requires its health check to report a ready kernel driver and ready local UDP/TCP listener. Its interception probe sends both UDP and TCP DNS queries to 127.0.0.1 and confirms a logged response. The test then exercises Stop, Start, Uninstall, and Install without altering server/profile/rule data.

## Scope

These checks prove the local interface expected by Windscribe. Live testing with Windscribe 2.24.12 confirmed that its c-ares client sent A and AAAA queries to 127.0.0.1 and BetterDNS returned a response for every query.

The same live test found a separate upstream constraint: with Windscribe connected, strict DoH3 timed out on all four official Control D IPv4/IPv6 bootstrap addresses, while diagnostic DoH requests to all four addresses succeeded. Other strict DoH3 providers also timed out during the test. Windscribe documents that its account-level Unlock Streaming feature prevents HTTP/3 from working while connected and that the feature is enabled by default:

- https://github.com/Windscribe/Desktop-App/wiki/Known-Issues#http3-not-working

BetterDNS does not work around that policy by downgrading to DoH. Disable Unlock Streaming in the Windscribe account when BetterDNS providers are configured as DoH3-only.
