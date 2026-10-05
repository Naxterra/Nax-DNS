// Nax-DNSService is the privileged half of Nax-DNSManager: it intercepts DNS, resolves
// it over encrypted transports and serves the control pipe.
//
//	Nax-DNSService            run as Windows service (started by the SCM)
//	Nax-DNSService run        run in the foreground (elevated console)
//	Nax-DNSService install    copy to Program Files, register and start
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
	"naxdns/internal/resolver"
	"naxdns/internal/service"
	"naxdns/internal/upstream"
)

func main() {
	isService, err := svc.IsWindowsService()
	if err != nil {
		log.Fatal(err)
	}
	if isService {
		setupLog(false)
		if err := svc.Run(serviceName, handler{}); err != nil {
			log.Fatal(err)
		}
		return
	}
	cmd := ""
	if len(os.Args) > 1 {
		cmd = os.Args[1]
	}
	switch cmd {
	case "run":
		setupLog(true)
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
		defer cancel()
		if err := run(ctx); err != nil {
			log.Fatal(err)
		}
	case "install":
		exitOn(install())
	case "uninstall":
		exitOn(uninstall())
	case "test":
		test(os.Args[2:])
	default:
		fmt.Println("usage: Nax-DNSService run | install | uninstall | test [url...]")
		os.Exit(2)
	}
}

func exitOn(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func setupLog(console bool) {
	os.MkdirAll(config.Dir(), 0o755)
	path := filepath.Join(config.Dir(), "service.log")
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
	engine, err := service.NewEngine()
	if err != nil {
		return err
	}
	defer engine.Close()
	_, detail := engine.Interceptor.Status()
	log.Printf("Nax-DNSManager %s started: %s", service.Version, detail)
	return engine.Serve(ctx)
}

type handler struct{}

func (handler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- run(ctx) }()
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
// fastest first. It needs no elevation and does not touch live routing.
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
