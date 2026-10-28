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
	rec := Recover(path)
	cfg, problem := rec.Config, rec.Problem
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
	rec := Recover(path)
	cfg, problem := rec.Config, rec.Problem
	if cfg.Active || !strings.Contains(problem, "protection is off") {
		t.Errorf("active=%v problem=%q", cfg.Active, problem)
	}
}

func TestRecoverMissingFileIsNotAProblem(t *testing.T) {
	if problem := Recover(filepath.Join(t.TempDir(), "config.json")).Problem; problem != "" {
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
	kept, _ := Load(BackupPath(path))
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
	os.WriteFile(BackupPath(path), []byte("{also damaged"), 0o644)
	rec := Recover(path)
	cfg, problem := rec.Config, rec.Problem
	if cfg.Active || !strings.Contains(problem, "damaged too") {
		t.Fatalf("active=%v problem=%q", cfg.Active, problem)
	}
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	kept, _ := filepath.Glob(BackupPath(path) + ".broken-*")
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
	rec := Recover(path)
	cfg, problem := rec.Config, rec.Problem
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

// 0.1.x saved parallel backup queries after 1500 ms as its default; they are
// off by default now, and an upgrade must not keep them on silently.
func TestOldParallelQueryDefaultIsSwitchedOffOnUpgrade(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	old := `{"active":true,"upstreams":[{"id":"a","name":"A","url":"https://dns.example/dns-query","enabled":true}],"rules":[],"settings":{"timeoutMs":3000,"hedgeMs":1500,"onFailure":"servfail","cacheMinTtl":7200,"cacheMaxTtl":3600}}`
	if err := os.WriteFile(path, []byte(old), 0o644); err != nil {
		t.Fatal(err)
	}
	cfg, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Settings.HedgeMs != 0 || cfg.Version != SchemaVersion {
		t.Errorf("hedgeMs=%d version=%d after the upgrade, want 0 and %d", cfg.Settings.HedgeMs, cfg.Version, SchemaVersion)
	}
	if cfg.Settings.CacheMinTTL > cfg.Settings.CacheMaxTTL {
		t.Errorf("minimum cache time %d is larger than the maximum %d", cfg.Settings.CacheMinTTL, cfg.Settings.CacheMaxTTL)
	}
	// A current file keeps the owner's choice.
	cfg.Settings.HedgeMs = 800
	if err := cfg.Save(path); err != nil {
		t.Fatal(err)
	}
	if again, _ := Load(path); again.Settings.HedgeMs != 800 {
		t.Errorf("the owner's own parallel query setting was reset to %d", again.Settings.HedgeMs)
	}
}

// A file that another program holds open without sharing is not damaged and
// must not be replaced by the older working copy.
func TestLockedValidConfigurationIsNotTreatedAsDamaged(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	older, newer := Default(), Default()
	newer.Active = true
	if err := older.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := newer.Save(path); err != nil {
		t.Fatal(err)
	}
	p, _ := windows.UTF16PtrFromString(path)
	h, err := windows.CreateFile(p, windows.GENERIC_READ, 0, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		time.Sleep(500 * time.Millisecond)
		windows.CloseHandle(h)
	}()
	rec := Recover(path)
	if rec.Save || !rec.Config.Active {
		t.Errorf("a briefly locked valid file was replaced (save=%v, active=%v, problem=%q)", rec.Save, rec.Config.Active, rec.Problem)
	}
	if broken, _ := filepath.Glob(path + ".broken-*"); len(broken) != 0 {
		t.Errorf("a valid file was kept as damaged: %v", broken)
	}
}

// config.json missing next to a working copy is a recovery case, not a
// first start with protection off.
func TestMissingConfigurationNextToWorkingCopyIsRestored(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	c := Default()
	c.Active = true
	if err := c.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := c.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	rec := Recover(path)
	if !rec.Config.Active || !rec.Save || rec.Problem == "" {
		t.Errorf("active=%v save=%v problem=%q, want the working copy restored and reported", rec.Config.Active, rec.Save, rec.Problem)
	}
}

// A file that is valid JSON but breaks the rules must not become the working
// copy when the restored configuration is saved over it.
func TestInvalidFileNeverReplacesTheWorkingCopy(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	good := Default()
	good.Active = true
	if err := good.Save(path); err != nil {
		t.Fatal(err)
	}
	invalid := `{"version":1,"active":true,"upstreams":[],"rules":[],"settings":{}}` // active without servers
	if err := os.WriteFile(path, []byte(invalid), 0o644); err != nil {
		t.Fatal(err)
	}
	rec := Recover(path)
	if !rec.Save || !rec.Config.Active {
		t.Fatalf("save=%v active=%v problem=%q", rec.Save, rec.Config.Active, rec.Problem)
	}
	if err := rec.Config.Save(path); err != nil {
		t.Fatal(err)
	}
	if kept, err := Load(BackupPath(path)); err != nil || !kept.Active {
		t.Errorf("the working copy was replaced by the invalid file: %v, %+v", err, kept)
	}
}

// A file that stays locked is not replaced, and the caller is told to load
// it again later instead of keeping the stand-in for good.
func TestLockedFileAsksTheCallerToTryAgain(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	c := Default()
	c.Active = true
	if err := c.Save(path); err != nil {
		t.Fatal(err)
	}
	p, _ := windows.UTF16PtrFromString(path)
	h, err := windows.CreateFile(p, windows.GENERIC_READ, 0, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer windows.CloseHandle(h)
	rec := Recover(path)
	if rec.Save || !rec.Retry {
		t.Errorf("save=%v retry=%v, want the locked file left alone and a retry requested", rec.Save, rec.Retry)
	}
}

// A save from the app over a file that was damaged while the service ran,
// for example by a hand edit with a typo, keeps a copy of the damaged file.
func TestSaveOverDamagedFileKeepsACopy(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	if err := os.WriteFile(path, []byte("{hand edit with a typo"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := Default().Save(path); err != nil {
		t.Fatal(err)
	}
	broken, _ := filepath.Glob(path + ".broken-*")
	if len(broken) != 1 {
		t.Fatalf("the damaged file was not kept: %v", broken)
	}
	if data, _ := os.ReadFile(broken[0]); string(data) != "{hand edit with a typo" {
		t.Errorf("the kept copy has the wrong content: %q", data)
	}
}

// A save that cannot read the current file changes neither the file nor its
// working copy.
func TestSaveOverLockedFileChangesNothing(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	c := Default()
	c.Active = true
	if err := c.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := c.Save(path); err != nil {
		t.Fatal(err)
	}
	before, _ := os.ReadFile(BackupPath(path))
	p, _ := windows.UTF16PtrFromString(path)
	h, err := windows.CreateFile(p, windows.GENERIC_READ, 0, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	next := Default()
	err = next.Save(path)
	windows.CloseHandle(h)
	if err == nil {
		t.Fatal("a save over a locked file succeeded")
	}
	if after, _ := os.ReadFile(BackupPath(path)); string(after) != string(before) {
		t.Error("the working copy was replaced by a save that failed")
	}
	if saved, err := Load(path); err != nil || !saved.Active {
		t.Errorf("the configuration changed: %v, %+v", err, saved)
	}
}

// Recovery and the save that follows it read the same damaged file; it must
// be kept once, not twice.
func TestRecoveredDamagedFileIsKeptOnce(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	good := Default()
	if err := good.Save(path); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	rec := Recover(path)
	if !rec.Save {
		t.Fatalf("recovery did not ask for a save: %q", rec.Problem)
	}
	if err := rec.Config.Save(path); err != nil {
		t.Fatal(err)
	}
	if broken, _ := filepath.Glob(path + ".broken-*"); len(broken) != 1 {
		t.Errorf("the damaged file was kept %d times: %v", len(broken), broken)
	}
}
