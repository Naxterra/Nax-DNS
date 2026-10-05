package service

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/Microsoft/go-winio"
	"github.com/miekg/dns"

	"naxdns/internal/config"
	"naxdns/internal/resolver"
)

// PipeName is the control channel between the service and the desktop app.
const PipeName = `\\.\pipe\NaxDNS.Control`

// SYSTEM and Administrators get full access; the signed-in interactive user
// may connect so the tray app works without elevation.
const pipeSDDL = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)"

type stateResponse struct {
	Version      string                 `json:"version"`
	Active       bool                   `json:"active"`
	Intercepting bool                   `json:"intercepting"`
	Detail       string                 `json:"detail"`
	Servers      []resolver.ServerState `json:"servers"`
	Stats        resolver.Stats         `json:"stats"`
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, code int, err error) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
}

// Handler serves the control API.
func (e *Engine) Handler() http.Handler {
	mux := http.NewServeMux()

	mux.HandleFunc("GET /api/state", func(w http.ResponseWriter, r *http.Request) {
		running, detail := e.Interceptor.Status()
		writeJSON(w, stateResponse{
			Version: Version, Active: e.Config().Active, Intercepting: running, Detail: detail,
			Servers: e.Resolver.ServerStates(), Stats: e.Resolver.Stats(),
		})
	})

	mux.HandleFunc("GET /api/config", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, e.Config())
	})

	mux.HandleFunc("PUT /api/config", func(w http.ResponseWriter, r *http.Request) {
		cfg := &config.Config{}
		if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<20)).Decode(cfg); err != nil {
			fail(w, http.StatusBadRequest, err)
			return
		}
		if err := e.Apply(cfg); err != nil {
			fail(w, http.StatusBadRequest, err)
			return
		}
		writeJSON(w, e.Config())
	})

	mux.HandleFunc("POST /api/active", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Active bool `json:"active"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			fail(w, http.StatusBadRequest, err)
			return
		}
		if err := e.SetActive(body.Active); err != nil {
			fail(w, http.StatusBadRequest, err)
			return
		}
		writeJSON(w, map[string]bool{"active": body.Active})
	})

	mux.HandleFunc("GET /api/log", func(w http.ResponseWriter, r *http.Request) {
		since, _ := strconv.ParseUint(r.URL.Query().Get("since"), 10, 64)
		writeJSON(w, e.Resolver.LogSince(since))
	})

	mux.HandleFunc("POST /api/cache/flush", func(w http.ResponseWriter, r *http.Request) {
		e.Resolver.FlushCache()
		writeJSON(w, map[string]bool{"ok": true})
	})

	// Test servers with fresh connections. The body may carry unsaved servers;
	// without one, every configured server is tested.
	mux.HandleFunc("POST /api/test", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Upstreams []config.Upstream `json:"upstreams"`
		}
		json.NewDecoder(r.Body).Decode(&body)
		cfg := e.Config()
		if len(body.Upstreams) == 0 {
			body.Upstreams = cfg.Upstreams
		}
		results := make([]resolver.TestResult, len(body.Upstreams))
		var wg sync.WaitGroup
		for i, u := range body.Upstreams {
			if err := config.ValidateUpstreamURL(u.URL); err != nil {
				results[i] = resolver.TestResult{ID: u.ID, Error: err.Error()}
				continue
			}
			wg.Add(1)
			go func() {
				defer wg.Done()
				results[i] = resolver.Test(r.Context(), u, cfg.Settings)
			}()
		}
		wg.Wait()
		writeJSON(w, results)
	})

	// Resolve a name through the live routing path, as a lookup tool.
	mux.HandleFunc("POST /api/resolve", func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Name string `json:"name"`
			Type string `json:"type"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			fail(w, http.StatusBadRequest, err)
			return
		}
		qtype, ok := dns.StringToType[body.Type]
		if !ok {
			qtype = dns.TypeA
		}
		req := new(dns.Msg)
		req.SetQuestion(dns.Fqdn(body.Name), qtype)
		ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
		defer cancel()
		e.Resolver.Resolve(ctx, req, resolver.Origin{Proto: "test"})
		entries := e.Resolver.LogSince(0)
		for i := len(entries) - 1; i >= 0; i-- {
			if entries[i].Proto == "test" {
				writeJSON(w, entries[i])
				return
			}
		}
		writeJSON(w, map[string]string{})
	})

	return mux
}

// Serve listens on the control pipe until ctx is cancelled.
func (e *Engine) Serve(ctx context.Context) error {
	ln, err := winio.ListenPipe(PipeName, &winio.PipeConfig{SecurityDescriptor: pipeSDDL})
	if err != nil {
		return err
	}
	srv := &http.Server{Handler: e.Handler()}
	go func() {
		<-ctx.Done()
		srv.Close()
	}()
	if err := srv.Serve(ln); err != http.ErrServerClosed {
		return err
	}
	return nil
}

// PipeClient returns an HTTP client that talks to the service.
func PipeClient() *http.Client {
	return &http.Client{
		Timeout: 60 * time.Second,
		Transport: &http.Transport{
			DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
				return winio.DialPipeContext(ctx, PipeName)
			},
		},
	}
}
