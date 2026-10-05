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
	firewallRule = "NaxDNS TCP DNS proxy"
	runKey       = `Software\Microsoft\Windows\CurrentVersion\Run`
)

var payload = []string{"naxdns-service.exe", "naxdns.exe", "WinDivert.dll", "WinDivert64.sys"}

func installDir() string {
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
		// The tray app keeps naxdns.exe locked while it runs.
		exec.Command("taskkill", "/F", "/IM", "naxdns.exe").Run()
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
	svcExe := filepath.Join(dstDir, "naxdns-service.exe")
	guiExe := filepath.Join(dstDir, "naxdns.exe")

	s, err := m.OpenService(serviceName)
	if err != nil {
		s, err = m.CreateService(serviceName, svcExe, mgr.Config{
			DisplayName: "NaxDNS",
			Description: "Intercepts DNS queries and resolves them over encrypted DNS (DoH, DoH3, DoT, DoQ).",
			StartType:   mgr.StartAutomatic,
		})
		if err != nil {
			return fmt.Errorf("create service: %w", err)
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
	if out, err := exec.Command("netsh", "advfirewall", "firewall", "add", "rule", "name="+firewallRule,
		"dir=in", "action=allow", "protocol=TCP", "program="+svcExe, "profile=any").CombinedOutput(); err != nil {
		fmt.Printf("warning: firewall rule not added (%v): %s\n", err, out)
	}

	if k, _, err := registry.CreateKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.SetStringValue(serviceName, `"`+guiExe+`" --tray`)
		k.Close()
	}
	shortcut(guiExe, true)

	if err := s.Start(); err != nil {
		return fmt.Errorf("start service: %w", err)
	}
	fmt.Println("NaxDNS installed in", dstDir, "and started.")
	return nil
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
	if k, err := registry.OpenKey(registry.LOCAL_MACHINE, runKey, registry.SET_VALUE); err == nil {
		k.DeleteValue(serviceName)
		k.Close()
	}
	exec.Command("taskkill", "/F", "/IM", "naxdns.exe").Run()
	shortcut("", false)
	fmt.Println("NaxDNS service removed. Configuration in ProgramData\\NaxDNS and the files in", installDir(), "were kept.")
	return nil
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

// shortcut creates or removes the Start menu entry.
func shortcut(target string, create bool) {
	link := filepath.Join(os.Getenv("ProgramData"), `Microsoft\Windows\Start Menu\Programs\NaxDNS.lnk`)
	if !create {
		os.Remove(link)
		return
	}
	script := fmt.Sprintf(`$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%s');$s.TargetPath='%s';$s.Save()`, link, target)
	exec.Command("powershell", "-NoProfile", "-Command", script).Run()
}
