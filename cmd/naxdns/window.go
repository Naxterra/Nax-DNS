package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"unsafe"

	"github.com/jchv/go-webview2"
	"golang.org/x/sys/windows"
)

const (
	windowTitle = "NaxDNS"
	windowClass = "webview" // class registered by go-webview2
)

var (
	user32                  = windows.NewLazySystemDLL("user32.dll")
	procFindWindow          = user32.NewProc("FindWindowW")
	procShowWindow          = user32.NewProc("ShowWindow")
	procIsIconic            = user32.NewProc("IsIconic")
	procSetForegroundWindow = user32.NewProc("SetForegroundWindow")
	procPostMessage         = user32.NewProc("PostMessageW")
	procSendMessage         = user32.NewProc("SendMessageW")
	procCreateIcon          = user32.NewProc("CreateIconFromResourceEx")
	procDwmSetAttribute     = windows.NewLazySystemDLL("dwmapi.dll").NewProc("DwmSetWindowAttribute")
)

func findWindow() uintptr {
	class, _ := windows.UTF16PtrFromString(windowClass)
	title, _ := windows.UTF16PtrFromString(windowTitle)
	hwnd, _, _ := procFindWindow.Call(uintptr(unsafe.Pointer(class)), uintptr(unsafe.Pointer(title)))
	return hwnd
}

// closeWindow closes a window left over from a previous tray instance; its
// session ended with that instance.
func closeWindow() {
	if hwnd := findWindow(); hwnd != 0 {
		const wmClose = 0x0010
		procPostMessage.Call(hwnd, wmClose, 0, 0)
	}
}

// openWindow shows the NaxDNS window. It lives in its own process so closing
// it leaves the tray icon running.
func openWindow(u string) {
	exe, err := os.Executable()
	if err == nil {
		err = exec.Command(exe, "--window", u).Start()
	}
	if err != nil {
		openInBrowser(u)
	}
}

func openInBrowser(u string) {
	verb, _ := windows.UTF16PtrFromString("open")
	target, _ := windows.UTF16PtrFromString(u)
	windows.ShellExecute(0, verb, target, nil, nil, windows.SW_SHOWNORMAL)
}

// runWindow is the body of "naxdns.exe --window <url>": a native window that
// renders the UI with the WebView2 runtime that ships with Windows 11.
func runWindow(u string) {
	runtime.LockOSThread()
	name, _ := windows.UTF16PtrFromString("Local\\NaxDNS.Window")
	if _, err := windows.CreateMutex(nil, false, name); err == windows.ERROR_ALREADY_EXISTS {
		if hwnd := findWindow(); hwnd != 0 {
			if iconic, _, _ := procIsIconic.Call(hwnd); iconic != 0 {
				procShowWindow.Call(hwnd, windows.SW_RESTORE)
			}
			procSetForegroundWindow.Call(hwnd)
		}
		return
	}

	w := webview2.NewWithOptions(webview2.WebViewOptions{
		DataPath:  filepath.Join(dataDir(), "webview"),
		AutoFocus: true,
		WindowOptions: webview2.WindowOptions{
			Title: windowTitle, Width: 1180, Height: 800, Center: true,
		},
	})
	if w == nil {
		openInBrowser(u) // WebView2 runtime missing
		return
	}
	defer w.Destroy()

	hwnd := uintptr(w.Window())
	dark := uint32(1)
	const dwmwaUseImmersiveDarkMode = 20
	procDwmSetAttribute.Call(hwnd, dwmwaUseImmersiveDarkMode, uintptr(unsafe.Pointer(&dark)), 4)
	if img := iconPNG(colorOn); len(img) > 0 {
		const iconVersion, wmSetIcon = 0x00030000, 0x0080
		if hicon, _, _ := procCreateIcon.Call(uintptr(unsafe.Pointer(&img[0])), uintptr(len(img)), 1, iconVersion, 0, 0, 0); hicon != 0 {
			procSendMessage.Call(hwnd, wmSetIcon, 0, hicon) // small
			procSendMessage.Call(hwnd, wmSetIcon, 1, hicon) // big
		}
	}
	w.Navigate(u)
	w.Run()
}
