package spool

import "os"

// SetWriteFile replaces the write seam and returns a function that restores it.
func SetWriteFile(fn func(f *os.File, b []byte, off int64) (int, error)) func() {
	old := writeFile
	writeFile = fn

	return func() { writeFile = old }
}
