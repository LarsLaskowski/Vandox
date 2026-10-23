package logship

import "os"

// OpenNoLinks opens the regular file at path, which must be absolute, through a walk from "/" that refuses a link in any element.
func OpenNoLinks(path string) (*os.File, error) {
	return nil, errNotImplemented
}
