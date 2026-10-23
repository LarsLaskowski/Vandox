package logship

import "github.com/LarsLaskowski/Vandox/internal/model"

// SwapRecordCheck replaces the record check of the collector (the seam of checkRecords) and returns the function that puts the real
// one back. Tests that swap it never run in parallel.
func SwapRecordCheck(check func(r *model.Record) (int, error)) (restore func()) {
	original := checkRecord
	checkRecord = check
	return func() { checkRecord = original }
}
