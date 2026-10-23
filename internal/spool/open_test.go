package spool

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"runtime/debug"
	"runtime/metrics"
	"strings"
	"sync/atomic"
	"syscall"
	"testing"
	"time"
)

const dirPrefix = "spool: directory: "

// dirCase is a spool directory the spool refuses to open and the exact error it answers with.
type dirCase struct {
	name string
	dir  func(t *testing.T, root string) string
	want string
}

func dirMissing(_ *testing.T, root string) string { return filepath.Join(root, "missing") }

func dirIsFile(t *testing.T, root string) string {
	t.Helper()

	path := filepath.Join(root, "file")
	needNoErr(t, "WriteFile", os.WriteFile(path, []byte("x"), 0o600))

	return path
}

func dirIsLink(t *testing.T, root string) string {
	t.Helper()

	link := filepath.Join(root, "link")
	needNoErr(t, "Symlink", os.Symlink(mkdir(t, root, "real"), link))

	return link
}

func dirIsDanglingLink(t *testing.T, root string) string {
	t.Helper()

	link := filepath.Join(root, "dangling")
	needNoErr(t, "Symlink", os.Symlink(filepath.Join(root, "nowhere"), link))

	return link
}

func dirRelative(*testing.T, string) string { return "relative/spool" }

func dirEmpty(*testing.T, string) string { return "" }

func dirTrailingSlash(t *testing.T, root string) string { return mkdir(t, root, "slash") + "/" }

func dirDotElement(t *testing.T, root string) string { return mkdir(t, root, "dot") + "/." }

func dirDoubleSlash(t *testing.T, root string) string {
	return root + "//" + filepath.Base(mkdir(t, root, "double"))
}

func dirParentElement(t *testing.T, root string) string {
	return mkdir(t, root, "parent") + "/../" + filepath.Base(mkdir(t, root, "other"))
}

func directoryCases() []dirCase {
	const unclean = dirPrefix + "not an absolute, clean path"

	return []dirCase{
		{"missing directory", dirMissing, dirPrefix + "no such file or directory"},
		{"a file in place of the directory", dirIsFile, dirPrefix + "not a directory"},
		{"a symbolic link to a directory", dirIsLink, dirPrefix + "symbolic link"},
		{"a dangling symbolic link", dirIsDanglingLink, dirPrefix + "symbolic link"},
		{"a relative path", dirRelative, unclean},
		{"an empty path", dirEmpty, unclean},
		{"a trailing slash", dirTrailingSlash, unclean},
		{"a dot element", dirDotElement, unclean},
		{"a double slash", dirDoubleSlash, unclean},
		{"a parent element", dirParentElement, unclean},
	}
}

func runDirectoryCase(t *testing.T, tc dirCase) {
	t.Helper()

	r := newRig(t)
	root := t.TempDir()
	r.opts.Dir = tc.dir(t, root)

	sp, err := r.tryOpen()
	need(t, err != nil, "Open(%s) = nil, want the error %q", tc.name, tc.want)
	_ = sp
	expect(t, err.Error() == tc.want, "Open(%s) error = %q, want %q", tc.name, err.Error(), tc.want)
	requireNoPath(t, "error", err.Error(), root)
	if tc.name == "missing directory" {
		_, statErr := os.Stat(r.opts.Dir)
		expect(t, os.IsNotExist(statErr), "Stat(missing directory) = %v, want it still missing (the agent never creates the directory)", statErr)
	}
	requireNoSpoolFilesBelow(t, root)
}

// requireNoSpoolFilesBelow checks that no spool file was created in root or in a directory directly below it.
func requireNoSpoolFilesBelow(t *testing.T, root string) {
	t.Helper()

	entries, _ := os.ReadDir(root)
	for _, e := range entries {
		expect(t, !isSpoolName(e.Name()), "Open left the spool file %s next to the refused directory", e.Name())
		if !e.IsDir() {
			continue
		}
		inner, _ := os.ReadDir(filepath.Join(root, e.Name()))
		for _, f := range inner {
			expect(t, !isSpoolName(f.Name()), "Open left the spool file %s in the directory %s", f.Name(), e.Name())
		}
	}
}

