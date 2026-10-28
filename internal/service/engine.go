// Package service ties configuration, resolver and interception together and
// exposes them over the local control pipe.
package service

import (
	"encoding/json"
	"fmt"
	"log"
	"path/filepath"
	"reflect"
	"sync"
	"time"

	"naxdns/internal/config"
	"naxdns/internal/intercept"
	"naxdns/internal/resolver"
)

// Version is shown in the UI.
const Version = "0.3.0"

type Engine struct {
	mu   sync.Mutex
	cfg  *config.Config
	path string

	// configProblem describes a configuration that could not be loaded at
	// startup. It is shown in the app until the configuration is saved again.
	configProblem string

	Resolver    *resolver.Resolver
	Interceptor *intercept.Manager
}

// NewEngine loads the configuration and starts routing if it was active.
func NewEngine() (*Engine, error) {
	e := &Engine{path: filepath.Join(config.Dir(), "config.json"), Resolver: resolver.New()}
	e.Interceptor = intercept.NewManager(e.Resolver)
	rec := config.Recover(e.path)
	cfg := rec.Config
	if rec.Problem != "" {
		log.Print(rec.Problem)
		e.configProblem = rec.Problem
	}
	if rec.Save {
		if err := cfg.Save(e.path); err != nil {
			e.configProblem += fmt.Sprintf(" The restored configuration could not be saved (%v).", err)
			log.Print(e.configProblem)
		}
	}
	if rec.Retry {
		go e.reloadWhenReadable()
	}
	if err := e.Resolver.Reload(cfg); err != nil {
		return nil, err
	}
	e.cfg = cfg
	if err := e.Interceptor.Apply(cfg.Active, cfg.Settings); err != nil {
		log.Printf("interception not started: %v", err)
	}
	return e, nil
}

// Config returns a deep copy of the current configuration.
func (e *Engine) Config() *config.Config {
	e.mu.Lock()
	defer e.mu.Unlock()
	return clone(e.cfg)
}

func clone(c *config.Config) *config.Config {
	data, _ := json.Marshal(c)
	out := &config.Config{}
	json.Unmarshal(data, out)
	out.Normalize()
	return out
}

// Apply validates, persists and activates a new configuration. The file is
// saved first, because the guard follows the file: if saving fails, nothing
// changes. If interception cannot start afterwards, the configuration stays
// active on disk, so strict mode keeps failing closed, and the interceptor
// keeps retrying in the background.
func (e *Engine) Apply(next *config.Config) error {
	next = clone(next)
	next.Version = config.SchemaVersion // a change from the app is current by definition
	if err := next.Validate(); err != nil {
		return err
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	prev := e.cfg
	routingChanged := !reflect.DeepEqual(prev.Upstreams, next.Upstreams) ||
		!reflect.DeepEqual(prev.Rules, next.Rules) || !reflect.DeepEqual(prev.Settings, next.Settings)
	if routingChanged {
		if err := e.Resolver.Reload(next); err != nil {
			return err
		}
	}
	if err := next.Save(e.path); err != nil {
		if routingChanged {
			e.Resolver.Reload(prev)
		}
		return fmt.Errorf("the configuration could not be saved, so nothing was changed: %w", err)
	}
	e.cfg = next
	e.configProblem = ""
	running, _ := e.Interceptor.Status()
	if next.Active != prev.Active || (next.Active && !running) || !reflect.DeepEqual(prev.Settings, next.Settings) {
		if err := e.Interceptor.Apply(next.Active, next.Settings); err != nil {
			return fmt.Errorf("protection is on, but DNS interception could not start (%v); it is retried every few seconds", err)
		}
	}
	return nil
}

// reloadWhenReadable loads config.json again every second until it can be
// read, for at most ten minutes, and then switches to it. Until then the
// resolver uses a stand-in while the guard keeps its own state, so the two
// may disagree; this ends the disagreement as soon as the file is readable.
func (e *Engine) reloadWhenReadable() {
	for i := 0; i < 600; i++ {
		time.Sleep(time.Second)
		cfg, err := config.Load(e.path)
		if err != nil {
			continue
		}
		if err := e.Apply(cfg); err != nil {
			log.Printf("the configuration is readable again but could not be applied: %v", err)
		} else {
			log.Print("the configuration is readable again and in use")
		}
		return
	}
}

// ConfigProblem returns the startup configuration problem, if any.
func (e *Engine) ConfigProblem() string {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.configProblem
}

// SetActive switches protection on or off.
func (e *Engine) SetActive(active bool) error {
	cfg := e.Config()
	cfg.Active = active
	return e.Apply(cfg)
}

func (e *Engine) Close() {
	e.Interceptor.Apply(false, config.Settings{})
	e.Resolver.Close()
}
