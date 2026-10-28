package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/eventlog"
	"golang.org/x/sys/windows/svc/mgr"

	"naxdns/internal/config"
	"naxdns/internal/guard"
	"naxdns/internal/intercept"
	"naxdns/internal/service"
)

const (
	serviceName  = "NaxDNS"
	firewallRule = "Nax-DNSManager TCP DNS proxy"
	runKey       = `Software\Microsoft\Windows\CurrentVersion\Run`

	// Names used before the product was renamed from NaxDNS to Nax-DNSManager.
	legacyFirewallRule = "NaxDNS TCP DNS proxy"
	legacyShortcut     = "NaxDNS.lnk"
	legacyGuiExe       = "naxdns.exe"

	serviceExe = "Nax-DNSService.exe"
	guiExeName = "Nax-DNSManager.exe"
)

var payload = []string{serviceExe, guiExeName, "WinDivert.dll", "WinDivert64.sys"}

// keepTray is set when the tray app runs the installer itself. The tray then
// stays up to report the result and hands over to the new version.
var keepTray bool

// out receives installer messages: the console and, once the data folder
// exists, install.log in it, so the tray app can point to the details of a
// failed installation.
var out io.Writer = os.Stdout

func say(format string, args ...any) { fmt.Fprintf(out, format+"\n", args...) }

func openInstallLog() func() {
	if config.SecureDir() != nil {
		return func() {}
	}
	path := filepath.Join(config.Dir(), "install.log")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		return func() {}
	}
	config.OwnByAdministrators(path)
	out = io.MultiWriter(os.Stdout, f)
	say("--- %s %s", time.Now().Format(time.RFC3339), strings.Join(os.Args[1:], " "))
	return func() { out = os.Stdout; f.Close() }
}

func system32(name string) string {
	return filepath.Join(os.Getenv("SystemRoot"), "System32", name)
}

func powershell() string {
	return system32(`WindowsPowerShell\v1.0\powershell.exe`)
}

// stopTray ends the tray app in the installer's own Windows session. Trays of
// other signed-in accounts keep running on the previous version until they
// restart.
func stopTray() {
	var session uint32
	if windows.ProcessIdToSessionId(windows.GetCurrentProcessId(), &session) != nil {
		return
	}
	for _, name := range []string{guiExeName, legacyGuiExe} {
		exec.Command(system32("taskkill.exe"), "/F", "/FI", "IMAGENAME eq "+name, "/FI", "SESSION eq "+strconv.Itoa(int(session))).Run()
	}
}

func installDir() string {
	return filepath.Join(os.Getenv("ProgramFiles"), "Nax-DNSManager")
}

func legacyInstallDir() string {
	return filepath.Join(os.Getenv("ProgramFiles"), "NaxDNS")
}

func requireAdmin() error {
	if !windows.GetCurrentProcessToken().IsElevated() {
		return errors.New("administrator rights required: run this from an elevated terminal")
	}
	return nil
}

// stopService stops a service and reports whether it is stopped (or absent).
func stopService(m *mgr.Mgr, name string) bool {
	s, err := m.OpenService(name)
	if err != nil {
		return true
	}
	defer s.Close()
	s.Control(svc.Stop)
	for i := 0; i < 150; i++ {
		if st, err := s.Query(); err != nil || st.State == svc.Stopped {
			return true
		}
		time.Sleep(200 * time.Millisecond)
	}
	return false
}

func serviceRunning(m *mgr.Mgr, name string) bool {
	s, err := m.OpenService(name)
	if err != nil {
		return false
	}
	defer s.Close()
	st, err := s.Query()
	return err == nil && st.State == svc.Running
}

// startAndWaitForGuard starts the guard and waits up to five seconds for its
// first report, which it writes after it has decided whether to block.
func startAndWaitForGuard(m *mgr.Mgr) {
	s, err := m.OpenService(guard.ServiceName)
	if err != nil {
		return
	}
	started := time.Now()
	s.Start()
	s.Close()
	for deadline := started.Add(5 * time.Second); time.Now().Before(deadline); {
		if st, running := guard.ReadStatus(); running && st.Updated.After(started) {
			return
		}
		time.Sleep(100 * time.Millisecond)
	}
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	f, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(f, in); err != nil {
		f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		f.Close()
		return err
	}
	return f.Close()
}

