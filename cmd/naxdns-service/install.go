package main

import (
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

func stopService(m *mgr.Mgr) {
	s, err := m.OpenService(serviceName)
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

func install() error {
	if err := requireAdmin(); err != nil {
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
	stopService(m)

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

	s, err := m.OpenService(serviceName)
	if err != nil {
		s, err = m.CreateService(serviceName, svcExe, mgr.Config{
			DisplayName: "Nax-DNSManager",
			Description: "Intercepts DNS queries and resolves them over encrypted DNS (DoH, DoH3, DoT, DoQ).",
			StartType:   mgr.StartAutomatic,
		})
		if err != nil {
			return fmt.Errorf("create service: %w", err)
		}
	} else {
		// An existing service may still point at the pre-rename folder.
		c, err := s.Config()
		if err != nil {
			s.Close()
			return fmt.Errorf("read service config: %w", err)
		}
		c.BinaryPathName = `"` + svcExe + `"`
		c.DisplayName = "Nax-DNSManager"
		if err := s.UpdateConfig(c); err != nil {
			s.Close()
			return fmt.Errorf("update service config: %w", err)
		}
	}
	defer s.Close()
	s.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 2 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 10 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 30 * time.Second},
	}, 3600)

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
	fmt.Println("Nax-DNSManager installed in", dstDir, "and started.")
	return nil
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
	stopService(m)
	if s, err := m.OpenService(serviceName); err == nil {
		s.Delete()
		s.Close()
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
