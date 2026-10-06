package store

import (
	"context"
	"errors"
	"math"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// importStart returns a valid ImportFileStart whose content hash starts with the byte tag.
func importStart(tag byte) ImportFileStart {
	var sum [32]byte
	sum[0] = tag
	return ImportFileStart{
		SHA256:     sum,
		Size:       4096,
		Name:       "var/log/syslog.1",
		FileName:   "var/log/syslog.1",
		ModTime:    baseTime.Add(-time.Hour),
		SourceType: "syslog",
		StartedAt:  baseTime,
	}
}

// sameImportFile reports whether a and b hold the same values; times are compared as instants.
func sameImportFile(a, b ImportFile) bool {
	return a.ID == b.ID && a.SHA256 == b.SHA256 && a.Size == b.Size && a.Name == b.Name && a.FileName == b.FileName &&
		a.ModTime.Equal(b.ModTime) && a.SourceType == b.SourceType && a.Records == b.Records && a.Complete == b.Complete &&
		a.StartedAt.Equal(b.StartedAt) && a.CompletedAt.Equal(b.CompletedAt)
}

// importLogRecord returns a valid log line record of origin import captured i seconds after baseTime.
func importLogRecord(message string, i int) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginImport, Source: "syslog", CapturedAt: baseTime.Add(time.Duration(i) * time.Second)},
		Data: &model.LogLine{Log: "syslog", Message: message},
	}
}

// beginImport starts the import of f and fails the test on an error.
func beginImport(t *testing.T, s *Store, f ImportFileStart) ImportFile {
	t.Helper()
	got, err := s.BeginImport(context.Background(), f)
	if err != nil {
		t.Fatalf("BeginImport() error = %v, want nil", err)
	}
	return got
}

func TestStore_BeginImport_NewFile(t *testing.T) {
	s := openStore(t, t.TempDir())
	start := importStart(1)

	got := beginImport(t, s, start)

	if got.ID <= 0 {
		t.Errorf("BeginImport() ID = %d, want a positive ID", got.ID)
	}
	want := ImportFile{
		ID: got.ID, SHA256: start.SHA256, Size: start.Size, Name: start.Name, FileName: start.FileName,
		ModTime: start.ModTime, SourceType: start.SourceType, Records: 0, Complete: false, StartedAt: start.StartedAt,
	}
	if !sameImportFile(got, want) {
		t.Errorf("BeginImport() = %+v, want %+v", got, want)
	}
	if !got.CompletedAt.IsZero() {
		t.Errorf("BeginImport() CompletedAt = %v, want the zero time while not complete", got.CompletedAt)
	}
	if n := countRows(t, s, "import_files"); n != 1 {
		t.Errorf("rows in import_files = %d, want 1", n)
	}
}

func TestStore_BeginImport_DistinctContentsGetDistinctFiles(t *testing.T) {
	s := openStore(t, t.TempDir())

	a, b := beginImport(t, s, importStart(1)), beginImport(t, s, importStart(2))

	if a.ID == b.ID {
		t.Errorf("BeginImport() IDs of two contents = %d and %d, want different IDs", a.ID, b.ID)
	}
	if n := countRows(t, s, "import_files"); n != 2 {
		t.Errorf("rows in import_files = %d, want 2", n)
	}
}

func TestStore_BeginImport_KnownContentIsReturnedUnchanged(t *testing.T) {
	s := openStore(t, t.TempDir())
	first := beginImport(t, s, importStart(7))
	other := importStart(7)
	other.Size = 1
	other.Name = "elsewhere/syslog.2.gz"
	other.FileName = "elsewhere/syslog.2"
	other.ModTime = baseTime.Add(48 * time.Hour)
	other.SourceType = "mail"
	other.StartedAt = baseTime.Add(72 * time.Hour)

	second := beginImport(t, s, other)

	if !sameImportFile(second, first) {
		t.Errorf("BeginImport() of a known content = %+v, want the stored file unchanged %+v", second, first)
	}
	if n := countRows(t, s, "import_files"); n != 1 {
		t.Errorf("rows in import_files = %d, want 1", n)
	}
}