func linkAboveDirectoryFollowed(t *testing.T) {
	t.Helper()

	r := newRig(t)
	root := t.TempDir()
	target := mkdir(t, root, "real")
	inner := mkdir(t, target, "spool")
	needNoErr(t, "Symlink", os.Symlink(target, filepath.Join(root, "top")))
	r.opts.Dir = filepath.Join(root, "top", "spool")
	sp := r.open()
	putTagged(t, sp, epoch, "up", 2, 100)
	expect(t, len(segmentNumbers(t, inner)) == 1, "%d data files in the real directory, want 1", len(segmentNumbers(t, inner)))
}

func staleTemporaryRemoved(t *testing.T) {
	t.Helper()

	r := newRig(t)
	writeSegment(t, r.dir(), metaTmpName, []byte("half written"))
	r.open()
	_, err := os.Lstat(filepath.Join(r.dir(), metaTmpName))
	expect(t, os.IsNotExist(err), "Lstat(%s) = %v, want the stale file removed", metaTmpName, err)
}

func failingWriteRefusesStart(t *testing.T, errno syscall.Errno) {
	t.Helper()

	fault := installFault(t)
	r := newRig(t)
	fault.set(faultFail, errno)

	sp, err := r.tryOpen()
	need(t, err != nil, "Open with failing writes (%v) = nil, want an error", errno)
	_ = sp
	expect(t, strings.HasPrefix(err.Error(), "spool: ") && strings.Contains(err.Error(), errno.Error()), "Open error = %q, want it to start with %q and hold %q", err.Error(), "spool: ", errno.Error())
	requireNoPath(t, "error", err.Error(), r.dir())
}

func TestOpen_DirectoryProblems(t *testing.T) {
	for _, tc := range directoryCases() {
		t.Run(tc.name, func(t *testing.T) { runDirectoryCase(t, tc) })
	}
	t.Run("a link in an element above the directory is followed", linkAboveDirectoryFollowed)
	t.Run("a stale temporary meta file of the agent is removed", staleTemporaryRemoved)
	t.Run("no permission to write", func(t *testing.T) { failingWriteRefusesStart(t, syscall.EACCES) })
	t.Run("read-only file system", func(t *testing.T) { failingWriteRefusesStart(t, syscall.EROFS) })
}

// mkdir creates the directory name under parent and returns its path.
func mkdir(t *testing.T, parent, name string) string {
	t.Helper()

	path := filepath.Join(parent, name)
	needNoErr(t, "Mkdir("+name+")", os.Mkdir(path, 0o700))

	return path
}

func runFirstStart(t *testing.T, withState bool) {
	t.Helper()

	r := newRig(t)
	statePath := filepath.Join(r.dir(), stateName)
	var before stat
	if withState {
		writeSegment(t, r.dir(), stateName, []byte(`{"agent":"state"}`))
		before = statOf(t, statePath)
	}

	sp := r.open()
	created := r.logs.find("spool created")
	need(t, len(created) == 1 && created[0].level == slog.LevelInfo, "log lines %q = %v, want one info line", "spool created", created)
	expect(t, len(warnings(r.logs)) == 0, "first start logged warnings: %v", warnings(r.logs))
	requireNoLeak(t, r)

	putTagged(t, sp, epoch, "first", 2, 100)
	r.logs.reset()
	r.reopen(sp)
	expect(t, r.logs.count("spool created") == 0, "a second start logged spool created %d times, want 0", r.logs.count("spool created"))
	expect(t, len(warnings(r.logs)) == 0, "second start logged warnings: %v", warnings(r.logs))
	if withState {
		requireUnchanged(t, "agent-state.json", statePath, before)
	}
}

