package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"

	"naxdns/internal/guard"
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

// stopTray ends the tray app, which keeps its exe locked while it runs.
func stopTray() {
	for _, name := range []string{guiExeName, legacyGuiExe} {
		exec.Command("taskkill", "/F", "/IM", name).Run()
	}
}

var payload = []string{serviceExe, guiExeName, "WinDivert.dll", "WinDivert64.sys"}

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

func stopService(m *mgr.Mgr, name string) {
	s, err := m.OpenService(name)
	if err != nil {
		return
	}
	defer s.Close()
	s.Control(svc.Stop)
	for i := 0; i < 100; i++ {
		if st, err := s.Query(); err != nil || st.State == svc.Stopped {
			return
		}
		time.Sleep(200 * time.Millisecond)
	}
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
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

// ensureService creates the service or points an existing one at exe.
func ensureService(m *mgr.Mgr, name, display, description string, exe string, args ...string) (*mgr.Service, error) {
	s, err := m.OpenService(name)
	if err != nil {
		s, err = m.CreateService(name, exe, mgr.Config{
			DisplayName: display, Description: description, StartType: mgr.StartAutomatic,
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
		c.DisplayName, c.Description, c.StartType = display, description, mgr.StartAutomatic
		if err := s.UpdateConfig(c); err != nil {
			s.Close()
			return nil, fmt.Errorf("update service config %s: %w", name, err)
		}
	}
	s.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 2 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 10 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 30 * time.Second},
	}, 3600)
	return s, nil
}

func install(user string) error {
	if err := requireAdmin(); err != nil {
		return err
	}
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
	// Both services run from the files that are about to be replaced.
	stopService(m, serviceName)
	stopService(m, guard.ServiceName)

	if !strings.EqualFold(srcDir, dstDir) {
		stopTray()
		if err := os.MkdirAll(dstDir, 0o755); err != nil {
			return err
		}
		for _, name := range payload {
			err := copyFile(filepath.Join(srcDir, name), filepath.Join(dstDir, name))
			// A loaded driver image cannot be replaced; the same version is already there.
			if err != nil && !(name == "WinDivert64.sys" && fileExists(filepath.Join(dstDir, name))) {
				return fmt.Errorf("copy %s: %w", name, err)
			}
		}
	}
	svcExe := filepath.Join(dstDir, serviceExe)
	guiExe := filepath.Join(dstDir, guiExeName)

	s, err := ensureService(m, serviceName, "Nax-DNSManager",
		"Intercepts DNS queries and resolves them over encrypted DNS (DoH, DoH3, DoT, DoQ).", svcExe)
	if err != nil {
		return err
	}
	defer s.Close()
	g, err := ensureService(m, guard.ServiceName, "Nax-DNSManager Guard",
		"Blocks plain DNS in strict mode whenever the Nax-DNSManager service is not handling it.", svcExe, "guard")
	if err != nil {
		return err
	}
	defer g.Close()

	k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, service.ParametersKey, registry.SET_VALUE)
	if err != nil {
		return fmt.Errorf("record control user: %w", err)
	}
	err = k.SetStringValue(service.ControlUserValue, sid)
	k.Close()
	if err != nil {
		return fmt.Errorf("record control user: %w", err)
	}

	// Reflected TCP/53 connections arrive at the service as inbound connections.
	exec.Command("netsh", "advfirewall", "firewall", "delete", "rule", "name="+firewallRule).Run()
	exec.Command("netsh", "advfirewall", "firewall", "delete", "rule", "name="+legacyFirewallRule).Run()
	if out, err := exec.Command("netsh", "advfirewall", "firewall", "add", "rule", "name="+firewallRule,
		"dir=in", "action=allow", "protocol=TCP", "program="+svcExe, "profile=any").CombinedOutput(); err != nil {
		fmt.Printf("warning: firewall rule not added (%v): %s\n", err, out)
	}

	if k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.SetStringValue(serviceName, `"`+guiExe+`" --tray`)
		k.Close()
	}
	shortcut(guiExe, true)
	removeLegacyInstall(dstDir)

	if err := s.Start(); err != nil {
		return fmt.Errorf("start service: %w", err)
	}
	if err := g.Start(); err != nil {
		return fmt.Errorf("start guard service: %w", err)
	}
	ready, err := waitReady(20 * time.Second)
	if err != nil {
		return fmt.Errorf("installed in %s, but %w", dstDir, err)
	}
	fmt.Printf("Nax-DNSManager %s installed in %s and running: %s.\n", service.Version, dstDir, ready)
	return nil
}

// waitReady checks through the control pipe that the new service answers,
// is the version just installed, and is intercepting if protection is on. In
// strict mode the guard must also be running.
func waitReady(limit time.Duration) (string, error) {
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
		switch {
		case st.Version != service.Version:
			problem = fmt.Sprintf("version %s is running instead of %s", st.Version, service.Version)
		case st.Active && !st.Intercepting:
			problem = "protection is on but DNS is not being intercepted (" + st.Detail + ")"
		case !st.Guard.Running:
			problem = "the guard service is not running"
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

// removeLegacyInstall deletes the pre-rename program folder once the service
// runs from dstDir. A WinDivert64.sys that is still loaded stays until reboot.
func removeLegacyInstall(dstDir string) {
	old := legacyInstallDir()
	if strings.EqualFold(old, dstDir) || !fileExists(old) {
		return
	}
	if err := os.RemoveAll(old); err != nil {
		fmt.Printf("warning: %s not fully removed (%v); delete it after a reboot\n", old, err)
	}
}

func uninstall() error {
	if err := requireAdmin(); err != nil {
		return err
	}
	m, err := mgr.Connect()
	if err != nil {
		return err
	}
	defer m.Disconnect()
	// The guard goes first, so DNS works again once the resolver stops.
	for _, name := range []string{guard.ServiceName, serviceName} {
		stopService(m, name)
		if s, err := m.OpenService(name); err == nil {
			s.Delete()
			s.Close()
		}
	}
	exec.Command("netsh", "advfirewall", "firewall", "delete", "rule", "name="+firewallRule).Run()
	exec.Command("netsh", "advfirewall", "firewall", "delete", "rule", "name="+legacyFirewallRule).Run()
	if k, err := registry.OpenKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.DeleteValue(serviceName)
		k.Close()
	}
	stopTray()
	shortcut("", false)
	fmt.Println("Nax-DNSManager service removed. Configuration in ProgramData\\NaxDNS and the files in", installDir(), "were kept.")
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
	exec.Command("powershell", "-NoProfile", "-Command", script).Run()
}
