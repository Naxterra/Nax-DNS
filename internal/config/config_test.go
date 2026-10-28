package config

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"golang.org/x/sys/windows"
)

func TestRecoverUsesLastWorkingCopy(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	good := Default()
	good.Active = true
	good.Upstreams = good.Upstreams[:1]
	if err := good.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	cfg, problem := Recover(path)
	if problem == "" || !strings.Contains(problem, "restored") {
		t.Fatalf("problem = %q, want a report of the restored copy", problem)
	}
	if !cfg.Active || len(cfg.Upstreams) != 1 {
		t.Errorf("the last working configuration was not restored: %+v", cfg)
	}
	if matches, _ := filepath.Glob(path + ".broken-*"); len(matches) != 1 {
		t.Errorf("the damaged file was not kept: %v", matches)
	}
}

func TestRecoverWithoutCopyReportsProtectionOff(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	cfg, problem := Recover(path)
	if cfg.Active || !strings.Contains(problem, "protection is off") {
		t.Errorf("active=%v problem=%q", cfg.Active, problem)
	}
}

func TestRecoverMissingFileIsNotAProblem(t *testing.T) {
	if _, problem := Recover(filepath.Join(t.TempDir(), "config.json")); problem != "" {
		t.Errorf("first start reported %q", problem)
	}
}

func TestSaveKeepsThePreviousVersionAsWorkingCopy(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	first, second := Default(), Default()
	second.Active = true
	if err := first.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := second.Save(path); err != nil {
		t.Fatal(err)
	}
	now, _ := Load(path)
	kept, _ := Load(backupPath(path))
	if !now.Active || kept.Active {
		t.Errorf("config.json active=%v (want true), working copy active=%v (want false)", now.Active, kept.Active)
	}
}

func TestSaveSucceedsWhileAnotherProgramHasTheFileOpen(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	if err := Default().Save(path); err != nil {
		t.Fatal(err)
	}
	// os.Open does not allow the file to be replaced while it is open, as
	// with a virus scanner. The save must wait for it instead of failing.
	f, err := os.Open(path)
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		time.Sleep(300 * time.Millisecond)
		f.Close()
	}()
	next := Default()
	next.Active = true
	if err := next.Save(path); err != nil {
		t.Fatalf("save failed while the file was briefly open: %v", err)
	}
	if got, _ := Load(path); !got.Active {
		t.Error("the new version was not written")
	}
}

func TestSaveSucceedsWhileTheGuardReadsTheFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	if err := Default().Save(path); err != nil {
		t.Fatal(err)
	}
	stop := make(chan struct{})
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			select {
			case <-stop:
				return
			default:
				Load(path)
			}
		}
	}()
	for i := 0; i < 200; i++ {
		c := Default()
		c.Active = i%2 == 0
		if err := c.Save(path); err != nil {
			close(stop)
			<-done
			t.Fatalf("save %d failed while the file was being read: %v", i, err)
		}
	}
	close(stop)
	<-done
}

func TestDamagedWorkingCopyIsKeptAndNeverOverwritten(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	os.WriteFile(path, []byte("{damaged"), 0o644)
	os.WriteFile(backupPath(path), []byte("{also damaged"), 0o644)
	cfg, problem := Recover(path)
	if cfg.Active || !strings.Contains(problem, "damaged too") {
		t.Fatalf("active=%v problem=%q", cfg.Active, problem)
	}
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	kept, _ := filepath.Glob(backupPath(path) + ".broken-*")
	if len(kept) != 1 {
		t.Fatalf("the damaged working copy was not kept: %v", kept)
	}
	if data, err := os.ReadFile(kept[0]); err != nil || string(data) != "{also damaged" {
		t.Errorf("the damaged working copy was not kept: %q, %v", data, err)
	}
}

