// Package ownports tracks local UDP ports of plain-DNS sockets opened by this
// process, so the interceptor lets those packets through instead of looping.
package ownports

import "sync"

var ports sync.Map

func Add(port uint16)           { ports.Store(port, struct{}{}) }
func Remove(port uint16)        { ports.Delete(port) }
func Contains(port uint16) bool { _, ok := ports.Load(port); return ok }
