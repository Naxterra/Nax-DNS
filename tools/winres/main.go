// Command winres writes the Windows resources (icon, manifest and version
// information) of both programs as .syso files, which go build links in. The
// version comes from the service package, so the file details always match
// the version the app reports. build.cmd runs it before every build.
package main

import (
	"image/png"
	"log"
	"os"
	"path/filepath"

	"github.com/tc-hib/winres"
	"github.com/tc-hib/winres/version"

	"naxdns/internal/service"
)

const (
	company   = "Naxterra"
	product   = "Nax-DNSManager"
	copyright = "Copyright (c) 2026 Naxterra. All rights reserved."
	langEnUS  = 0x0409
)

type program struct {
	dir         string
	exe         string
	description string
	manifest    winres.AppManifest
}

func main() {
	icon := loadIcon(filepath.Join("cmd", "naxdns", "winres", "icon.png"))
	programs := []program{
		{
			dir: filepath.Join("cmd", "naxdns"), exe: "Nax-DNSManager.exe", description: product,
			manifest: winres.AppManifest{DPIAwareness: winres.DPIPerMonitorV2, UseCommonControlsV6: true},
		},
		{
			dir: filepath.Join("cmd", "naxdns-service"), exe: "Nax-DNSService.exe", description: product + " Service",
			manifest: winres.AppManifest{},
		},
	}
	for _, p := range programs {
		rs := winres.ResourceSet{}
		if err := rs.SetIcon(winres.Name("APP"), icon); err != nil {
			log.Fatal(err)
		}
		rs.SetManifest(p.manifest)
		rs.SetVersionInfo(versionInfo(p))
		out := filepath.Join(p.dir, "rsrc_windows_amd64.syso")
		f, err := os.Create(out)
		if err != nil {
			log.Fatal(err)
		}
		if err := rs.WriteObject(f, winres.ArchAMD64); err != nil {
			f.Close()
			log.Fatal(err)
		}
		if err := f.Close(); err != nil {
			log.Fatal(err)
		}
	}
}

func loadIcon(path string) *winres.Icon {
	f, err := os.Open(path)
	if err != nil {
		log.Fatal(err)
	}
	defer f.Close()
	img, err := png.Decode(f)
	if err != nil {
		log.Fatal(err)
	}
	icon, err := winres.NewIconFromResizedImage(img, nil)
	if err != nil {
		log.Fatal(err)
	}
	return icon
}

func versionInfo(p program) version.Info {
	vi := version.Info{}
	vi.SetFileVersion(service.Version)
	vi.SetProductVersion(service.Version)
	for key, value := range map[string]string{
		version.CompanyName:      company,
		version.FileDescription:  p.description,
		version.FileVersion:      service.Version,
		version.InternalName:     p.exe,
		version.LegalCopyright:   copyright,
		version.OriginalFilename: p.exe,
		version.ProductName:      product,
		version.ProductVersion:   service.Version,
	} {
		if err := vi.Set(langEnUS, key, value); err != nil {
			log.Fatal(err)
		}
	}
	return vi
}
