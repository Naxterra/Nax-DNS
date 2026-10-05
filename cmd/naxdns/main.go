// naxdns is the desktop half of NaxDNS: a tray icon and a local web UI that
// talks to the service over its control pipe. It runs as the signed-in user.
package main

import (
	"bytes"
	"crypto/rand"
	"crypto/subtle"
	"embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io/fs"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"path/filepath"
	"time"

	"fyne.io/systray"
	"golang.org/x/sys/windows"

	"naxdns/internal/service"
)

//go:embed web
var webFS embed.FS

type app struct {
	url    string // UI address including the access token
	token  string
	client *http.Client
}

func dataDir() string {
	return filepath.Join(os.Getenv("LOCALAPPDATA"), "NaxDNS")
}

func main() {
	if len(os.Args) > 2 && os.Args[1] == "--window" {
		runWindow(os.Args[2])
		return
	}
	trayOnly := len(os.Args) > 1 && os.Args[1] == "--tray"
	instanceFile := filepath.Join(dataDir(), "ui.url")

	// One instance per user: a second launch just opens the existing UI.
	name, _ := windows.UTF16PtrFromString("Local\\NaxDNS.UI")
	if _, err := windows.CreateMutex(nil, false, name); err == windows.ERROR_ALREADY_EXISTS {
		if u, err := os.ReadFile(instanceFile); err == nil && !trayOnly {
			openWindow(string(u))
		}
		return
	}

	closeWindow()

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fatal(err)
	}
	tok := make([]byte, 24)
	rand.Read(tok)
	a := &app{token: hex.EncodeToString(tok), client: service.PipeClient()}
	a.url = fmt.Sprintf("http://%s/?t=%s", ln.Addr(), a.token)
	os.MkdirAll(dataDir(), 0o700)
	os.WriteFile(instanceFile, []byte(a.url), 0o600)
	defer os.Remove(instanceFile)

	go http.Serve(ln, a.handler(ln.Addr().String()))
	if !trayOnly {
		openWindow(a.url)
	}
	systray.Run(a.trayReady, nil)
}

func fatal(err error) {
	text, _ := windows.UTF16PtrFromString(err.Error())
	title, _ := windows.UTF16PtrFromString("NaxDNS")
	windows.MessageBox(0, text, title, windows.MB_ICONERROR)
	os.Exit(1)
}

func (a *app) handler(host string) http.Handler {
	static, _ := fs.Sub(webFS, "web")
	files := http.FileServer(http.FS(static))
	proxy := &httputil.ReverseProxy{
		Rewrite:   func(r *httputil.ProxyRequest) { r.SetURL(&url.URL{Scheme: "http", Host: "naxdns"}) },
		Transport: a.client.Transport,
		ErrorHandler: func(w http.ResponseWriter, _ *http.Request, _ error) {
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusServiceUnavailable)
			w.Write([]byte(`{"error":"The NaxDNS service is not running.","serviceDown":true}`))
		},
	}
	mux := http.NewServeMux()
	mux.Handle("/api/", proxy)
	mux.HandleFunc("POST /local/install", func(w http.ResponseWriter, r *http.Request) {
		if err := elevate("install"); err != nil {
			w.WriteHeader(http.StatusInternalServerError)
			json.NewEncoder(w).Encode(map[string]string{"error": err.Error()})
			return
		}
		w.Write([]byte(`{"ok":true}`))
	})
	mux.Handle("/", files)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// The Host check defeats DNS rebinding; the token keeps other local
		// users and web pages out.
		if r.Host != host {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if t := r.URL.Query().Get("t"); t != "" && r.URL.Path == "/" {
			if subtle.ConstantTimeCompare([]byte(t), []byte(a.token)) == 1 {
				http.SetCookie(w, &http.Cookie{Name: "naxdns", Value: a.token, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode})
				http.Redirect(w, r, "/", http.StatusSeeOther)
				return
			}
		}
		c, err := r.Cookie("naxdns")
		if err != nil || subtle.ConstantTimeCompare([]byte(c.Value), []byte(a.token)) != 1 {
			http.Error(w, "Open NaxDNS from its tray icon.", http.StatusForbidden)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		mux.ServeHTTP(w, r)
	})
}

// elevate runs naxdns-service.exe with a UAC prompt.
func elevate(args string) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	verb, _ := windows.UTF16PtrFromString("runas")
	file, _ := windows.UTF16PtrFromString(filepath.Join(filepath.Dir(exe), "naxdns-service.exe"))
	params, _ := windows.UTF16PtrFromString(args)
	return windows.ShellExecute(0, verb, file, params, nil, windows.SW_HIDE)
}

func (a *app) setActive(active bool) {
	body, _ := json.Marshal(map[string]bool{"active": active})
	if resp, err := a.client.Post("http://naxdns/api/active", "application/json", bytes.NewReader(body)); err == nil {
		resp.Body.Close()
	}
}

func (a *app) trayReady() {
	systray.SetIcon(icon(colorOff))
	systray.SetTitle("NaxDNS")
	systray.SetTooltip("NaxDNS")
	systray.SetOnTapped(func() { openWindow(a.url) })
	open := systray.AddMenuItem("Open NaxDNS", "")
	toggle := systray.AddMenuItemCheckbox("Protection", "", false)
	systray.AddSeparator()
	quit := systray.AddMenuItem("Quit tray icon", "DNS protection keeps running in the service")

	active := false
	refresh := func() {
		var st struct {
			Active       bool   `json:"active"`
			Intercepting bool   `json:"intercepting"`
			Detail       string `json:"detail"`
			Servers      []struct {
				Healthy bool `json:"healthy"`
			} `json:"servers"`
		}
		resp, err := a.client.Get("http://naxdns/api/state")
		if err != nil {
			systray.SetIcon(icon(colorOff))
			systray.SetTooltip("NaxDNS: service not running")
			toggle.Disable()
			return
		}
		json.NewDecoder(resp.Body).Decode(&st)
		resp.Body.Close()
		toggle.Enable()
		active = st.Active
		healthy := false
		for _, s := range st.Servers {
			healthy = healthy || s.Healthy
		}
		switch {
		case !st.Intercepting:
			toggle.Uncheck()
			systray.SetIcon(icon(colorOff))
			systray.SetTooltip("NaxDNS: off")
		case !healthy:
			toggle.Check()
			systray.SetIcon(icon(colorWarn))
			systray.SetTooltip("NaxDNS: no DNS server reachable")
		default:
			toggle.Check()
			systray.SetIcon(icon(colorOn))
			systray.SetTooltip("NaxDNS: " + st.Detail)
		}
	}
	refresh()
	tick := time.NewTicker(3 * time.Second)
	go func() {
		for {
			select {
			case <-tick.C:
				refresh()
			case <-open.ClickedCh:
				openWindow(a.url)
			case <-toggle.ClickedCh:
				a.setActive(!active)
				refresh()
			case <-quit.ClickedCh:
				systray.Quit()
			}
		}
	}()
}