func TestStore_BeginImport_KnownContentKeepsProgressAndCompletion(t *testing.T) {
	s := openStore(t, t.TempDir())
	file := beginImport(t, s, importStart(3))
	received := baseTime.Add(2 * time.Hour).Add(123 * time.Nanosecond)
	writeOK(t, s, Batch{
		ReceivedAt: received,
		Records:    []model.Record{importLogRecord("a", 1), importLogRecord("b", 2)},
		Import:     &ImportStep{FileID: file.ID, Done: 0, Complete: true},
	})
	again := importStart(3)
	again.Name = "renamed"
	again.StartedAt = baseTime.Add(time.Hour)

	got := beginImport(t, s, again)

	want := file
	want.Records = 2
	want.Complete = true
	want.CompletedAt = received
	if !sameImportFile(got, want) {
		t.Errorf("BeginImport() of a completed content = %+v, want %+v", got, want)
	}
}

func TestStore_BeginImport_FileNameIsStoredByteForByte(t *testing.T) {
	tests := []struct {
		name     string
		fileName string
	}{
		{"invalid UTF-8", "var/log/a\xff\xfeb"},
		{"control characters", "var/log/a\x00b\nc\x1b[2Jd\u009be\u202ef\x7f"},
		{"valid non-ASCII", "var/log/ü€\U0001F600.log"},
		{"longest allowed", strings.Repeat("x", MaxImportNameBytes)},
		{"longest allowed ending in a partial sequence", strings.Repeat("x", MaxImportNameBytes-1) + "\xc3"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			start := importStart(1)
			start.FileName = tc.fileName

			first := beginImport(t, s, start)
			second := beginImport(t, s, start)

			if first.FileName != tc.fileName {
				t.Errorf("BeginImport() FileName = %q, want %q", first.FileName, tc.fileName)
			}
			if second.FileName != tc.fileName {
				t.Errorf("BeginImport() of the known content FileName = %q, want %q", second.FileName, tc.fileName)
			}
		})
	}
}

func TestStore_BeginImport_ModTime(t *testing.T) {
	plus1 := time.FixedZone("plus1", 3600)
	tests := []struct {
		name string
		in   time.Time
		want time.Time // the zero time: unknown
	}{
		{"UTC with nanoseconds", time.Date(2026, 10, 6, 12, 0, 0, 123456789, time.UTC), time.Date(2026, 10, 6, 12, 0, 0, 123456789, time.UTC)},
		{"converted to UTC", time.Date(2026, 10, 6, 13, 0, 0, 5, plus1), time.Date(2026, 10, 6, 12, 0, 0, 5, time.UTC)},
		{"zero is unknown", time.Time{}, time.Time{}},
		{"before 1678 is unknown", time.Date(1500, 1, 1, 0, 0, 0, 0, time.UTC), time.Time{}},
		{"after 2262 is unknown", time.Date(2300, 1, 1, 0, 0, 0, 0, time.UTC), time.Time{}},
		{"first storable instant", time.Unix(0, math.MinInt64).UTC(), time.Unix(0, math.MinInt64).UTC()},
		{"last storable instant", time.Unix(0, math.MaxInt64).UTC(), time.Unix(0, math.MaxInt64).UTC()},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			start := importStart(1)
			start.ModTime = tc.in

			first := beginImport(t, s, start)
			second := beginImport(t, s, start)

			for label, got := range map[string]time.Time{"new": first.ModTime, "known": second.ModTime} {
				if !got.Equal(tc.want) || got.IsZero() != tc.want.IsZero() {
					t.Errorf("BeginImport() (%s) ModTime = %v, want %v", label, got, tc.want)
				}
				if _, off := got.Zone(); off != 0 {
					t.Errorf("BeginImport() (%s) ModTime = %v, want UTC", label, got)
				}
			}
		})
	}
}

func TestStore_BeginImport_StartedAtKeepsNanoseconds(t *testing.T) {
	s := openStore(t, t.TempDir())
	start := importStart(1)
	start.StartedAt = time.Date(2026, 10, 6, 12, 0, 0, 987654321, time.UTC)

	got := beginImport(t, s, start)

	if !got.StartedAt.Equal(start.StartedAt) {
		t.Errorf("BeginImport() StartedAt = %v, want %v", got.StartedAt, start.StartedAt)
	}
	if _, off := got.StartedAt.Zone(); off != 0 {
		t.Errorf("BeginImport() StartedAt = %v, want UTC", got.StartedAt)
	}
}

