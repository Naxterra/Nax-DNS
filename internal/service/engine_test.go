package service

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"

	"naxdns/internal/config"
)

func newTestEngine(t *testing.T) *Engine {
	t.Helper()
	t.Setenv("NAXDNS_DATA", t.TempDir())
	e, err := NewEngine()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(e.Close)
	return e
}

// A config.json that another program keeps locked at startup must not be
// replaced by a change from the app, and must be picked up once it unlocks.
func TestChangesWaitUntilTheSavedConfigurationCanBeRead(t *testing.T) {
	dir := t.TempDir()
	t.Setenv("NAXDNS_DATA", dir)
	path := filepath.Join(dir, "config.json")
	saved := config.Default()
	saved.Rules = append(saved.Rules, config.Rule{ID: "own", Name: "own rule", Enabled: true, Domains: []string{"example.com"}, Action: config.ActionBlock})
	if err := saved.Save(path); err != nil {
		t.Fatal(err)
	}
	os.Remove(config.BackupPath(path)) // no working copy: the stand-in is the defaults
	p, _ := windows.UTF16PtrFromString(path)
	h, err := windows.CreateFile(p, windows.GENERIC_READ, 0, nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		t.Fatal(err)
	}
	e, err := NewEngine()
	if err != nil {
		windows.CloseHandle(h)
		t.Fatal(err)
	}
	t.Cleanup(e.Close)
	if err := e.Apply(e.Config()); !errors.Is(err, errPending) {
		t.Errorf("a change was accepted while the saved configuration was unreadable: %v", err)
	}
	windows.CloseHandle(h)
	deadline := time.Now().Add(15 * time.Second)
	for time.Now().Before(deadline) {
		if len(e.Config().Rules) == len(saved.Rules) {
			return
		}
		time.Sleep(200 * time.Millisecond)
	}
	t.Errorf("the configuration was not picked up after it became readable: %d rules, want %d", len(e.Config().Rules), len(saved.Rules))
}

// In tests WinDivert cannot be loaded, so turning protection on always fails
// to start interception, just like a blocked driver on a real system.
func TestInterceptionFailureKeepsProtectionOnDisk(t *testing.T) {
	e := newTestEngine(t)
	next := e.Config()
	next.Active = true
	if err := e.Apply(next); err == nil {
		t.Fatal("expected interception to fail without WinDivert")
	}
	saved, err := config.Load(filepath.Join(config.Dir(), "config.json"))
	if err != nil {
		t.Fatal(err)
	}
	if !saved.Active || !e.Config().Active {
		t.Errorf("protection was switched off after a failed start (disk %v, service %v), which would open the kill switch", saved.Active, e.Config().Active)
	}
}

func TestFailedSaveChangesNothing(t *testing.T) {
	e := newTestEngine(t)
	before := e.Config()
	// A non-empty folder where a temporary file must go makes every save fail.
	blocker := filepath.Join(config.Dir(), "config.json.last-good.tmp")
	if err := os.MkdirAll(filepath.Join(blocker, "x"), 0o755); err != nil {
		t.Fatal(err)
	}
	next := e.Config()
	next.Rules = append(next.Rules, config.Rule{ID: "t", Name: "test", Enabled: true, Domains: []string{"example.com"}, Action: config.ActionBlock})
	if err := e.Apply(next); err == nil {
		t.Fatal("the save did not fail")
	}
	if got := e.Config(); len(got.Rules) != len(before.Rules) {
		t.Errorf("the service applied a configuration it could not save: %d rules, want %d", len(got.Rules), len(before.Rules))
	}
}

func TestControlAccountCannotCreatePipeInstances(t *testing.T) {
	ace := controlUserACE("S-1-5-21-1-2-3-1001")
	hex := ace[strings.Index(ace, "0x")+2 : strings.Index(ace, ";;;")]
	rights, err := strconv.ParseUint(hex, 16, 32)
	if err != nil {
		t.Fatal(err)
	}
	const fileCreatePipeInstance = 0x4
	if rights&fileCreatePipeInstance != 0 {
		t.Errorf("the control account may create pipe instances: %#x", rights)
	}
	if rights&clientAccess&0xffff != clientAccess&0xffff {
		t.Errorf("the granted rights %#x do not cover what the client requests", rights)
	}
}

func TestPipeServedByAnotherProgramIsRefused(t *testing.T) {
	t.Setenv("NAXDNS_DATA", "")
	name := `\\.\pipe\NaxDNS.Test.` + strconv.Itoa(os.Getpid())
	ln, err := winio.ListenPipe(name, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	go func() {
		if c, err := ln.Accept(); err == nil {
			defer c.Close()
			time.Sleep(time.Second)
		}
	}()
	conn, err := winio.DialPipeAccess(context.Background(), name, clientAccess)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if err := verifyServer(conn); !errors.Is(err, ErrNotService) {
		t.Errorf("a pipe served by a test program was accepted as the service: %v", err)
	}
}
