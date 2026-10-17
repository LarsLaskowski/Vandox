package procstats

import "io/fs"

// readPasswd returns the user names of etc/passwd by user ID.
func readPasswd(etc fs.FS) (map[uint32]string, error) {
	return nil, errNotImplemented
}
