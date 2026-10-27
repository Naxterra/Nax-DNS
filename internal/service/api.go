package service

import (
	"context"
	"encoding/json"
	"errors"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"sync"
	"time"
	"unsafe"

	"github.com/Microsoft/go-winio"
	"github.com/miekg/dns"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"

	"naxdns/internal/config"
	"naxdns/internal/guard"
	"naxdns/internal/resolver"
)

// PipeName is the control channel between the service and the desktop app.
const PipeName = `\\.\pipe\NaxDNS.Control`

// ParametersKey holds the service settings that the installer writes.
const ParametersKey = `SYSTEM\CurrentControlSet\Services\NaxDNS\Parameters`

// ControlUserValue names the account allowed to use the control pipe besides
// SYSTEM and Administrators.
const ControlUserValue = "ControlUser"

// pipeSDDL gives SYSTEM and Administrators full access. The account that
// installed the app may also read and write, so its tray app works without
// elevation. Other accounts on the PC cannot change the configuration. Without
// a recorded account, only elevated programs can connect.
func pipeSDDL() string {
	sddl := "D:P(A;;GA;;;SY)(A;;GA;;;BA)"
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, ParametersKey, registry.QUERY_VALUE)
	if err != nil {
		return sddl
	}
	defer k.Close()
	sid, _, err := k.GetStringValue(ControlUserValue)
	if err != nil {
		return sddl
	}
	if _, err := windows.StringToSid(sid); err != nil {
		log.Printf("ignoring invalid control user %q", sid)
		return sddl
	}
	return sddl + controlUserACE(sid)
}

// controlUserACE grants sid read access and the right to write data
// (FILE_GENERIC_READ | FILE_WRITE_DATA), matching clientAccess. It leaves out
// FILE_APPEND_DATA, which on a pipe is the right to create pipe instances.
func controlUserACE(sid string) string {
	return "(A;;0x12008b;;;" + sid + ")"
}

type guardState struct {
	Running bool   `json:"running"`
	Enabled bool   `json:"enabled"`
	Error   string `json:"error,omitempty"`
}

type stateResponse struct {
	Version       string                 `json:"version"`
	Active        bool                   `json:"active"`
	Intercepting  bool                   `json:"intercepting"`
	Detail        string                 `json:"detail"`
	Servers       []resolver.ServerState `json:"servers"`
	Stats         resolver.Stats         `json:"stats"`
	Guard         guardState             `json:"guard"`
	ConfigProblem string                 `json:"configProblem,omitempty"`
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
		gs, guardRunning := guard.ReadStatus()
		writeJSON(w, stateResponse{
			Version: Version, Active: e.Config().Active, Intercepting: running, Detail: detail,
			Servers: e.Resolver.ServerStates(), Stats: e.Resolver.Stats(),
			Guard:         guardState{Running: guardRunning, Enabled: guardRunning && gs.Enabled, Error: gs.Error},
			ConfigProblem: e.ConfigProblem(),
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

// Serve listens on the control pipe until ctx is cancelled. If the pipe name
// is taken, for example by another program while the service was stopped,
// it keeps retrying instead of ending the service, so DNS handling continues.
func (e *Engine) Serve(ctx context.Context) error {
	srv := &http.Server{Handler: e.Handler()}
	go func() {
		<-ctx.Done()
		srv.Close()
	}()
	lastErr := ""
	for ctx.Err() == nil {
		ln, err := winio.ListenPipe(PipeName, &winio.PipeConfig{SecurityDescriptor: pipeSDDL()})
		if err != nil {
			if err.Error() != lastErr {
				log.Printf("control pipe unavailable, retrying: %v", err)
				lastErr = err.Error()
			}
			select {
			case <-ctx.Done():
			case <-time.After(2 * time.Second):
			}
			continue
		}
		lastErr = ""
		if err := srv.Serve(ln); err != http.ErrServerClosed {
			log.Printf("control pipe stopped, reopening: %v", err)
			continue
		}
	}
	return nil
}

// clientAccess is what the control account is granted on the pipe: reading
// plus writing data, but not FILE_CREATE_PIPE_INSTANCE, which would let it
// serve its own instances of the pipe.
const clientAccess = windows.GENERIC_READ | windows.FILE_WRITE_DATA

// PipeClient returns an HTTP client that talks to the service. It refuses a
// pipe that is not served by the installed service running in session 0,
// so a program squatting on the pipe name cannot pose as the service.
func PipeClient() *http.Client {
	return &http.Client{
		Timeout: 60 * time.Second,
		Transport: &http.Transport{
			DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
				conn, err := winio.DialPipeAccess(ctx, PipeName, clientAccess)
				if err != nil {
					return nil, err
				}
				if err := verifyServer(conn); err != nil {
					conn.Close()
					return nil, err
				}
				return conn, nil
			},
		},
	}
}

var procGetNamedPipeServerSessionId = windows.NewLazySystemDLL("kernel32.dll").NewProc("GetNamedPipeServerSessionId")

// ErrNotService means the control pipe is served by some other program.
var ErrNotService = errors.New("the control pipe is not served by the Nax-DNSManager service")

func verifyServer(conn net.Conn) error {
	if os.Getenv("NAXDNS_DATA") != "" {
		return nil // development runs serve the pipe from a console
	}
	f, ok := conn.(interface{ Fd() uintptr })
	if !ok {
		return ErrNotService
	}
	h := windows.Handle(f.Fd())
	var session uint32
	if r, _, _ := procGetNamedPipeServerSessionId.Call(uintptr(h), uintptr(unsafe.Pointer(&session))); r == 0 || session != 0 {
		return ErrNotService
	}
	var pid uint32
	if err := windows.GetNamedPipeServerProcessId(h, &pid); err != nil {
		return ErrNotService
	}
	// A normal account may not open the service's process, but it may ask the
	// Service Control Manager which process runs the service.
	if servicePID, err := ServiceProcessID(); err != nil || servicePID == 0 || servicePID != pid {
		return ErrNotService
	}
	return nil
}

// ServiceProcessID returns the process ID of the running NaxDNS service.
func ServiceProcessID() (uint32, error) {
	scm, err := windows.OpenSCManager(nil, nil, windows.SC_MANAGER_CONNECT)
	if err != nil {
		return 0, err
	}
	defer windows.CloseServiceHandle(scm)
	name, _ := windows.UTF16PtrFromString("NaxDNS")
	svc, err := windows.OpenService(scm, name, windows.SERVICE_QUERY_STATUS)
	if err != nil {
		return 0, err
	}
	defer windows.CloseServiceHandle(svc)
	var st windows.SERVICE_STATUS_PROCESS
	var needed uint32
	if err := windows.QueryServiceStatusEx(svc, windows.SC_STATUS_PROCESS_INFO, (*byte)(unsafe.Pointer(&st)), uint32(unsafe.Sizeof(st)), &needed); err != nil {
		return 0, err
	}
	return st.ProcessId, nil
}
