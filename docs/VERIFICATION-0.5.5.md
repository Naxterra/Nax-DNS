# BetterDNS 0.5.5 verification

## Single instances

- A per-user named pipe is created before the GUI requests its elevated control broker.
- A second GUI cannot own that pipe. It sends an activate command and exits; the original window is restored from the notification area.
- The update-exit command asks the existing window to close normally. Busy work and unsaved-draft confirmation remain authoritative.
- The service opens an exclusive lock file in its protected ProgramData directory before constructing the host. This supplements Windows Service Control Manager enforcement and rejects a manually launched second resolver process.
- Health-check and post-stop restore commands remain separate maintenance modes and do not attempt to become a resolver service instance.

## Privilege boundary

- The visible GUI manifest is asInvoker.
- A windowless elevated helper connects to the existing administrator-only service pipe.
- The private broker pipe verifies the GUI and broker process IDs in both directions.
- Only the defined BetterDNS control and service-management operations are accepted.
- Closing the GUI closes the private pipe and ends its helper.

## Installer and tray

- The tray menu uses the dark BetterDNS palette.
- A version marker prevents older GUI versions from receiving an unsupported update-exit argument.
- Supported versions exit through the single-instance command channel before install or uninstall proceeds.
- The installer stops if the GUI remains busy or the user keeps an unsaved draft open.

## Automated checks

The test suite covers activation and update-exit handoff, refusal of a second pipe owner, ownership recovery after exit, the service process lock, broker identity and command restrictions, and the existing DNS, GUI, localization, and lifecycle behavior. The disposable Windows installer test launches the installed GUI, verifies that a second launch exits while the original remains alive, then verifies graceful update-exit before repair.
