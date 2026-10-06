package importer

import (
	"archive/tar"
	"bytes"
	"context"
	"errors"
	"io"
	"strings"
	"testing"
)

// entryCall is one call of the callback of eachEntry.
type entryCall struct {
	index   int
	name    string
	typ     byte
	content string
}

// collectEntries runs eachEntry over archive and returns the calls and the error.
func collectEntries(ctx context.Context, archive []byte) ([]entryCall, error) {
	var calls []entryCall
	err := eachEntry(ctx, bytes.NewReader(archive), func(index int, h *tar.Header, content io.Reader) error {
		data, err := io.ReadAll(content)
		if err != nil {
			return err
		}
		calls = append(calls, entryCall{index, h.Name, h.Typeflag, string(data)})
		return nil
	})
	return calls, err
}

// checkEntryCalls compares the calls of eachEntry with want.
func checkEntryCalls(t *testing.T, got, want []entryCall) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("eachEntry() made %d calls, want %d: %+v", len(got), len(want), got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Errorf("call %d = %+v, want %+v", i, got[i], want[i])
		}
	}
}

func TestEachEntry_CallsFnForEveryHeader(t *testing.T) {
	archive := buildTar(t,
		tarEntry{name: "global", typ: tar.TypeXGlobalHeader, global: map[string]string{"comment": "x"}},
		tarEntry{name: "d/", typ: tar.TypeDir},
		tarEntry{name: "d/a.log", content: "alpha\n"},
		tarEntry{name: "d/link", typ: tar.TypeSymlink, link: "a.log"},
		tarEntry{name: "d/long-" + strings.Repeat("n", 200), content: "pax long name\n"},
		tarEntry{name: "empty"},
	)

	got, err := collectEntries(context.Background(), archive)

	if err != nil {
		t.Fatalf("eachEntry() error = %v, want nil", err)
	}
	checkEntryCalls(t, got, []entryCall{
		{0, "global", tar.TypeXGlobalHeader, ""},
		{1, "d/", tar.TypeDir, ""},
		{2, "d/a.log", tar.TypeReg, "alpha\n"},
		{3, "d/link", tar.TypeSymlink, ""},
		{4, "d/long-" + strings.Repeat("n", 200), tar.TypeReg, "pax long name\n"},
		{5, "empty", tar.TypeReg, ""},
	})
}

func TestEachEntry_EmptyArchives(t *testing.T) {
	for name, archive := range map[string][]byte{
		"only the trailer": buildTar(t),
		"no bytes":         nil,
	} {
		t.Run(name, func(t *testing.T) {
			got, err := collectEntries(context.Background(), archive)

			if err != nil || len(got) != 0 {
				t.Errorf("eachEntry() = %+v, %v, want no calls and nil", got, err)
			}
		})
	}
}

func TestEachEntry_InsecureNamesAreNormalHeaders(t *testing.T) {
	archive := buildTar(t,
		tarEntry{name: "../x", content: "one"},
		tarEntry{name: "/abs", content: "two"},
		tarEntry{name: "ok", content: "three"},
	)
	want := []entryCall{
		{0, "../x", tar.TypeReg, "one"},
		{1, "/abs", tar.TypeReg, "two"},
		{2, "ok", tar.TypeReg, "three"},
	}
	t.Run("default GODEBUG", func(t *testing.T) {
		got, err := collectEntries(context.Background(), archive)

		if err != nil {
			t.Fatalf("eachEntry() error = %v, want nil", err)
		}
		checkEntryCalls(t, got, want)
	})
	t.Run("GODEBUG=tarinsecurepath=0", func(t *testing.T) {
		t.Setenv("GODEBUG", "tarinsecurepath=0")
		if _, err := tar.NewReader(bytes.NewReader(archive)).Next(); !errors.Is(err, tar.ErrInsecurePath) {
			t.Fatalf("tar.Reader.Next() error = %v, want tar.ErrInsecurePath (precondition of the test)", err)
		}

		got, err := collectEntries(context.Background(), archive)

		if err != nil {
			t.Fatalf("eachEntry() error = %v, want nil (names are labels only)", err)
		}
		checkEntryCalls(t, got, want)
	})
}

// guardedReader serves data up to limit bytes and fails every read that asks for a byte beyond it.
type guardedReader struct {
	data     []byte
	limit    int
	pos      int
	exceeded bool
}

func (g *guardedReader) Read(p []byte) (int, error) {
	if g.pos >= g.limit {
		g.exceeded = true
		return 0, errors.New("read beyond the end of the header")
	}
	n := copy(p, g.data[g.pos:g.limit])
	g.pos += n
	return n, nil
}

