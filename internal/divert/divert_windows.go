// Package divert is a minimal binding to WinDivert.dll (2.2), loaded from the
// directory of the running executable.
package divert

import (
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"unsafe"

	"golang.org/x/sys/windows"
)

const (
	LayerNetwork = 0

	FlagDrop = 0x0002

	PriorityHighest = 30000

	shutdownRecv = 0x1
)

// Address mirrors WINDIVERT_ADDRESS (80 bytes).
type Address struct {
	Timestamp int64
	flags     uint32
	_         uint32
	_         [64]byte
}

const (
	bitOutbound = 1 << 17
	bitLoopback = 1 << 18
	bitIPv6     = 1 << 20
)

func (a *Address) Outbound() bool { return a.flags&bitOutbound != 0 }
func (a *Address) Loopback() bool { return a.flags&bitLoopback != 0 }
func (a *Address) IPv6() bool     { return a.flags&bitIPv6 != 0 }

func (a *Address) SetOutbound(v bool) {
	if v {
		a.flags |= bitOutbound
	} else {
		a.flags &^= bitOutbound
	}
}

var (
	loadOnce sync.Once
	loadErr  error

	procOpen, procRecv, procSend, procShutdown, procClose, procChecksums *windows.LazyProc
)

func load() error {
	loadOnce.Do(func() {
		exe, err := os.Executable()
		if err != nil {
			loadErr = err
			return
		}
		dll := windows.NewLazyDLL(filepath.Join(filepath.Dir(exe), "WinDivert.dll"))
		if loadErr = dll.Load(); loadErr != nil {
			return
		}
		procOpen = dll.NewProc("WinDivertOpen")
		procRecv = dll.NewProc("WinDivertRecv")
		procSend = dll.NewProc("WinDivertSend")
		procShutdown = dll.NewProc("WinDivertShutdown")
		procClose = dll.NewProc("WinDivertClose")
		procChecksums = dll.NewProc("WinDivertHelperCalcChecksums")
	})
	return loadErr
}

type Handle struct{ h uintptr }

// Open starts diverting packets that match filter. Requires administrator
// rights; the first call installs the WinDivert driver.
func Open(filter string, priority int16, flags uint64) (*Handle, error) {
	if err := load(); err != nil {
		return nil, fmt.Errorf("WinDivert.dll: %w", err)
	}
	f, err := windows.BytePtrFromString(filter)
	if err != nil {
		return nil, err
	}
	h, _, callErr := procOpen.Call(uintptr(unsafe.Pointer(f)), LayerNetwork, uintptr(priority), uintptr(flags))
	if h == uintptr(windows.InvalidHandle) {
		return nil, fmt.Errorf("WinDivertOpen: %w", callErr)
	}
	return &Handle{h}, nil
}

// Recv blocks for the next packet. It fails once Shutdown has been called.
func (h *Handle) Recv(buf []byte, addr *Address) (int, error) {
	var n uint32
	ok, _, callErr := procRecv.Call(h.h, uintptr(unsafe.Pointer(&buf[0])), uintptr(len(buf)),
		uintptr(unsafe.Pointer(&n)), uintptr(unsafe.Pointer(addr)))
	if ok == 0 {
		return 0, callErr
	}
	return int(n), nil
}

// Send injects a packet. Checksums are recalculated first.
func (h *Handle) Send(packet []byte, addr *Address) error {
	procChecksums.Call(uintptr(unsafe.Pointer(&packet[0])), uintptr(len(packet)), uintptr(unsafe.Pointer(addr)), 0)
	ok, _, callErr := procSend.Call(h.h, uintptr(unsafe.Pointer(&packet[0])), uintptr(len(packet)),
		0, uintptr(unsafe.Pointer(addr)))
	if ok == 0 {
		return callErr
	}
	return nil
}

// Shutdown stops diverting and unblocks Recv; Send keeps working until Close.
func (h *Handle) Shutdown() { procShutdown.Call(h.h, shutdownRecv) }

// Close releases the handle. Call it after every Recv and Send has returned.
func (h *Handle) Close() { procClose.Call(h.h) }