func sameContent(a, b string) bool {
	x, err1 := os.ReadFile(a)
	y, err2 := os.ReadFile(b)
	return err1 == nil && err2 == nil && bytes.Equal(x, y)
}

// removeLater deletes a file now, or at the next restart if it is in use. A
// file in use is first moved to a name of its own, and only that name is
// queued for deletion: a later installation may put a new file at the
// original path before the restart, and Windows would delete that one
// instead.
func removeLater(path string) {
	if err := os.Remove(path); err == nil || errors.Is(err, os.ErrNotExist) {
		return
	}
	target := path
	aside := path + ".delete-" + strconv.FormatInt(time.Now().UnixNano(), 10)
	if os.Rename(path, aside) == nil {
		target = aside
	}
	p, _ := windows.UTF16PtrFromString(target)
	windows.MoveFileEx(p, nil, windows.MOVEFILE_DELAY_UNTIL_REBOOT)
}

// removeDirLater deletes an empty folder now, or queues it for the next
// restart. Windows skips a queued folder that is not empty then, so a
// reinstalled program folder is never deleted.
func removeDirLater(dir string) {
	if err := os.Remove(dir); err == nil || errors.Is(err, os.ErrNotExist) {
		return
	}
	p, _ := windows.UTF16PtrFromString(dir)
	windows.MoveFileEx(p, nil, windows.MOVEFILE_DELAY_UNTIL_REBOOT)
}

// controlUser returns the account that may use the control pipe: the one
// given with --user, or else the account running the installer.
func controlUser(given string) (string, error) {
	if given != "" {
		if _, err := windows.StringToSid(given); err != nil {
			return "", fmt.Errorf("--user %q is not a valid SID", given)
		}
		return given, nil
	}
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return "", err
	}
	return user.User.Sid.String(), nil
}

// ensureService creates the service or points an existing one at exe. Both
// services need the base filtering engine (BFE) for WinDivert, and Windows
// restarts them after any failure, including an exit with an error code.
func ensureService(m *mgr.Mgr, name, display, description string, exe string, args ...string) (*mgr.Service, error) {
	deps := []string{"BFE"}
	s, err := m.OpenService(name)
	if err != nil {
		s, err = m.CreateService(name, exe, mgr.Config{
			DisplayName: display, Description: description, StartType: mgr.StartAutomatic, Dependencies: deps,
		}, args...)
		if err != nil {
			return nil, fmt.Errorf("create service %s: %w", name, err)
		}
	} else {
		// An existing service may still point at an older folder or name.
		c, err := s.Config()
		if err != nil {
			s.Close()
			return nil, fmt.Errorf("read service config %s: %w", name, err)
		}
		c.BinaryPathName = `"` + exe + `"`
		for _, a := range args {
			c.BinaryPathName += " " + a
		}
		c.DisplayName, c.Description, c.StartType, c.Dependencies = display, description, mgr.StartAutomatic, deps
		if err := s.UpdateConfig(c); err != nil {
			s.Close()
			return nil, fmt.Errorf("update service config %s: %w", name, err)
		}
	}
	if err := s.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 2 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 10 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 30 * time.Second},
	}, 3600); err != nil {
		s.Close()
		return nil, fmt.Errorf("set recovery for %s: %w", name, err)
	}
	if err := s.SetRecoveryActionsOnNonCrashFailures(true); err != nil {
		s.Close()
		return nil, fmt.Errorf("set recovery for %s: %w", name, err)
	}
	return s, nil
}

// swap describes the replacement of one installed file.
type swap struct {
	name      string
	installed bool // the new file is in place
	backedUp  bool // the previous file was moved to name.old
}