func TestStore_BeginImport_Refuses(t *testing.T) {
	tests := []struct {
		name   string
		change func(f *ImportFileStart)
	}{
		{"zero StartedAt", func(f *ImportFileStart) { f.StartedAt = time.Time{} }},
		{"non-UTC StartedAt", func(f *ImportFileStart) { f.StartedAt = baseTime.In(time.FixedZone("plus1", 3600)) }},
		{"negative Size", func(f *ImportFileStart) { f.Size = -1 }},
		{"empty SourceType", func(f *ImportFileStart) { f.SourceType = "" }},
		{"upper case SourceType", func(f *ImportFileStart) { f.SourceType = "Syslog" }},
		{"SourceType with a slash", func(f *ImportFileStart) { f.SourceType = "sys/log" }},
		{"FileName one byte too long", func(f *ImportFileStart) { f.FileName = strings.Repeat("x", MaxImportNameBytes+1) }},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			start := importStart(1)
			tc.change(&start)

			got, err := s.BeginImport(context.Background(), start)

			if !errors.Is(err, ErrInvalidImport) {
				t.Errorf("BeginImport() error = %v, want an error wrapping ErrInvalidImport", err)
			}
			if !sameImportFile(got, ImportFile{}) {
				t.Errorf("BeginImport() = %+v with the error, want the zero ImportFile", got)
			}
			if n := countRows(t, s, "import_files"); n != 0 {
				t.Errorf("rows in import_files after the refusal = %d, want 0", n)
			}
		})
	}
}

func TestStore_BeginImport_AcceptsBoundaryValues(t *testing.T) {
	s := openStore(t, t.TempDir())
	start := importStart(1)
	start.Size = 0
	start.SourceType = "a" + strings.Repeat("b", 63)
	start.FileName = strings.Repeat("x", MaxImportNameBytes)

	got, err := s.BeginImport(context.Background(), start)

	if err != nil {
		t.Fatalf("BeginImport() error = %v, want nil", err)
	}
	if got.Size != 0 || got.SourceType != start.SourceType || got.FileName != start.FileName {
		t.Errorf("BeginImport() = %+v, want size 0, the 64-byte source type and the 1024-byte file name", got)
	}
}

func TestStore_BeginImport_Name(t *testing.T) {
	tests := []struct {
		name string
		in   string
		want string
	}{
		{"unchanged", "var/log/syslog", "var/log/syslog"},
		{"valid non-ASCII unchanged", "var/ü€\U0001F600", "var/ü€\U0001F600"},
		{"invalid UTF-8 replaced", "a\xffb", "a�b"},
		{"truncated sequence replaced", "a\xc3", "a�"},
		{"longest allowed", strings.Repeat("x", MaxImportNameBytes), strings.Repeat("x", MaxImportNameBytes)},
		{"cut to the limit", strings.Repeat("x", MaxImportNameBytes+476), strings.Repeat("x", MaxImportNameBytes)},
		{"cut before a sequence that would straddle the limit", strings.Repeat("x", MaxImportNameBytes-1) + "étail", strings.Repeat("x", MaxImportNameBytes-1)},
		{"cut before a 4-byte sequence straddling the limit", strings.Repeat("x", MaxImportNameBytes-2) + "\U0001F600tail", strings.Repeat("x", MaxImportNameBytes-2)},
		{"replacement character that would straddle the limit", strings.Repeat("x", MaxImportNameBytes-1) + "\xff", strings.Repeat("x", MaxImportNameBytes-1)},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			s := openStore(t, t.TempDir())
			start := importStart(1)
			start.Name = tc.in

			first := beginImport(t, s, start)
			second := beginImport(t, s, start)

			for label, got := range map[string]string{"new": first.Name, "known": second.Name} {
				if got != tc.want {
					t.Errorf("BeginImport() (%s) Name = %q (%d bytes), want %q (%d bytes)", label, got, len(got), tc.want, len(tc.want))
				}
				if !utf8.ValidString(got) {
					t.Errorf("BeginImport() (%s) Name is not valid UTF-8", label)
				}
			}
		})
	}
}

