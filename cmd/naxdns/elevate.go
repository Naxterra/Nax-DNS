package main

import (
	"fmt"
	"os"
	"path/filepath"
	"unsafe"

	"golang.org/x/sys/windows"
)

// shellExecuteInfo mirrors SHELLEXECUTEINFOW.
type shellExecuteInfo struct {
	cbSize       uint32
	fMask        uint32
	hwnd         uintptr
	lpVerb       *uint16
	lpFile       *uint16
	lpParameters *uint16
	lpDirectory  *uint16
	nShow        int32
	hInstApp     uintptr
	lpIDList     uintptr
	lpClass      *uint16
	hkeyClass    uintptr
	dwHotKey     uint32
	hIcon        uintptr
	hProcess     windows.Handle
}

var procShellExecuteEx = windows.NewLazySystemDLL("shell32.dll").NewProc("ShellExecuteExW")

// elevateWait runs Nax-DNSService.exe with a UAC prompt, waits for it to
// finish and returns its exit code.
func elevateWait(args string) (uint32, error) {
	exe, err := os.Executable()
	if err != nil {
		return 0, err
	}
	const seeMaskNoCloseProcess = 0x40
	info := shellExecuteInfo{fMask: seeMaskNoCloseProcess, nShow: windows.SW_HIDE}
	info.cbSize = uint32(unsafe.Sizeof(info))
	info.lpVerb, _ = windows.UTF16PtrFromString("runas")
	info.lpFile, _ = windows.UTF16PtrFromString(filepath.Join(filepath.Dir(exe), "Nax-DNSService.exe"))
	info.lpParameters, _ = windows.UTF16PtrFromString(args)
	if r, _, err := procShellExecuteEx.Call(uintptr(unsafe.Pointer(&info))); r == 0 {
		return 0, fmt.Errorf("the installer could not be started: %w", err)
	}
	if info.hProcess == 0 {
		return 0, fmt.Errorf("the installer could not be started")
	}
	defer windows.CloseHandle(info.hProcess)
	windows.WaitForSingleObject(info.hProcess, windows.INFINITE)
	var code uint32
	if err := windows.GetExitCodeProcess(info.hProcess, &code); err != nil {
		return 0, err
	}
	return code, nil
}

// restartAfterInstall starts the newly installed app, which waits for this
// process to exit before it takes over the tray icon and opens its window.
func restartAfterInstall() {
	exe := filepath.Join(os.Getenv("ProgramFiles"), "Nax-DNSManager", "Nax-DNSManager.exe")
	attr := &os.ProcAttr{Files: []*os.File{nil, nil, nil}}
	if p, err := os.StartProcess(exe, []string{exe, "--after", fmt.Sprint(os.Getpid())}, attr); err == nil {
		p.Release()
	}
}

// waitForExit blocks until process pid has ended, for at most ten seconds.
func waitForExit(pid string) {
	var id uint32
	if _, err := fmt.Sscan(pid, &id); err != nil {
		return
	}
	h, err := windows.OpenProcess(windows.SYNCHRONIZE, false, id)
	if err != nil {
		return
	}
	defer windows.CloseHandle(h)
	windows.WaitForSingleObject(h, 10000)
}
