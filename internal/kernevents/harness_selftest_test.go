package kernevents

import (
	"errors"
	"io"
	"io/fs"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/journal"
)

// openJournalFile opens a file of the journal root as the reader does: through the machine directory, as a ReaderAt of its size.
func openJournalFile(t *testing.T, root fs.FS, name string, setAside bool) *journal.File {
	t.Helper()
	dir, err := fs.Sub(root, machineID)
	if err != nil {
		t.Fatalf("fs.Sub() error = %v, want nil", err)
	}
	f, err := dir.Open(name)
	if err != nil {
		t.Fatalf("Open(%q) error = %v, want nil", name, err)
	}
	t.Cleanup(func() { _ = f.Close() })
	ra, ok := f.(io.ReaderAt)
	if !ok {
		t.Fatalf("Open(%q) gave %T, want a file that is an io.ReaderAt", name, f)
	}
	st, err := f.Stat()
	if err != nil {
		t.Fatalf("Stat(%q) error = %v, want nil", name, err)
	}
	jf, err := journal.Open(ra, st.Size(), setAside)
	if err != nil {
		t.Fatalf("journal.Open(%q) error = %v, want nil", name, err)
	}
	return jf
}

// requireKernelList checks that a journal file of the root has the seqnum ID and holds a first kernel entry.
func requireKernelList(t *testing.T, root fs.FS, name, wantID string, setAside bool) {
	t.Helper()
	jf := openJournalFile(t, root, name, setAside)
	if h := jf.Header(); h.SeqnumID != mustID(t, wantID) {
		t.Errorf("%s: seqnum ID = %x, want %s", name, h.SeqnumID, wantID)
	}
	d, found, err := jf.Lookup([]byte("_TRANSPORT=kernel"))
	if err != nil || !found {
		t.Fatalf("%s: Lookup(_TRANSPORT=kernel) = %v, %v, want found", name, found, err)
	}
	cur, err := jf.Seek(d, 0)
	if err != nil {
		t.Fatalf("%s: Seek() error = %v, want nil", name, err)
	}
	var en journal.Entry
	if ok, err := jf.Next(&cur, []string{"MESSAGE"}, &en); err != nil || !ok {
		t.Errorf("%s: Next() = %v, %v, want an entry", name, ok, err)
	}
}

func TestHarness_JournalFSIsReadByTheReader(t *testing.T) {
	e := newEnv(t)
	var a, b, s seqCounter
	archived := e.j.add(t, kindArchived, seqIDA, []entry{a.line(bootB, 1), a.kill(bootB, 2, 1, "p", lagUS)})
	online := e.j.add(t, kindOnline, seqIDB, []entry{b.line(bootB, 3)})
	setAside := e.j.add(t, kindSetAside, seqIDC, []entry{s.line(bootB, 4)})

	dir, err := fs.Sub(e.j, machineID)
	if err != nil {
		t.Fatalf("fs.Sub() error = %v, want nil", err)
	}
	names, err := journal.ListFiles(dir)
	if err != nil || len(names) != 3 {
		t.Errorf("ListFiles() = %v, %v, want the 3 files %q %q %q", names, err, archived, online, setAside)
	}
	if id, err := journal.MachineDir(e.etc); err != nil || id != machineID {
		t.Errorf("MachineDir() = %q, %v, want %q", id, err, machineID)
	}
	requireKernelList(t, e.j, archived, seqIDA, false)
	requireKernelList(t, e.j, online, seqIDB, false)
	requireKernelList(t, e.j, setAside, seqIDC, true)
	if !openJournalFile(t, e.j, online, false).Header().Online || openJournalFile(t, e.j, archived, false).Header().Online {
		t.Errorf("the online file is not flagged online or the archived file is")
	}
	if e.j.reads.Load() == 0 {
		t.Errorf("reads counted = 0, want the reads of the reader counted")
	}
}

func TestHarness_JournalFSHooksAndFaults(t *testing.T) {
	e := newEnv(t)
	var a seqCounter
	name := e.j.add(t, kindArchived, seqIDA, []entry{a.line(bootB, 1)})
	boom := errors.New("boom")

	e.j.setHook(func(op, _ string, _ int64) error {
		if op == "read" {
			return boom
		}
		return nil
	})
	dir, _ := fs.Sub(e.j, machineID)
	f, err := dir.Open(name)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil (the hook fails reads only)", err)
	}
	if _, err := f.(io.ReaderAt).ReadAt(make([]byte, 8), 0); !errors.Is(err, boom) {
		t.Errorf("ReadAt() error = %v, want the hook's error", err)
	}
	_ = f.Close()
	e.j.setHook(func(_, _ string, _ int64) error { return boom })
	if _, err := dir.Open(name); !errors.Is(err, boom) {
		t.Errorf("Open() error = %v, want the hook's error", err)
	}
	e.j.setHook(nil)

	e.j.setDenied(fs.ErrPermission)
	if _, err := dir.Open("."); !errors.Is(err, fs.ErrPermission) {
		t.Errorf("Open(.) of a denied directory error = %v, want permission denied", err)
	}
	e.j.setDenied(nil)
	e.j.setNoDir(true)
	if _, err := dir.Open("."); !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("Open(.) of a missing directory error = %v, want not exist", err)
	}
	e.j.setNoDir(false)
	if _, err := dir.Open(name); err != nil {
		t.Errorf("Open() after the faults are lifted: error = %v, want nil", err)
	}
}

func TestHarness_FixtureIsReadByTheReader(t *testing.T) {
	j := newJournalFS()
	j.loadFixture(t)
	dir, _ := fs.Sub(j, machineID)
	names, err := journal.ListFiles(dir)
	if err != nil || len(names) != 3 {
		t.Fatalf("ListFiles() = %v, %v, want the 3 fixture files", names, err)
	}
	for _, n := range names {
		jf := openJournalFile(t, j, n, n[len(n)-1] == '~')
		if _, found, err := jf.Lookup([]byte("_TRANSPORT=kernel")); err != nil || !found {
			t.Errorf("%s: Lookup(_TRANSPORT=kernel) = %v, %v, want found", n, found, err)
		}
	}
}