func TestOpen_FirstStart(t *testing.T) {
	t.Run("empty directory", func(t *testing.T) { runFirstStart(t, false) })
	t.Run("directory holding only the state file", func(t *testing.T) { runFirstStart(t, true) })
}

// foreignEntry is a file of the directory that is not one of the spool's.
type foreignEntry struct {
	name string
	data string
}

func TestOpen_ForeignEntriesUntouched(t *testing.T) {
	r := newRig(t)
	dir := r.dir()

	entries := []foreignEntry{
		{stateName, `{"agent":"state","positions":[1,2,3]}`},
		{stateName + ".tmp", filler(3 << 20)},
		{"notes.txt", "operator notes"},
		{"segment-" + strings.Repeat("0", 19) + "7" + ".other", "wrong extension"},
		{"segment-" + strings.Repeat("0", 18) + "07" + segmentExt + ".old", "a backup of a data file"},
		{"segment-" + strings.Repeat("0", 19) + segmentExt, "nineteen digits"},
		{"segment-" + strings.Repeat("0", 20) + "1" + segmentExt, "twenty-one digits"},
		{"Segment-" + strings.Repeat("0", 19) + "5" + segmentExt, "capital letter"},
		{"segment-" + strings.Repeat("0", 19) + "x" + segmentExt, "not a number"},
		{metaName + ".bak", "a backup of the meta file"},
		{metaName + "~", "an editor file"},
	}
	for _, e := range entries {
		writeSegment(t, dir, e.name, []byte(e.data))
	}
	sub := mkdir(t, dir, "sub")
	writeSegment(t, sub, segName(1), []byte("a data file name in a subdirectory"))
	if err := os.Symlink("notes.txt", filepath.Join(dir, "link-to-notes")); err != nil {
		t.Fatal(err)
	}
	paths := []string{sub, filepath.Join(sub, segName(1)), filepath.Join(dir, "link-to-notes")}
	for _, e := range entries {
		paths = append(paths, filepath.Join(dir, e.name))
	}
	before := map[string]stat{}
	for _, p := range paths {
		before[p] = statOf(t, p)
	}
	linkTarget, err := os.Readlink(filepath.Join(dir, "link-to-notes"))
	if err != nil {
		t.Fatal(err)
	}

	sp := r.open()
	all := putTagged(t, sp, epoch, "own", 10, 200)
	own, err := sp.SpoolBytes(context.Background())
	if err != nil || own != diskBytes(t, dir) {
		t.Errorf("SpoolBytes() = %d, %v, want %d (only the spool's own files, not the 3 MiB of foreign files)", own, err, diskBytes(t, dir))
	}
	if own > 1<<20 {
		t.Errorf("SpoolBytes() = %d, want a size that counts none of the foreign files", own)
	}
	mustAck(t, sp, all[0].Seq, all[9].Seq)
	r.closeSpool(sp)
	sp = r.open()
	putTagged(t, sp, epoch.Add(time.Hour), "own2", 3, 200)
	mustAck(t, sp, 1, sp.LastSeq())
	r.closeSpool(sp)

	for _, p := range paths {
		requireUnchanged(t, "foreign entry "+filepath.Base(p), p, before[p])
	}
	if got, err := os.Readlink(filepath.Join(dir, "link-to-notes")); err != nil || got != linkTarget {
		t.Errorf("Readlink(link-to-notes) = %q, %v, want %q", got, err, linkTarget)
	}
	if w := warnings(r.logs); len(w) != 0 {
		t.Errorf("foreign entries caused warnings: %v", w)
	}
}

// foreignPerInode is the number of hard links made to one source file; file systems limit the links of one file.
const foreignPerInode = 30_000

// makeForeign creates n entries that are not the spool's in dir: empty files, made as hard links to a few source files
// because creating 100,000 separate files takes seconds on many file systems.
func makeForeign(t *testing.T, dir string, n int) {
	t.Helper()

	var source string
	for i := range n {
		if i%foreignPerInode == 0 {
			source = filepath.Join(dir, fmt.Sprintf("foreign-source-%d.dat", i/foreignPerInode))
			needNoErr(t, "creating a foreign source file", os.WriteFile(source, nil, 0o600))
		}
		needNoErr(t, fmt.Sprintf("linking foreign entry %d", i), os.Link(source, filepath.Join(dir, fmt.Sprintf("foreign-entry-%06d.dat", i))))
	}
}

