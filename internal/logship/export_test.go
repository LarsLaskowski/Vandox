package logship

import (
	"os"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// SwapRecordCheck replaces the record check of the collector (the seam of checkRecords) and returns the function that puts the real
// one back. Tests that swap it never run in parallel.
func SwapRecordCheck(check func(r *model.Record) (int, error)) (restore func()) {
	original := checkRecord
	checkRecord = check
	return func() { checkRecord = original }
}

// SwapReadAt replaces the read of a file at an offset (the seam of the renamed file's read) and returns the function that puts the real
// one back. Tests that swap it never run in parallel.
func SwapReadAt(read func(f *os.File, p []byte, off int64) (int, error)) (restore func()) {
	original := readAt
	readAt = read
	return func() { readAt = original }
}