// install copies the files and (re)starts both services. The new files are
// first copied next to the old ones while everything keeps running. Only the
// resolver is stopped for the swap: the guard keeps blocking plain DNS in
// strict mode until the new resolver runs, and is restarted only afterwards.
// If the swap or the start of the new resolver fails, the previous files are
// restored and the previous version is started again.
func install(user string) (err error) {
	if err := requireAdmin(); err != nil {
		return err
	}
	// The event source lets the services report problems that cannot go to
	// the data folder. It may already exist from an earlier installation.
	eventCreated := eventlog.InstallAsEventCreate(eventSource, eventlog.Error|eventlog.Warning|eventlog.Info) == nil
	if err := config.SecureDir(); err != nil {
		err = fmt.Errorf("the data folder could not be secured, so nothing was installed: %w", err)
		reportEvent(err)
		if eventCreated {
			eventlog.Remove(eventSource)
		}
		return err
	}
	// A failed run takes back an event source it created, whether it fails
	// before anything else is changed or rolls back.
	defer func() {
		if err != nil && eventCreated {
			eventlog.Remove(eventSource)
		}
	}()
	defer openInstallLog()()
	defer func() {
		if err != nil {
			say("error: %v", err)
		}
	}()
	sid, err := controlUser(user)
	if err != nil {
		return err
	}
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	srcDir, dstDir := filepath.Dir(exe), installDir()
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()

	var swaps []swap
	dirCreated := !fileExists(dstDir)
	if !strings.EqualFold(srcDir, dstDir) {
		if err := os.MkdirAll(dstDir, 0o755); err != nil {
			return err
		}
		for _, name := range payload {
			dst := filepath.Join(dstDir, name)
			if name == "WinDivert64.sys" && sameContent(filepath.Join(srcDir, name), dst) {
				continue // a loaded driver cannot be replaced, and it is unchanged
			}
			if err := copyFile(filepath.Join(srcDir, name), dst+".new"); err != nil {
				for _, n := range payload {
					os.Remove(filepath.Join(dstDir, n+".new"))
				}
				return fmt.Errorf("copy %s (nothing was changed): %w", name, err)
			}
			swaps = append(swaps, swap{name: name})
		}
	}

	if !stopService(m, serviceName) {
		// It may be half stopped; let it run again and leave nothing behind.
		for _, sw := range swaps {
			os.Remove(filepath.Join(dstDir, sw.name+".new"))
		}
		if s, err := m.OpenService(serviceName); err == nil {
			s.Start()
			s.Close()
		}
		return errors.New("the running service did not stop, so nothing was changed")
	}
	if !keepTray {
		stopTray()
	}
	// Services that this run creates are removed again on failure, together
	// with their settings, so a failed first installation leaves nothing
	// behind. The settings of services that already exist are kept, so a
	// rollback can point them at their previous program again.
	created := map[string]bool{}
	previous := map[string]mgr.Config{}
	for _, name := range []string{serviceName, guard.ServiceName} {
		if s, err := m.OpenService(name); err == nil {
			if c, err := s.Config(); err == nil {
				previous[name] = c
			}
			s.Close()
		} else {
			created[name] = true
		}
	}
	restartInTurn := func() {
		// The guard must run before the resolver is cycled, so plain DNS is
		// blocked at every moment. A stopped guard (its start may have been
		// what failed) is started first from the restored files. A running
		// guard keeps blocking from the new files and is restarted only once
		// the resolver intercepts; if it never does, the guard stays as it is.
		if created[serviceName] {
			return
		}
		guardRunning := created[guard.ServiceName] || serviceRunning(m, guard.ServiceName)
		if !guardRunning {
			startAndWaitForGuard(m)
		}
		stopService(m, serviceName)
		if s, err := m.OpenService(serviceName); err == nil {
			s.Start()
			s.Close()
		}
		if created[guard.ServiceName] || !guardRunning || !waitIntercepting(15*time.Second) {
			return
		}
		stopService(m, guard.ServiceName)
		startAndWaitForGuard(m)
	}
	restore := func(cause error) error {
		if created[serviceName] {
			// A first installation had no protection before, so its services
			// can simply stop before their files go.
			stopService(m, guard.ServiceName)
			stopService(m, serviceName)
		}
		// Files are swapped back by renaming, which works while a program or
		// driver is still running from them, so in an upgrade the guard keeps
		// blocking until the previous version runs again.
		var failed []string
		for i := len(swaps) - 1; i >= 0; i-- {
			path := filepath.Join(dstDir, swaps[i].name)
			if swaps[i].installed {
				// The new file goes to a name of its own and is deleted from
				// there, never from the path that receives the old file.
				aside := path + ".rollback-" + strconv.FormatInt(time.Now().UnixNano(), 10)
				if err := os.Rename(path, aside); err != nil {
					failed = append(failed, fmt.Sprintf("move the new %s aside: %v", swaps[i].name, err))
					continue
				}
				removeLater(aside)
			}
			if swaps[i].backedUp {
				if err := os.Rename(path+".old", path); err != nil {
					failed = append(failed, fmt.Sprintf("put the previous %s back: %v", swaps[i].name, err))
				}
			}
			os.Remove(path + ".new")
		}
		for name, c := range previous {
			if s, err := m.OpenService(name); err == nil {
				if err := s.UpdateConfig(c); err != nil {
					failed = append(failed, fmt.Sprintf("point %s at its previous program: %v", name, err))
				}
				s.Close()
			}
		}
		if len(failed) > 0 && !created[serviceName] {
			// Say so plainly; the previous files stay as *.old for a manual fix.
			restartInTurn()
			return fmt.Errorf("%w; restoring the previous version failed (%s), and the previous files are kept as *.old in %s", cause, strings.Join(failed, "; "), dstDir)
		}
		// Only services that this run created are deleted.
		for _, name := range []string{guard.ServiceName, serviceName} {
			if !created[name] {
				continue
			}
			stopService(m, name)
			if s, err := m.OpenService(name); err == nil {
				s.Delete()
				s.Close()
			}
		}
		if created[serviceName] {
			// A first installation: remove everything else it may have added.
			exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "delete", "rule", "name="+firewallRule).Run()
			if k, err := registry.OpenKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
				k.DeleteValue(serviceName)
				k.Close()
			}
			shortcut("", false)
			if dirCreated {
				removeDirLater(dstDir)
			}
			if len(failed) > 0 {
				return fmt.Errorf("%w; the installation was removed, but some files remain in %s (%s)", cause, dstDir, strings.Join(failed, "; "))
			}
			return fmt.Errorf("%w; nothing was installed", cause)
		}
		restartInTurn()
		return fmt.Errorf("%w; the previous version was restored", cause)
	}
	for i := range swaps {
		path := filepath.Join(dstDir, swaps[i].name)
		if fileExists(path) {
			if fileExists(path + ".old") {
				os.Remove(path + ".old")
			}
			if err := os.Rename(path, path+".old"); err != nil {
				return restore(fmt.Errorf("move %s aside: %w", swaps[i].name, err))
			}
			swaps[i].backedUp = true
		}
		if err := os.Rename(path+".new", path); err != nil {
			return restore(fmt.Errorf("put the new %s in place: %w", swaps[i].name, err))
		}
		swaps[i].installed = true
	}

	svcExe := filepath.Join(dstDir, serviceExe)
	guiExe := filepath.Join(dstDir, guiExeName)
	s, err := ensureService(m, serviceName, "Nax-DNSManager",
		"Intercepts DNS queries and resolves them over encrypted DNS (DoH, DoH3, DoT, DoQ).", svcExe)
	if err != nil {
		return restore(err)
	}
	defer s.Close()
	g, err := ensureService(m, guard.ServiceName, "Nax-DNSManager Guard",
		"Blocks plain DNS in strict mode whenever the Nax-DNSManager service is not handling it.", svcExe, "guard")
	if err != nil {
		return restore(err)
	}
	defer g.Close()

	k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, service.ParametersKey, registry.SET_VALUE)
	if err != nil {
		return restore(fmt.Errorf("record control user: %w", err))
	}
	err = k.SetStringValue(service.ControlUserValue, sid)
	k.Close()
	if err != nil {
		return restore(fmt.Errorf("record control user: %w", err))
	}

	if err := s.Start(); err != nil {
		return restore(fmt.Errorf("start the new service: %w", err))
	}
	// Once the resolver intercepts again, restarting the guard cannot let
	// DNS leak.
	if !waitIntercepting(15 * time.Second) {
		return restore(errors.New("the new service did not start intercepting"))
	}
	stopService(m, guard.ServiceName)
	if err := g.Start(); err != nil {
		return restore(fmt.Errorf("start the guard service: %w", err))
	}

	// Redirected TCP DNS reaches the service as inbound connections on the
	// proxy port, so only that port is opened, and only for the service.
	exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "delete", "rule", "name="+firewallRule).Run()
	exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "delete", "rule", "name="+legacyFirewallRule).Run()
	if o, err := exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "add", "rule", "name="+firewallRule,
		"dir=in", "action=allow", "protocol=TCP", "localport="+strconv.Itoa(intercept.ProxyPort),
		"program="+svcExe, "profile=any").CombinedOutput(); err != nil {
		say("warning: firewall rule not added (%v): %s", err, o)
	}
	if k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.SetStringValue(serviceName, `"`+guiExe+`" --tray`)
		k.Close()
	}
	shortcut(guiExe, true)
	// A new version that starts but does not work is rolled back as well.
	// The previous files, and the folder of an installation from before the
	// rename, are only deleted once the new version is ready.
	ready, err := waitReady(m, 20*time.Second)
	if err != nil {
		return restore(err)
	}
	removeLegacyInstall(dstDir)
	for _, sw := range swaps {
		if sw.backedUp {
			removeLater(filepath.Join(dstDir, sw.name+".old"))
		}
	}
	say("Nax-DNSManager %s installed in %s and running: %s.", service.Version, dstDir, ready)
	say("Start Nax-DNSManager from the Start menu to open the updated app.")
	return nil
}