// heapObjects returns the bytes of live and not yet collected objects on the heap.
func heapObjects() uint64 {
	sample := []metrics.Sample{{Name: "/memory/classes/heap/objects:bytes"}}
	metrics.Read(sample)

	return sample[0].Value.Uint64()
}

// peakHeapGrowth runs f with a collector that runs often and returns the largest growth of the heap above its size
// before f that a sampler saw while f ran, or at its end.
func peakHeapGrowth(f func()) uint64 {
	old := debug.SetGCPercent(5)
	defer debug.SetGCPercent(old)
	runtime.GC()
	base := heapObjects()

	var peak atomic.Uint64
	stop, done := make(chan struct{}), make(chan struct{})
	go func() {
		defer close(done)
		for {
			select {
			case <-stop:
				return
			default:
			}
			if v := heapObjects(); v > peak.Load() {
				peak.Store(v)
			}
			runtime.Gosched()
		}
	}()
	f()
	close(stop)
	<-done
	runtime.GC()
	top := max(peak.Load(), heapObjects())
	if top <= base {
		return 0
	}

	return top - base
}

func TestOpen_ManyForeignEntries(t *testing.T) {
	const foreign = 100_000

	r := newRig(t)
	dir := r.dir()
	writeSegment(t, dir, stateName, []byte(`{"agent":"state"}`))
	makeForeign(t, dir, foreign)
	state := statOf(t, filepath.Join(dir, stateName))

	var sp *Spool
	growth := peakHeapGrowth(func() {
		sp = r.open()
		putTagged(t, sp, epoch, "many", 5, 200)
	})
	if growth >= uint64(4*mebi) {
		t.Errorf("opening a directory with %d foreign entries and handing over 5 records grew the heap by %d bytes, want less than 4 MiB", foreign, growth)
	}
	runtime.KeepAlive(sp)
	r.closeSpool(sp)

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	own := 0
	for _, e := range entries {
		if isSpoolName(e.Name()) {
			own++
		}
	}
	wantForeign := foreign + (foreign+foreignPerInode-1)/foreignPerInode + 1
	if len(entries)-own != wantForeign {
		t.Errorf("%d foreign entries remain, want %d (nothing deleted or renamed)", len(entries)-own, wantForeign)
	}
	requireUnchanged(t, stateName, filepath.Join(dir, stateName), state)
	if w := warnings(r.logs); len(w) != 0 {
		t.Errorf("a directory of foreign entries caused warnings: %v", w[0])
	}
}

func TestSpool_MemoryBound(t *testing.T) {
	const foreign = 100_000

	r := newRig(t)
	r.opts.MaxBytes = 64 << 30
	dir := r.dir()
	makeForeign(t, dir, foreign)
	boots := []string{otherBoot, thirdBoot}
	var files []fileSpec
	for i := range maxFiles {
		f := fileSpec{first: forgedFirst + 4*ofLenInt(i), n: 4, sealed: i < maxFiles-1, boot: boots[i%2], capture: epoch.Add(time.Duration(i) * time.Minute), tag: "m"}
		if !f.sealed {
			f.boot = testBoot
		}
		files = append(files, f)
	}
	writeFiles(t, dir, files...)
	last := files[len(files)-1]
	writeMeta(t, dir, metaSpec{agent: testAgent, boot: testBoot, next: last.last() + 1})

	var sp *Spool
	growth := peakHeapGrowth(func() {
		sp = r.open()
		for i := range MaxAckRanges {
			mustAck(t, sp, forgedFirst+1+2*ofLenInt(i), forgedFirst+1+2*ofLenInt(i))
		}
		oldest, err := sp.Read(context.Background(), 0, 100, mebi)
		if err != nil || len(oldest.Records) == 0 {
			t.Errorf("Read(0) = %d records, %v, want records from the oldest end", len(oldest.Records), err)
		}
		newest, err := sp.Read(context.Background(), last.last()-3, 100, mebi)
		if err != nil || len(newest.Records) != 3 {
			t.Errorf("Read(near the newest) = %d records, %v, want the last 3", len(newest.Records), err)
		}
		putTagged(t, sp, epoch.Add(24*time.Hour), "mem", 3, 200)
	})
	if growth >= uint64(4*mebi) {
		t.Errorf("a spool capped at 64 GiB with %d files, 64 acknowledged ranges and %d foreign entries grew the heap by %d bytes, want less than 4 MiB", maxFiles, foreign, growth)
	}
	runtime.KeepAlive(sp)
}

