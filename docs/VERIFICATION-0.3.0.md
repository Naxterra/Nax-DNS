# 0.3.0 workflow and dropdown verification

## Regression fixed

The dark ComboBox template's selected-value presenter did not apply DisplayMemberPath. It displayed the UpstreamEditor class name even when the popup rows had names. Provider, chain, protocol and usage-role selectors now use explicit item templates. No test service is included in the installer.

## Automated checks

- WPF off-screen rendering of the actual main and provider-editor windows in German and English. Assertions inspect the rendered TextBlocks for provider names, DoH3 and usage roles and reject internal class names.
- Changing the rendered primary selector updates the view model and saved route; existing additional backups are retained without duplicates.
- Save precedes enable. Save failure prevents activation. Activation failure leaves routing off and retains its error during refresh.
- Turning off works with an invalid unsaved draft and does not save that draft.
- Refresh preserves unsaved edits; stale-window saves do not overwrite externally changed configuration.
- Default-route edits preserve profile data, other chains and domain rules, including rules sharing the previous default chain.
- Live response status uses recorded resolver ID, route ID and protocol. Bootstrap replies and unrelated routes cannot masquerade as default-route failover.
- Changing a provider's endpoint invalidates its old health identity instead of presenting an old successful probe as a test of the new endpoint.
- Existing CI checks still exercise actual installer deployment, kernel loopback interception, published GUI startup, repair and uninstall.

The off-screen test outputs PNGs under the GUI test output's TestResults/ui folder. CI uploads them as gui-regression-renders. These tests never modify adapter settings, contact a real DNS profile or enable kernel interception on the development machine.

## Limits retained

This release improves configuration and reporting, not driver precedence. The interception limits documented in README and SECURITY still apply. A green service connection indicator is not proof of active DNS routing. Provider availability depends on the chosen protocol and network.

## Provider bootstrap references

The guided HTTPS editor uses the operator's documented premium/free Control D bootstrap addresses: https://docs.controld.com/docs/control-d-ip-ranges. NextDNS bootstrap addresses follow the existing defaults. Pasting a personal profile URL does not upload it to another service or change the active route until saved.
