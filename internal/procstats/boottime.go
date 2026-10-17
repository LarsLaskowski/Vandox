package procstats

import (
	"io/fs"
	"time"
)

// readBootTime returns the boot time from the btime line of proc/stat.
func readBootTime(proc fs.FS) (time.Time, error) {
	return time.Time{}, errNotImplemented
}