// waitReady checks through the control pipe that the new service answers,
// is the version just installed, and is intercepting if protection is on.
// The guard must be running according to the service manager and its own
// report, and blocking DNS if protection is on in strict mode.
func waitReady(m *mgr.Mgr, limit time.Duration) (string, error) {
	type state struct {
		Version      string `json:"version"`
		Active       bool   `json:"active"`
		Intercepting bool   `json:"intercepting"`
		Detail       string `json:"detail"`
		Guard        struct {
			Running bool   `json:"running"`
			Enabled bool   `json:"enabled"`
			Error   string `json:"error"`
		} `json:"guard"`
		ConfigProblem string `json:"configProblem"`
	}
	client := service.PipeClient()
	client.Timeout = 3 * time.Second
	deadline := time.Now().Add(limit)
	problem := "the service did not answer"
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		resp, err := client.Get("http://naxdns/api/state")
		if err != nil {
			continue
		}
		var st state
		err = json.NewDecoder(resp.Body).Decode(&st)
		resp.Body.Close()
		if err != nil {
			problem = "the service sent an unreadable status"
			continue
		}
		cfg, _ := config.Load(filepath.Join(config.Dir(), "config.json"))
		strict := cfg != nil && guard.Enabled(cfg)
		switch {
		case st.Version != service.Version:
			problem = fmt.Sprintf("version %s is running instead of %s", st.Version, service.Version)
		case st.Active && !st.Intercepting:
			problem = "protection is on but DNS is not being intercepted (" + st.Detail + ")"
		case !serviceRunning(m, guard.ServiceName) || !st.Guard.Running:
			problem = "the guard service is not running"
		case strict && !st.Guard.Enabled:
			problem = "the guard is not blocking DNS although strict mode is on (" + st.Guard.Error + ")"
		default:
			summary := st.Detail
			if st.Guard.Enabled {
				summary += ", guard blocking DNS outside the service"
			}
			if st.ConfigProblem != "" {
				summary += ". Warning: " + st.ConfigProblem
			}
			return summary, nil
		}
	}
	return "", errors.New(problem)
}