func TestStore_BeginImport_ContextAndClosedStore(t *testing.T) {
	t.Run("cancelled context", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		ctx, cancel := context.WithCancel(context.Background())
		cancel()

		if _, err := s.BeginImport(ctx, importStart(1)); err == nil {
			t.Error("BeginImport(cancelled ctx) error = nil, want an error")
		}
		if n := countRows(t, s, "import_files"); n != 0 {
			t.Errorf("rows in import_files = %d, want 0", n)
		}
	})
	t.Run("closed store", func(t *testing.T) {
		s := openStore(t, t.TempDir())
		if err := s.Close(); err != nil {
			t.Fatalf("Close() error = %v, want nil", err)
		}

		if _, err := s.BeginImport(context.Background(), importStart(1)); err == nil {
			t.Error("BeginImport() on a closed store error = nil, want an error")
		}
	})
}

func TestCheckImportRecord(t *testing.T) {
	minTime := time.Unix(0, math.MinInt64).UTC()
	maxTime := time.Unix(0, math.MaxInt64).UTC()
	valid := func() model.Record { return importLogRecord("a line", 1) }
	tests := []struct {
		name   string
		change func(r *model.Record)
		ok     bool
	}{
		{"valid log line", func(*model.Record) {}, true},
		{"valid metric", func(r *model.Record) { r.Data = &model.MetricPoint{Name: "cpu.load", Value: 1} }, true},
		{"earliest storable instant", func(r *model.Record) { r.CapturedAt = minTime }, true},
		{"latest storable instant", func(r *model.Record) { r.CapturedAt = maxTime }, true},
		{"origin agent", func(r *model.Record) { r.Origin, r.Seq = model.OriginAgent, 1 }, false},
		{"origin backend", func(r *model.Record) { r.Origin = model.OriginBackend }, false},
		{"unknown origin", func(r *model.Record) { r.Origin = "other" }, false},
		{"empty source", func(r *model.Record) { r.Source = "" }, false},
		{"no payload", func(r *model.Record) { r.Data = nil }, false},
		{"invalid payload", func(r *model.Record) { r.Data = &model.LogLine{Log: "", Message: "x"} }, false},
		{"message over the limit", func(r *model.Record) {
			r.Data = &model.LogLine{Log: "syslog", Message: strings.Repeat("x", model.MaxTextBytes+1)}
		}, false},
		{"zero CapturedAt", func(r *model.Record) { r.CapturedAt = time.Time{} }, false},
		{"non-UTC CapturedAt", func(r *model.Record) { r.CapturedAt = baseTime.In(time.FixedZone("plus1", 3600)) }, false},
		{"CapturedAt before the storable range", func(r *model.Record) { r.CapturedAt = time.Date(1500, 1, 1, 0, 0, 0, 0, time.UTC) }, false},
		{"CapturedAt after the storable range", func(r *model.Record) { r.CapturedAt = time.Date(2300, 1, 1, 0, 0, 0, 0, time.UTC) }, false},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			r := valid()
			tc.change(&r)

			err := CheckImportRecord(&r)

			if tc.ok && err != nil {
				t.Errorf("CheckImportRecord() = %v, want nil", err)
			}
			if !tc.ok && err == nil {
				t.Error("CheckImportRecord() = nil, want an error")
			}
		})
	}
}

func TestCheckImportRecord_AgreesWithWriteBatch(t *testing.T) {
	s := openStore(t, t.TempDir())
	file := beginImport(t, s, importStart(1))
	r := importLogRecord("accepted by both", 1)

	if err := CheckImportRecord(&r); err != nil {
		t.Fatalf("CheckImportRecord() = %v, want nil", err)
	}

	res := writeOK(t, s, Batch{ReceivedAt: baseTime, Records: []model.Record{r}, Import: &ImportStep{FileID: file.ID}})
	if res != (WriteResult{Stored: 1}) {
		t.Errorf("WriteBatch() of a record CheckImportRecord accepted = %+v, want {Stored: 1}", res)
	}
}
