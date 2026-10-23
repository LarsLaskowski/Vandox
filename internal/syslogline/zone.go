package syslogline

import (
	"errors"
	"io"
	"io/fs"
	"time"
)

// maxZoneFile is the most bytes read of the time zone file.
const maxZoneFile = 64 << 10

// errZone is the error of a time zone that cannot be loaded; it never holds a path.
var errZone = errors.New("syslogline: time zone not readable")

// LoadZone reads at most 64 KiB of localtime from etc and returns its time zone. An error never falls back to UTC.
func LoadZone(etc fs.FS) (*time.Location, error) {
	f, err := etc.Open("localtime")
	if err != nil {
		return nil, errZone
	}
	defer func() { _ = f.Close() }()
	data, err := io.ReadAll(io.LimitReader(f, maxZoneFile+1))
	if err != nil || len(data) == 0 || len(data) > maxZoneFile {
		return nil, errZone
	}
	loc, err := time.LoadLocationFromTZData("localtime", data)
	if err != nil {
		return nil, errZone
	}
	return loc, nil
}
