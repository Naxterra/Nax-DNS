// Nax-DNSService is the privileged half of Nax-DNSManager: it intercepts DNS, resolves
// it over encrypted transports and serves the control pipe.
//
//	Nax-DNSService            run as Windows service (started by the SCM)
//	Nax-DNSService run        run in the foreground (elevated console)
//	Nax-DNSService guard      run the strict-mode guard (its own Windows service)
//	Nax-DNSService install [--user SID]  copy to Program Files, register and start
//	Nax-DNSService uninstall  stop and remove
//	Nax-DNSService test [url...]  measure servers from this network
package main

import (
	"context"
	"fmt"
	"io"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"sort"
	"sync"

	"golang.org/x/sys/windows/svc"

	"naxdns/internal/config"
	"naxdns/internal/guard"
	"naxdns/internal/resolver"
	"naxdns/internal/service"
	"naxdns/internal/upstream"
)

func main() {
	isService, err := svc.IsWindowsService()
	if err != nil {
		log.Fatal(err)
	}
	cmd := ""
	if len(os.Args) > 1 {
		cmd = os.Args[1]
	}
	if isService {
		name, h := serviceName, handler{run: run}
		if cmd == "guard" {
			name, h = guard.ServiceName, handler{run: runGuard}
		}
		secureDataDir()
		setupLog(name, false)
		if err := svc.Run(name, h); err != nil {
			log.Fatal(err)
		}
		return
	}
	switch cmd {
	case "run", "guard":
		f := run
		if cmd == "guard" {
			f = runGuard
		}
		secureDataDir()
		setupLog(cmd, true)
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
		defer cancel()
		if err := f(ctx); err != nil {
			log.Fatal(err)
		}
	case "install":
		user := ""
		for i := 2; i < len(os.Args); i++ {
			switch {
			case os.Args[i] == "--user" && i+1 < len(os.Args):
				user = os.Args[i+1]
				i++
			case os.Args[i] == "--keep-tray":
				keepTray = true
			}
		}
		exitOn(install(user))
	case "uninstall":
		exitOn(uninstall())
	case "test":
		test(os.Args[2:])
	default:
		fmt.Println("usage: Nax-DNSService run | guard | install [--user SID] | uninstall | test [url...]")
		os.Exit(2)
	}
}

func exitOn(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

// setupLog writes to service.log, or to guard.log for the guard.
func setupLog(name string, console bool) {
	if dirErr != nil {
		return // never write into a folder that could not be secured
	}
	os.MkdirAll(config.Dir(), 0o755)
	file := "service.log"
	if name == "guard" || name == guard.ServiceName {
		file = "guard.log"
	}
	path := filepath.Join(config.Dir(), file)
	if fi, err := os.Stat(path); err == nil && fi.Size() > 5<<20 {
		os.Rename(path, path+".old")
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	if console {
		log.SetOutput(io.MultiWriter(os.Stderr, f))
	} else {
		log.SetOutput(f)
	}
}

func run(ctx context.Context) error {
	if dirErr != nil {
		return dirErr
	}
	engine, err := service.NewEngine()
	if err != nil {
		return err
	}
	defer engine.Close()
	_, detail := engine.Interceptor.Status()
	log.Printf("Nax-DNSManager %s started: %s", service.Version, detail)
	return engine.Serve(ctx)
}

func runGuard(ctx context.Context) error {
	if dirErr != nil {
		return dirErr
	}
	log.Printf("Nax-DNSManager guard %s started", service.Version)
	guard.Run(ctx)
	return nil
}

// handler runs one of the two services until the SCM stops it.
type handler struct {
	run func(context.Context) error
}

func (h handler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- h.run(ctx) }()
	status <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	for {
		select {
		case err := <-done:
			if err != nil {
				log.Printf("service stopped: %v", err)
				return false, 1
			}
			return false, 0
		case req := <-requests:
			switch req.Cmd {
			case svc.Interrogate:
				status <- req.CurrentStatus
			case svc.Stop, svc.Shutdown:
				status <- svc.Status{State: svc.StopPending}
				cancel()
				<-done
				return false, 0
			}
		}
	}
}

// test measures the configured servers, or the given URLs, and prints them
// fastest first. It does not touch live routing. With URLs it needs no
// elevation; without them it reads the saved configuration, which only
// administrators may open.
func test(urls []string) {
	settings := config.Default().Settings
	var servers []config.Upstream
	for _, u := range urls {
		servers = append(servers, config.Upstream{ID: u, Name: u, URL: u})
	}
	if len(servers) == 0 {
		cfg, err := config.Load(filepath.Join(config.Dir(), "config.json"))
		exitOn(err)
		servers, settings = cfg.Upstreams, cfg.Settings
	}
	results := make([]resolver.TestResult, len(servers))
	var wg sync.WaitGroup
	for i, u := range servers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			results[i] = resolver.Test(context.Background(), u, settings)
		}()
	}
	wg.Wait()
	order := make([]int, len(servers))
	for i := range order {
		order[i] = i
	}
	sort.Slice(order, func(a, b int) bool {
		ra, rb := results[order[a]], results[order[b]]
		if ra.OK != rb.OK {
			return ra.OK
		}
		return ra.QueryMs < rb.QueryMs
	})
	fmt.Printf("%-6s %9s %9s  %s\n", "PROTO", "QUERY", "CONNECT", "SERVER")
	for _, i := range order {
		r, u := results[i], servers[i]
		if r.OK {
			fmt.Printf("%-6s %7.1fms %7.1fms  %s\n", upstream.Protocol(u.URL), r.QueryMs, r.ConnectMs, u.URL)
		} else {
			fmt.Printf("%-6s %9s %9s  %s  (%s)\n", upstream.Protocol(u.URL), "failed", "", u.URL, r.Error)
		}
	}
}

// dirErr is set when the data folder could not be secured. Both services
// then refuse to run, because a folder another account controls could hold a
// planted configuration. Windows restarts them, which retries the check.
var dirErr error

// secureDataDir restricts the data folder before anything is written to it.
func secureDataDir() {
	if err := config.SecureDir(); err != nil {
		dirErr = fmt.Errorf("the data folder %s could not be secured, so the service does not use it: %w", config.Dir(), err)
	}
}
