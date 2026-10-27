package config

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// dirSDDL gives SYSTEM and Administrators full control of the data folder and
// everything in it, owned by SYSTEM. Other accounts get no access, so they
// can neither read the configuration nor plant files that the service would
// later trust or write to.
const dirSDDL = "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"

// DirProblem describes a failure to secure the data folder at startup. It is
// shown in the app next to configuration problems.
var DirProblem string

// SecureDir creates the data folder with restricted permissions or repairs
// an existing one. A folder owned by another account is moved aside and
// replaced, because files planted in it (links in particular) cannot be
// trusted. Files and folders inside that are not owned by SYSTEM or
// Administrators, or that are links, are deleted, and the remaining entries
// lose any permissions of their own.
func SecureDir() error {
	if os.Getenv("NAXDNS_DATA") != "" {
		return os.MkdirAll(Dir(), 0o755) // development runs keep their own folder
	}
	name, _ := windows.UTF16PtrFromString(`Global\NaxDNS.SecureDir`)
	mutex, err := windows.CreateMutex(nil, false, name)
	if err == nil || errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
		windows.WaitForSingleObject(mutex, 10000)
		defer func() {
			windows.ReleaseMutex(mutex)
			windows.CloseHandle(mutex)
		}()
	}
	enablePrivilege("SeTakeOwnershipPrivilege")
	enablePrivilege("SeRestorePrivilege")

	dir := Dir()
	if fi, err := os.Lstat(dir); err == nil {
		trusted, terr := ownedByAdmins(dir)
		if terr != nil {
			return terr
		}
		if !trusted || fi.Mode()&os.ModeSymlink != 0 || fi.Mode()&os.ModeIrregular != 0 || !fi.IsDir() {
			aside := dir + ".untrusted-" + strconv.FormatInt(time.Now().Unix(), 10)
			if err := os.Rename(dir, aside); err != nil {
				return fmt.Errorf("move the untrusted data folder aside: %w", err)
			}
		}
	}
	if _, err := os.Lstat(dir); errors.Is(err, os.ErrNotExist) {
		sd, err := windows.SecurityDescriptorFromString(dirSDDL)
		if err != nil {
			return err
		}
		sa := &windows.SecurityAttributes{Length: uint32(unsafe.Sizeof(windows.SecurityAttributes{})), SecurityDescriptor: sd}
		path, _ := windows.UTF16PtrFromString(dir)
		if err := windows.CreateDirectory(path, sa); err != nil {
			return fmt.Errorf("create the data folder: %w", err)
		}
	}
	if err := applySDDL(dir, dirSDDL); err != nil {
		return fmt.Errorf("restrict the data folder: %w", err)
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		path := filepath.Join(dir, entry.Name())
		fi, err := os.Lstat(path)
		if err != nil {
			continue
		}
		if fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			os.Remove(path) // removes the link itself, never its target
			continue
		}
		if trusted, err := ownedByAdmins(path); err != nil || !trusted {
			if fi.IsDir() {
				os.RemoveAll(path)
			} else {
				os.Remove(path)
			}
			continue
		}
		inheritOnly(path)
	}
	return nil
}

func applySDDL(path, sddl string) error {
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return err
	}
	owner, _, err := sd.Owner()
	if err != nil {
		return err
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.OWNER_SECURITY_INFORMATION|windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION,
		owner, nil, dacl, nil)
}

// inheritOnly removes permissions set on path itself, so that only the data
// folder's permissions apply to it.
func inheritOnly(path string) error {
	// ACLFromEntries cannot build an empty list, so it comes from SDDL.
	sd, err := windows.SecurityDescriptorFromString("D:")
	if err != nil {
		return err
	}
	empty, _, err := sd.DACL()
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.UNPROTECTED_DACL_SECURITY_INFORMATION, nil, nil, empty, nil)
}

func ownedByAdmins(path string) (bool, error) {
	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION)
	if err != nil {
		return false, err
	}
	owner, _, err := sd.Owner()
	if err != nil {
		return false, err
	}
	return TrustedOwner(owner), nil
}

// TrustedOwner reports whether sid is SYSTEM or the Administrators group.
func TrustedOwner(sid *windows.SID) bool {
	return sid != nil && (sid.IsWellKnown(windows.WinLocalSystemSid) || sid.IsWellKnown(windows.WinBuiltinAdministratorsSid))
}

func enablePrivilege(name string) {
	var luid windows.LUID
	n, _ := windows.UTF16PtrFromString(name)
	if windows.LookupPrivilegeValue(nil, n, &luid) != nil {
		return
	}
	var token windows.Token
	if windows.OpenProcessToken(windows.CurrentProcess(), windows.TOKEN_ADJUST_PRIVILEGES|windows.TOKEN_QUERY, &token) != nil {
		return
	}
	defer token.Close()
	tp := windows.Tokenprivileges{PrivilegeCount: 1}
	tp.Privileges[0] = windows.LUIDAndAttributes{Luid: luid, Attributes: windows.SE_PRIVILEGE_ENABLED}
	windows.AdjustTokenPrivileges(token, false, &tp, 0, nil, nil)
}

// readShared reads a file while allowing it to be replaced at the same time,
// so a reader such as the guard never makes a save fail.
func readShared(path string) ([]byte, error) {
	p, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(p, windows.GENERIC_READ,
		windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE,
		nil, windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		if errors.Is(err, windows.ERROR_FILE_NOT_FOUND) || errors.Is(err, windows.ERROR_PATH_NOT_FOUND) {
			return nil, os.ErrNotExist
		}
		return nil, &os.PathError{Op: "open", Path: path, Err: err}
	}
	f := os.NewFile(uintptr(h), path)
	defer f.Close()
	return io.ReadAll(f)
}

// writeAtomic writes data to a fresh temporary file, flushes it to disk and
// moves it over target. A leftover temporary file is deleted first rather than
// reused, so its permissions never carry over. Replacing target is retried
// for a few seconds while another program (antivirus, for example) holds it
// open.
func writeAtomic(target string, data []byte) error {
	tmp := target + ".tmp"
	if err := os.Remove(tmp); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	f, err := os.OpenFile(tmp, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		return err
	}
	if _, err := f.Write(data); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	if err := f.Sync(); err != nil {
		f.Close()
		os.Remove(tmp)
		return err
	}
	if err := f.Close(); err != nil {
		os.Remove(tmp)
		return err
	}
	deadline := time.Now().Add(3 * time.Second)
	for {
		err = os.Rename(tmp, target)
		if err == nil {
			return nil
		}
		if time.Now().After(deadline) || !(errors.Is(err, windows.ERROR_ACCESS_DENIED) || errors.Is(err, windows.ERROR_SHARING_VIOLATION)) {
			os.Remove(tmp)
			return err
		}
		time.Sleep(50 * time.Millisecond)
	}
}