func TestSaveDoesNotReuseAPlantedTemporaryFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, []byte("planted"), 0o644); err != nil {
		t.Fatal(err)
	}
	// Give the planted file an explicit entry for Everyone, which must not end
	// up on config.json.
	everyone, _ := windows.CreateWellKnownSid(windows.WinWorldSid)
	acl, err := windows.ACLFromEntries([]windows.EXPLICIT_ACCESS{{
		AccessPermissions: windows.GENERIC_ALL, AccessMode: windows.GRANT_ACCESS,
		Trustee: windows.TRUSTEE{TrusteeForm: windows.TRUSTEE_IS_SID, TrusteeType: windows.TRUSTEE_IS_WELL_KNOWN_GROUP, TrusteeValue: windows.TrusteeValueFromSID(everyone)},
	}}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := windows.SetNamedSecurityInfo(tmp, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION, nil, nil, acl, nil); err != nil {
		t.Fatal(err)
	}
	if err := Default().Save(path); err != nil {
		t.Fatal(err)
	}
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(sd.String(), ";;;WD)") {
		t.Errorf("config.json carries the planted file's entry for Everyone: %s", sd.String())
	}
}

func TestStrictModeRejectsUnencryptedTCP(t *testing.T) {
	c := Default()
	c.Settings.OnFailure, c.Settings.TCPMode = FailServfail, TCPAllow
	if c.Validate() == nil {
		t.Error("strict mode accepted letting TCP DNS pass unencrypted")
	}
	c.Settings.OnFailure = FailPassthrough
	if err := c.Validate(); err != nil {
		t.Errorf("TCP allow outside strict mode was rejected: %v", err)
	}
}

func TestSettingsOutsideTheirBoundsAreRejected(t *testing.T) {
	c := Default()
	c.Settings.TimeoutMs = 60000
	if c.Validate() == nil {
		t.Error("a 60 s timeout was accepted")
	}
	c = Default()
	c.Settings.HedgeMs = -1
	if c.Validate() == nil {
		t.Error("a negative hedge delay was accepted")
	}
}

func TestFilePlantedWithItsOwnPermissionsLosesThem(t *testing.T) {
	path := filepath.Join(t.TempDir(), "planted.json")
	if err := os.WriteFile(path, []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	everyone, _ := windows.CreateWellKnownSid(windows.WinWorldSid)
	acl, err := windows.ACLFromEntries([]windows.EXPLICIT_ACCESS{{
		AccessPermissions: windows.GENERIC_ALL, AccessMode: windows.GRANT_ACCESS,
		Trustee: windows.TRUSTEE{TrusteeForm: windows.TRUSTEE_IS_SID, TrusteeType: windows.TRUSTEE_IS_WELL_KNOWN_GROUP, TrusteeValue: windows.TrusteeValueFromSID(everyone)},
	}}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if err := windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, acl, nil); err != nil {
		t.Fatal(err)
	}
	if err := inheritOnly(path); err != nil {
		t.Fatal(err)
	}
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(sd.String(), ";;;WD)") {
		t.Errorf("the planted entry for Everyone survived: %s", sd.String())
	}
}

// Version 0.2.0 saved strict mode with TCP DNS allowed and accepted settings
// outside today's bounds. Such a file must keep protection on after an
// upgrade instead of being replaced by defaults.
func TestOlderConfigurationIsMigratedInsteadOfDiscarded(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	old := `{"active":true,"upstreams":[{"id":"a","name":"A","url":"https://dns.example/dns-query","enabled":true}],"rules":[],
		"settings":{"timeoutMs":200,"hedgeMs":0,"failThreshold":3,"cooldownSec":7200,"onFailure":"servfail","cache":true,
		"cacheMinTtl":0,"cacheMaxTtl":5,"interceptLoopback":true,"tcpMode":"allow"}}`
	if err := os.WriteFile(path, []byte(old), 0o644); err != nil {
		t.Fatal(err)
	}
	cfg, problem := Recover(path)
	if problem != "" {
		t.Fatalf("the older file was treated as damaged: %s", problem)
	}
	s := cfg.Settings
	if !cfg.Active || len(cfg.Upstreams) != 1 || s.OnFailure != FailServfail {
		t.Errorf("protection or servers were lost: active=%v upstreams=%d onFailure=%s", cfg.Active, len(cfg.Upstreams), s.OnFailure)
	}
	if s.TCPMode != TCPProxy || s.TimeoutMs != 500 || s.CooldownSec != 3600 || s.CacheMaxTTL != 10 {
		t.Errorf("settings were not brought within bounds: tcp=%s timeout=%d cooldown=%d cacheMax=%d", s.TCPMode, s.TimeoutMs, s.CooldownSec, s.CacheMaxTTL)
	}
}