// benchOptions returns the options of a benchmark spool in dir.
func benchOptions(dir string, maxBytes int64) Options {
	return Options{
		Dir: dir, AgentID: testAgent, BootID: testBoot, MaxBytes: maxBytes, SyncInterval: time.Hour,
		Now:       func() time.Time { return epoch },
		Available: func(string) (uint64, error) { return hugeAvailable, nil },
	}
}

// openClose opens and closes the spool b.N times and times the opening.
func openClose(b *testing.B, opts Options) {
	b.Helper()

	b.ResetTimer()
	for range b.N {
		sp, err := Open(opts, silentLogger())
		if err != nil {
			b.Fatalf("Open() = %v", err)
		}
		b.StopTimer()
		if err := sp.Close(context.Background()); err != nil {
			b.Fatalf("Close() = %v", err)
		}
		b.StartTimer()
	}
}

// BenchmarkOpen_Full measures opening a spool filled to the default spool.max_size of 2 GiB.
func BenchmarkOpen_Full(b *testing.B) {
	const capacity = int64(2) << 30

	opts := benchOptions(b.TempDir(), capacity)
	sp, err := Open(opts, silentLogger())
	if err != nil {
		b.Fatalf("Open() = %v", err)
	}
	for i := 0; ; i++ {
		if i%64 == 0 {
			size, err := sp.SpoolBytes(context.Background())
			if err != nil {
				b.Fatalf("SpoolBytes() = %v", err)
			}
			if size >= capacity-64*mebi {
				break
			}
		}
		if err := sp.Put(context.Background(), batchOf(epoch.Add(time.Duration(i)*time.Second), fmt.Sprintf("full%d", i), 8, 16000)); err != nil {
			b.Fatalf("Put() = %v", err)
		}
	}
	if err := sp.Close(context.Background()); err != nil {
		b.Fatalf("Close() = %v", err)
	}
	openClose(b, opts)
}

// BenchmarkOpen_ManyFiles measures opening a spool of 1,024 data files.
func BenchmarkOpen_ManyFiles(b *testing.B) {
	dir := b.TempDir()
	boots := []string{otherBoot, thirdBoot}
	var files []fileSpec
	for i := range maxFiles {
		f := fileSpec{first: forgedFirst + 20*ofLenInt(i), n: 20, sealed: i < maxFiles-1, boot: boots[i%2], capture: epoch.Add(time.Duration(i) * time.Minute), tag: "mf"}
		if !f.sealed {
			f.boot = testBoot
		}
		files = append(files, f)
	}
	for _, f := range files {
		if err := os.WriteFile(filepath.Join(dir, f.name()), f.bytes(b), 0o600); err != nil {
			b.Fatalf("WriteFile() = %v", err)
		}
	}
	meta := metaJSON(metaSpec{agent: testAgent, boot: testBoot, next: files[len(files)-1].last() + 1})
	if err := os.WriteFile(filepath.Join(dir, metaName), meta, 0o600); err != nil {
		b.Fatalf("WriteFile(meta) = %v", err)
	}
	openClose(b, benchOptions(dir, 2<<30))
}