// archiveWithHeaderEnd returns the archive of the entries and the offset at which the header of entry k ends: the
// writer emits a header as soon as it is written, so the offset after WriteHeader is the end of that header.
func archiveWithHeaderEnd(t *testing.T, entries []tarEntry, k int) ([]byte, int) {
	t.Helper()
	var buf bytes.Buffer
	w := tar.NewWriter(&buf)
	end := 0
	for i, e := range entries {
		h := &tar.Header{Name: e.name, Mode: 0o644, Size: int64(len(e.content)), ModTime: modA, Typeflag: tar.TypeReg}
		if err := w.WriteHeader(h); err != nil {
			t.Fatal(err)
		}
		if i == k {
			end = buf.Len()
		}
		if _, err := w.Write([]byte(e.content)); err != nil {
			t.Fatal(err)
		}
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes(), end
}

func TestEachEntry_StopsWithoutReadingPastTheHeaderOfTheFailingEntry(t *testing.T) {
	entries := []tarEntry{
		{name: "first", content: strings.Repeat("a", 1000)},
		{name: "second", content: strings.Repeat("b", 1000)},
		{name: "third", content: strings.Repeat("c", 1000)},
	}
	stop := errors.New("stop at entry")
	for k := range entries {
		t.Run(entries[k].name, func(t *testing.T) {
			data, limit := archiveWithHeaderEnd(t, entries, k)
			r := &guardedReader{data: data, limit: limit}
			calls := 0

			err := eachEntry(context.Background(), r, func(index int, _ *tar.Header, _ io.Reader) error {
				calls++
				if index == k {
					return stop
				}
				return nil
			})

			if !errors.Is(err, stop) {
				t.Errorf("eachEntry() error = %v, want the callback's error", err)
			}
			if r.exceeded {
				t.Errorf("eachEntry() read the stream beyond the end of header %d (offset %d), want it to stop there", k, limit)
			}
			if calls != k+1 {
				t.Errorf("callback calls = %d, want %d (none after the failing entry)", calls, k+1)
			}
		})
	}
}

func TestEachEntry_ContextIsCheckedBetweenEntries(t *testing.T) {
	archive := buildTar(t, tarEntry{name: "a", content: "1"}, tarEntry{name: "b", content: "2"}, tarEntry{name: "c", content: "3"})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	calls := 0

	err := eachEntry(ctx, bytes.NewReader(archive), func(int, *tar.Header, io.Reader) error {
		calls++
		cancel()
		return nil
	})

	if !errors.Is(err, context.Canceled) {
		t.Errorf("eachEntry() error = %v, want context.Canceled", err)
	}
	if calls != 1 {
		t.Errorf("callback calls = %d, want 1 (the context is checked before the next entry)", calls)
	}
}

func TestEachEntry_ReaderErrors(t *testing.T) {
	good := buildTar(t, tarEntry{name: "one", content: "alpha\n"}, tarEntry{name: "two", content: "beta\n"})
	big := buildTar(t, tarEntry{name: "one", content: strings.Repeat("a", 1000)})
	tests := []struct {
		name      string
		archive   []byte
		wantCalls int
	}{
		{"corrupt header after two entries", append(append([]byte(nil), good[:len(good)-1024]...), bytes.Repeat([]byte("x"), 512)...), 2},
		{"archive cut inside a header", good[:512+512+100], 1},
		{"archive cut inside the content of an entry", big[:512+100], 1},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			err := eachEntry(context.Background(), bytes.NewReader(tc.archive), func(_ int, _ *tar.Header, content io.Reader) error {
				calls++
				_, err := io.Copy(io.Discard, content)
				return err
			})

			if err == nil {
				t.Error("eachEntry() error = nil, want the tar error")
			}
			if calls != tc.wantCalls {
				t.Errorf("callback calls = %d, want %d (the entries before the error are handled)", calls, tc.wantCalls)
			}
		})
	}
}

func TestEachEntry_ReadErrorOfTheUnderlyingReader(t *testing.T) {
	boom := errors.New("disk failure")
	good := buildTar(t, tarEntry{name: "one", content: "alpha\n"})
	r := io.MultiReader(bytes.NewReader(good[:512+512]), &failingReader{err: boom})

	err := eachEntry(context.Background(), r, func(int, *tar.Header, io.Reader) error { return nil })

	if !errors.Is(err, boom) {
		t.Errorf("eachEntry() error = %v, want it to wrap %v", err, boom)
	}
}

// failingReader fails every read with err.
type failingReader struct{ err error }

func (f *failingReader) Read([]byte) (int, error) { return 0, f.err }
