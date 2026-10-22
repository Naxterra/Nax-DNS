package config

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
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
	if _, err := os.Stat(path + ".broken"); err != nil {
		t.Errorf("the damaged file was not kept: %v", err)
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
