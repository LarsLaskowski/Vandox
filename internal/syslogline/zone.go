package syslogline

import (
	"errors"
	"io/fs"
	"time"
)

// LoadZone reads at most 64 KiB of localtime from etc and returns its time zone. An error never falls back to UTC.
func LoadZone(etc fs.FS) (*time.Location, error) {
	return nil, errors.New("syslogline: not implemented")
}
