package main

import (
	"os"
	"testing"
)

// TestExportIcon writes the application icon used for the executable's
// resources (winres/icon.png, compiled into the .syso by go-winres) when
// NAXDNS_ICON_OUT is set.
func TestExportIcon(t *testing.T) {
	out := os.Getenv("NAXDNS_ICON_OUT")
	if out == "" {
		t.Skip("NAXDNS_ICON_OUT not set")
	}
	if err := os.WriteFile(out, iconPNG(colorOn), 0o644); err != nil {
		t.Fatal(err)
	}
}
