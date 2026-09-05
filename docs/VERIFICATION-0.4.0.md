# 0.4.0 latency, localization and routing verification

## User workflow

- Activity has a real DNS test button for saved server settings. Each enabled server receives an encrypted example.com query; the latency includes connection setup when needed. Disabled and incomplete-profile entries are identified instead of being reported as successful tests.
- Full-width status rows show result, latency, measurement source, time and localized failure category. A failed attempt no longer displays an old successful latency.
- Selecting a query shows each attempted provider, protocol, duration and fallback reason. The home screen separates stable primary/cooldown status from the last completed answer.
- A saved active route displays a non-warning saved message. Polling and actions are serialized so stale refreshes cannot undo the UI state of a newer save.
- Advanced groups use display names and ordered server lists. The failure threshold and cooldown are settings, not current error counters; explanatory text is shown next to both. Rule match/action/group selectors are localized too.

## Routing policy

Transport/protocol failures trigger fallback. Valid DNS response codes are forwarded as received, including REFUSED and SERVFAIL. This is a deliberate policy change from 0.3.x: a domain-specific refusal must not automatically bypass the selected provider's policy through a public fallback. DNS response-code meanings are defined in [RFC 1035 section 4.1.1](https://www.rfc-editor.org/rfc/rfc1035.html#section-4.1.1).

Circuit generations prevent older in-flight successes from ending cooldown early and older failures from extending a later circuit. Only one recovery attempt is admitted after cooldown; cancellation releases recovery ownership without blaming the provider. All-open circuits fail closed until recovery is eligible. Manual latency probes do not manipulate this routing state.

## Automated evidence

83 tests passed locally before packaging, including circuit timing and stale completions, single-flight recovery, caller cancellation, preserved refusal/error answers, recorded timeout fallback, probe independence, persisted measurement results over the actual control pipe, serialized refresh/save behavior, input validation and unchanged configuration during probes.

Off-screen WPF tests exercise the actual test button with a test service, render English and German Activity/group/rule screens, verify localized timeout messages and named groups, and retain the previous checkbox/theme tests. CI continues to perform fresh install, kernel loopback interception, published GUI startup, repair and uninstall. Test fixtures are never included in the installer.
