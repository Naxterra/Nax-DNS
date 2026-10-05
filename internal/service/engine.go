// Package service ties configuration, resolver and interception together and
// exposes them over the local control pipe.
package service

import (
	"encoding/json"
	"log"
	"path/filepath"
	"reflect"
	"sync"

	"naxdns/internal/config"
	"naxdns/internal/intercept"
	"naxdns/internal/resolver"
)

// Version is shown in the UI.
const Version = "0.1.1"

type Engine struct {
	mu   sync.Mutex
	cfg  *config.Config
	path string

	Resolver    *resolver.Resolver
	Interceptor *intercept.Manager
}

// NewEngine loads the configuration and starts routing if it was active.
func NewEngine() (*Engine, error) {
	e := &Engine{path: filepath.Join(config.Dir(), "config.json"), Resolver: resolver.New()}
	e.Interceptor = intercept.NewManager(e.Resolver)
	cfg, err := config.Load(e.path)
	if err != nil {
		log.Printf("configuration unusable, starting with defaults: %v", err)
		cfg = config.Default()
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

// Apply validates, activates and persists a new configuration. If interception
// cannot start, the configuration is kept but saved as inactive.
func (e *Engine) Apply(next *config.Config) error {
	next = clone(next)
	if err := next.Validate(); err != nil {
		return err
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	routingChanged := !reflect.DeepEqual(e.cfg.Upstreams, next.Upstreams) ||
		!reflect.DeepEqual(e.cfg.Rules, next.Rules) || !reflect.DeepEqual(e.cfg.Settings, next.Settings)
	if routingChanged {
		if err := e.Resolver.Reload(next); err != nil {
			return err
		}
	}
	var applyErr error
	running, _ := e.Interceptor.Status()
	if next.Active != running || !reflect.DeepEqual(e.cfg.Settings, next.Settings) {
		if applyErr = e.Interceptor.Apply(next.Active, next.Settings); applyErr != nil {
			next.Active = false
		}
	}
	e.cfg = next
	if err := next.Save(e.path); err != nil {
		return err
	}
	return applyErr
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