// waitIntercepting waits until the resolver answers and either intercepts or
// has protection off, so that stopping the guard opens no gap.
func waitIntercepting(limit time.Duration) bool {
	client := service.PipeClient()
	client.Timeout = 2 * time.Second
	deadline := time.Now().Add(limit)
	for time.Now().Before(deadline) {
		time.Sleep(300 * time.Millisecond)
		resp, err := client.Get("http://naxdns/api/state")
		if err != nil {
			continue
		}
		var st struct {
			Active       bool `json:"active"`
			Intercepting bool `json:"intercepting"`
		}
		err = json.NewDecoder(resp.Body).Decode(&st)
		resp.Body.Close()
		if err == nil && (!st.Active || st.Intercepting) {
			return true
		}
	}
	return false
}

// removeLegacyInstall deletes the pre-rename program folder once the service
// runs from dstDir. A WinDivert64.sys that is still loaded stays until reboot.
func removeLegacyInstall(dstDir string) {
	old := legacyInstallDir()
	if strings.EqualFold(old, dstDir) || !fileExists(old) {
		return
	}
	if err := os.RemoveAll(old); err != nil {
		say("warning: %s not fully removed (%v); delete it after a reboot", old, err)
	}
}

// uninstall removes both services, the firewall rule, autostart, the Start
// menu entry, the program files and this account's app data. The guard is
// removed first, so DNS works normally once the resolver stops. The
// configuration and logs in the data folder are kept. Files that are still
// in use, such as a loaded driver, are deleted at the next restart.
func uninstall() error {
	if err := requireAdmin(); err != nil {
		return err
	}
	defer openInstallLog()()
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	// If the guard keeps running, removing the resolver would leave DNS
	// blocked until the next restart, so nothing else is removed then.
	if !stopService(m, guard.ServiceName) {
		return errors.New("the guard service did not stop, so nothing was removed; try again or restart Windows first")
	}
	var problems []string
	for _, name := range []string{guard.ServiceName, serviceName} {
		if !stopService(m, name) {
			problems = append(problems, name+" did not stop")
		}
		if s, err := m.OpenService(name); err == nil {
			if err := s.Delete(); err != nil {
				problems = append(problems, fmt.Sprintf("%s could not be removed: %v", name, err))
			}
			s.Close()
		}
	}
	exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "delete", "rule", "name="+firewallRule).Run()
	exec.Command(system32("netsh.exe"), "advfirewall", "firewall", "delete", "rule", "name="+legacyFirewallRule).Run()
	if k, err := registry.OpenKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.DeleteValue(serviceName)
		k.Close()
	}
	eventlog.Remove(eventSource)
	stopTray()
	shortcut("", false)

	dir := installDir()
	if entries, err := os.ReadDir(dir); err == nil {
		for _, e := range entries {
			removeLater(filepath.Join(dir, e.Name()))
		}
	}
	removeDirLater(dir)
	if local := os.Getenv("LOCALAPPDATA"); local != "" {
		os.RemoveAll(filepath.Join(local, "NaxDNS"))
	}

	if len(problems) > 0 {
		return errors.New(strings.Join(problems, "; "))
	}
	say("Nax-DNSManager removed. The configuration and logs in %s were kept. Files still in use are deleted at the next restart.", config.Dir())
	return nil
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// shortcut creates or removes the Start menu entry.
func shortcut(target string, create bool) {
	programs := filepath.Join(os.Getenv("ProgramData"), `Microsoft\Windows\Start Menu\Programs`)
	os.Remove(filepath.Join(programs, legacyShortcut))
	link := filepath.Join(programs, "Nax-DNSManager.lnk")
	if !create {
		os.Remove(link)
		return
	}
	script := fmt.Sprintf(`$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');$s.TargetPath='%s';$s.Save()`, link, target)
	exec.Command(powershell(), "-NoProfile", "-Command", script).Run()
}
