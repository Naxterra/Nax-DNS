package config

import (
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// dirSDDL gives SYSTEM and Administrators full control of the data folder and
// everything in it, owned by SYSTEM. Other accounts get no access, so they
// can neither read the configuration nor plant files that the service would
// later trust or write to.
const dirSDDL = "O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"

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
	// Both services and the installer may run this at the same time, so they
	// take turns through a named lock. Any account can create or hold a
	// global named object, though, so the lock is only best effort: if it is
	// denied or not granted in time, the work goes ahead without it rather
	// than letting another account keep the services from starting. The steps
	// below are safe to repeat and end with a check of the result.
	mutex, err := windows.CreateMutex(nil, false, name)
	if err == nil || errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
		defer windows.CloseHandle(mutex)
		// A short wait: this runs before the service reports to Windows, which
		// allows only about 30 seconds for that, and the guard rechecks with it.
		if event, werr := windows.WaitForSingleObject(mutex, 3000); werr == nil && (event == windows.WAIT_OBJECT_0 || event == windows.WAIT_ABANDONED) {
			defer windows.ReleaseMutex(mutex)
		}
	}
	enablePrivilege("SeTakeOwnershipPrivilege")
	enablePrivilege("SeRestorePrivilege")

	dir := Dir()
	// Another account may recreate the folder right after it was moved aside,
	// so moving and creating are retried, and the result is checked at the end.
	for attempt := 0; attempt < 5; attempt++ {
		if fi, err := os.Lstat(dir); err == nil {
			trusted, terr := ownedByAdmins(dir)
			if terr != nil {
				// Never move the real folder aside because its owner could not
				// be read for a moment; that would hide the configuration.
				return fmt.Errorf("read the owner of the data folder: %w", terr)
			}
			if trusted && fi.IsDir() && fi.Mode()&(os.ModeSymlink|os.ModeIrregular) == 0 {
				break
			}
			aside := dir + ".untrusted-" + strconv.FormatInt(time.Now().UnixNano(), 10)
			if err := os.Rename(dir, aside); err != nil {
				return fmt.Errorf("move the untrusted data folder aside: %w", err)
			}
		}
		sd, err := windows.SecurityDescriptorFromString(dirSDDL)
		if err != nil {
			return err
		}
		sa := &windows.SecurityAttributes{Length: uint32(unsafe.Sizeof(windows.SecurityAttributes{})), SecurityDescriptor: sd}
		path, _ := windows.UTF16PtrFromString(dir)
		if err := windows.CreateDirectory(path, sa); err == nil {
			break
		} else if !errors.Is(err, windows.ERROR_ALREADY_EXISTS) {
			return fmt.Errorf("create the data folder: %w", err)
		}
	}
	if fi, err := os.Lstat(dir); err != nil || !fi.IsDir() || fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
		return fmt.Errorf("the data folder could not be created safely: %v", err)
	}
	if trusted, err := ownedByAdmins(dir); err != nil || !trusted {
		return fmt.Errorf("the data folder belongs to another account and could not be replaced: %v", err)
	}
	if err := applySDDL(dir, dirSDDL); err != nil {
		return fmt.Errorf("restrict the data folder: %w", err)
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		return err
	}
	// Every entry that cannot be made safe is reported instead of skipped: a
	// planted file that is held open survives its deletion, and a file keeps
	// permissions of its own if they could not be removed, so the services
	// must not start on such a folder.
	var problems []string
	for _, entry := range entries {
		path := filepath.Join(dir, entry.Name())
		fi, err := os.Lstat(path)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			problems = append(problems, fmt.Sprintf("%s: %v", entry.Name(), err))
			continue
		}
		if fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			// Removes the link itself, never its target.
			if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
				problems = append(problems, fmt.Sprintf("remove the link %s: %v", entry.Name(), err))
			}
			continue
		}
		if trusted, err := ownedByAdmins(path); err != nil || !trusted {
			remove := os.Remove
			if fi.IsDir() {
				remove = os.RemoveAll
			}
			if err := remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
				problems = append(problems, fmt.Sprintf("remove %s, which belongs to another account: %v", entry.Name(), err))
			}
			continue
		}
		if err := settle(path, func() error { return inheritOnly(path) }); err != nil {
			problems = append(problems, fmt.Sprintf("reset the permissions of %s: %v", entry.Name(), err))
		}
	}
	if len(problems) > 0 {
		return fmt.Errorf("the data folder could not be made safe: %s", strings.Join(problems, "; "))
	}
	// A last pass checks what the services will actually open.
	entries, err = os.ReadDir(dir)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		path := filepath.Join(dir, entry.Name())
		fi, err := os.Lstat(path)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil || fi.Mode()&(os.ModeSymlink|os.ModeIrregular) != 0 {
			return fmt.Errorf("the data folder still contains %s, which cannot be trusted", entry.Name())
		}
		trusted := false
		if err := settle(path, func() error {
			ok, err := ownedByAdmins(path)
			trusted = ok
			return err
		}); err != nil {
			return fmt.Errorf("the owner of %s in the data folder could not be read: %v", entry.Name(), err)
		}
		if _, err := os.Lstat(path); err == nil && !trusted {
			return fmt.Errorf("the data folder still contains %s, which belongs to another account", entry.Name())
		}
	}
	return nil
}

// settle runs op on an entry of the data folder. The services replace files
// there all the time (the guard renames guard.json.tmp over guard.json every
// second), so an entry that vanishes meanwhile, or that is pending deletion
// and denies access for a moment, is not an error: op is retried briefly and
// succeeds once the entry is gone.
func settle(path string, op func() error) error {
	var err error
	for attempt := 0; attempt < 4; attempt++ {
		if attempt > 0 {
			time.Sleep(100 * time.Millisecond)
		}
		if err = op(); err == nil || errors.Is(err, os.ErrNotExist) {
			return nil
		}
		if _, serr := os.Lstat(path); errors.Is(serr, os.ErrNotExist) {
			return nil
		}
	}
	return err
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

// OwnByAdministrators makes the Administrators group the owner of a file
// that an elevated program created. Depending on Windows policy such a file
// may otherwise belong to the signed-in account, and SecureDir would delete
// it as planted.
func OwnByAdministrators(path string) error {
	admins, err := windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid)
	if err != nil {
		return err
	}
	return windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.OWNER_SECURITY_INFORMATION, admins, nil, nil, nil)
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
