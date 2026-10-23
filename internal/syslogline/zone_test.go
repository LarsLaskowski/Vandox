package syslogline_test

import (
	"io"
	"io/fs"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// endlessFile is a file of endless text that counts what is read from it.
type endlessFile struct{ read *int64 }

func (f endlessFile) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = 'x'
	}
	*f.read += int64(len(p))
	return len(p), nil
}

func (f endlessFile) Stat() (fs.FileInfo, error) { return nil, io.ErrUnexpectedEOF }
func (f endlessFile) Close() error               { return nil }

type endlessFS struct{ read *int64 }

func (e endlessFS) Open(string) (fs.File, error) { return endlessFile(e), nil }

func TestLoadZone_RefusesWhatIsNotAZoneFile(t *testing.T) {
	good := fixedZone(7200)
	loc, err := syslogline.LoadZone(fstest.MapFS{"localtime": {Data: good}})
	if err != nil || loc == nil {
		t.Fatalf("LoadZone() of a zone file = %v, %v, want a location and no error", loc, err)
	}
	if got := time.Date(2026, time.October, 23, 7, 13, 2, 0, loc).UTC(); !got.Equal(utc(2026, time.October, 23, 5, 13, 2)) {
		t.Errorf("07:13:02 in the loaded zone is %v in UTC, want 05:13:02 (the zone of the file, +02:00)", got)
	}

	tests := []struct {
		name string
		fsys fs.FS
	}{
		{"no file", fstest.MapFS{}},
		{"an empty file", fstest.MapFS{"localtime": {Data: nil}}},
		{"text", fstest.MapFS{"localtime": {Data: []byte("Europe/Berlin\n")}}},
		{"a zone file cut inside its header", fstest.MapFS{"localtime": {Data: good[:30]}}},
		{"a zone file cut inside its data", fstest.MapFS{"localtime": {Data: good[:len(good)-3]}}},
		{"a directory", fstest.MapFS{"localtime/x": {Data: good}}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := syslogline.LoadZone(tt.fsys)

			if err == nil || got != nil {
				t.Errorf("LoadZone() = %v, %v, want a nil location and an error (never UTC as a fallback)", got, err)
			}
		})
	}

	t.Run("a file of endless text is read for at most 64 KiB", func(t *testing.T) {
		var read int64

		got, err := syslogline.LoadZone(endlessFS{read: &read})

		if err == nil || got != nil {
			t.Errorf("LoadZone() = %v, %v, want a nil location and an error", got, err)
		}
		if read > 64<<10+1 {
			t.Errorf("LoadZone() read %d bytes, want at most 64 KiB", read)
		}
	})
}

func TestLoadZone_ReadsAZoneWithDaylightSavingTime(t *testing.T) {
	loc, err := syslogline.LoadZone(fstest.MapFS{"localtime": {Data: centralEurope()}})
	if err != nil {
		t.Fatalf("LoadZone() error = %v, want nil", err)
	}

	winter := time.Date(2026, time.January, 20, 7, 0, 0, 0, loc).UTC()
	summer := time.Date(2026, time.July, 20, 7, 0, 0, 0, loc).UTC()

	if !winter.Equal(utc(2026, time.January, 20, 6, 0, 0)) || !summer.Equal(utc(2026, time.July, 20, 5, 0, 0)) {
		t.Errorf("07:00 local is %v in winter and %v in summer, want 06:00 and 05:00 UTC", winter, summer)
	}
}
