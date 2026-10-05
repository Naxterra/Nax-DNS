package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"

	"golang.org/x/sys/windows"
)

// The language choice ("auto", "en" or "de") is stored per user; "auto"
// follows the Windows display language. The window's own strings live in
// web/i18n.js; only the tray menu is translated here.
var language atomic.Value // "en" or "de"

func langFile() string { return filepath.Join(dataDir(), "lang") }

func langChoice() string {
	data, _ := os.ReadFile(langFile())
	switch c := strings.TrimSpace(string(data)); c {
	case "en", "de":
		return c
	}
	return "auto"
}

func resolveLang(choice string) string {
	if choice != "auto" {
		return choice
	}
	const primaryMask, german = 0x3ff, 0x07
	id, _, _ := windows.NewLazySystemDLL("kernel32.dll").NewProc("GetUserDefaultUILanguage").Call()
	if id&primaryMask == german {
		return "de"
	}
	return "en"
}

func lang() string {
	if l, ok := language.Load().(string); ok {
		return l
	}
	l := resolveLang(langChoice())
	language.Store(l)
	return l
}

var german = map[string]string{
	"Open Nax-DNSManager":                     "Nax-DNSManager öffnen",
	"Protection":                              "Schutz",
	"Quit tray icon":                          "Tray-Symbol beenden",
	"Nax-DNSManager: service not running":     "Nax-DNSManager: Dienst läuft nicht",
	"Nax-DNSManager: off":                     "Nax-DNSManager: aus",
	"Nax-DNSManager: no DNS server reachable": "Nax-DNSManager: kein DNS-Server erreichbar",
	"Intercepting UDP/53 and TCP/53":          "UDP/53 und TCP/53 werden abgefangen",
	"Intercepting UDP/53, blocking TCP/53":    "UDP/53 wird abgefangen, TCP/53 blockiert",
	"Intercepting UDP/53":                     "UDP/53 wird abgefangen",
}

// tr translates a tray string into the current language.
func tr(s string) string {
	if lang() == "de" {
		if t, ok := german[s]; ok {
			return t
		}
	}
	return s
}

// handleLang serves GET and PUT /local/lang for the window.
func handleLang(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodPut {
		var body struct {
			Choice string `json:"choice"`
		}
		json.NewDecoder(r.Body).Decode(&body)
		switch body.Choice {
		case "en", "de":
			os.WriteFile(langFile(), []byte(body.Choice), 0o600)
		default:
			os.Remove(langFile())
		}
		language.Store(resolveLang(langChoice()))
	}
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(map[string]string{"choice": langChoice(), "lang": lang()})
}
