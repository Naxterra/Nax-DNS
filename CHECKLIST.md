# Must-never-happen checklist

Nax-DNSManager must never do any of the following. Every release report marks each item as verified by a named test or as not verified.

## DNS privacy

1. In strict mode, while protection is on, a DNS query on port 53 must never reach a DNS server other than the configured encrypted servers, unless a rule explicitly sends that domain to the original DNS server.
2. In strict mode, a query must never leave unencrypted because the resolver service is overloaded, stopped, crashed or restarting its capture.
3. A query that one server answers with SERVFAIL must never be sent to the other servers in the chain.
4. Parallel queries to backup servers must never happen unless the owner turned them on in Settings.
5. The TCP proxy must never answer a connection that the interceptor did not redirect, such as one from another device on the network.
6. A cached answer obtained with DNSSEC checking disabled must never be returned to a request that expects validation.

## Control and configuration

7. No Windows account other than SYSTEM, Administrators and the account that installed the app may change the configuration or turn protection off through the control pipe.
8. A damaged or unreadable configuration file must never silently turn protection off or be overwritten without a copy being kept.
9. A configuration save must never leave a half-written `config.json`.
10. The app must never report interception as running while its capture has stopped.

## System changes

11. Uninstall must never leave the `NaxDNS` or `NaxDNSGuard` service, the firewall rule, the autostart entry or the Start menu entry behind.
12. Uninstall must never leave the guard blocking DNS, so DNS works normally afterwards.
13. Turning protection off must never leave the guard blocking DNS.
14. The app must never change adapter DNS settings or bind port 53.
15. The installer must never report success unless the installed version answers, intercepts when protection is on, and the guard runs.
