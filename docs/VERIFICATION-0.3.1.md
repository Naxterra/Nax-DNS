# 0.3.1 server controls, theme and bootstrap fixes

## Corrections

- Server-list enable checkboxes are interactive controls even though textual cells stay read-only. Clicking or keyboard toggling updates the draft immediately; Save applies it.
- Disabling a server retains its chain position. Disabled entries are skipped by routing. The active default route must retain at least one enabled server; all-disabled drafts can be saved while routing is off.
- Checkboxes have teal checked states and dark unchecked states. Selected tab headers use their actual accent foreground and explicit selected fill. Scrollbars use a dark template.
- Both windows explicitly apply the shared Window style. The provider dialog's entire client area and native title bar are dark, not just its inner panel.
- Provider rows display DoH3, DoH, DoT and DoQ rather than enum casing. Generated Control D and NextDNS placeholders use localized labels; arbitrary custom names and saved profile URLs are preserved.
- Empty bootstrap fields on known resolvers use the shared service-side bootstrap catalog. This fixes already-saved Control D profiles too, without requiring the editor to rewrite the configuration. Explicit custom addresses take precedence. Unknown providers still require suitable connection addresses.

Control D publishes separate premium and free bootstrap addresses in its [operator documentation](https://docs.controld.com/docs/control-d-ip-ranges). These are connection targets for the selected encrypted endpoint, not fallback DNS services. No profile ID is sent to a bootstrap lookup service.

## Regression coverage

62 local tests passed before packaging. Coverage includes actual WPF checkbox toggle patterns and persisted state, provider labels in both languages, dark dialog background and native dark-title-bar attributes, tab colors, disabled-server routing, refusal to disable every active default-route server, automatic bootstrap for Control D over HTTPS/TLS/QUIC, explicit-IP preservation, lookalike-host rejection, and a Control D bootstrap query answered without any upstream transport call.

Off-screen renders cover home, server list, advanced tabs and collapsed/expanded provider dialogs in English and German. Normal installer CI also verifies fresh install, kernel loopback interception, published GUI startup, repair and uninstall. No test preview is shipped.
