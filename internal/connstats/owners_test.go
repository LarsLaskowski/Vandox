package connstats

import (
	"context"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"testing/fstest"
)

const (
	fdinfoDir   = "../../testdata/connections/sandbox/fdinfo"
	socketMount = int32(10) // the mnt_id of the captured sockets
)

// readFdinfoSample returns a captured fdinfo file.
func readFdinfoSample(t *testing.T, name string) string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join(fdinfoDir, name))
	if err != nil {
		t.Fatalf("reading the fixture fdinfo/%s: %v", name, err)
	}
	return string(data)
}

// fdinfoOf returns the captured fdinfo of a TCP listener with only mnt_id and ino changed.
func fdinfoOf(t *testing.T, mount int32, ino uint64) string {
	t.Helper()

	s := readFdinfoSample(t, "tcp-listen")
	if !strings.Contains(s, "mnt_id:\t10\n") || !strings.Contains(s, "ino:\t6087\n") {
		t.Fatalf("fixture fdinfo/tcp-listen = %q, want mnt_id 10 and ino 6087", s)
	}
	s = strings.Replace(s, "mnt_id:\t10\n", fmt.Sprintf("mnt_id:\t%d\n", mount), 1)
	return strings.Replace(s, "ino:\t6087\n", fmt.Sprintf("ino:\t%d\n", ino), 1)
}

// procTree is an in-memory /proc with descriptors.
type procTree struct {
	t     *testing.T
	files fstest.MapFS
}

func newProcTree(t *testing.T) *procTree {
	t.Helper()

	return &procTree{t: t, files: fstest.MapFS{}}
}

// add gives process pid the descriptor fd of a socket-like file with mount and inode.
func (p *procTree) add(pid int32, fd int, mount int32, ino uint64) {
	p.t.Helper()

	p.addRaw(pid, fd, fdinfoOf(p.t, mount, ino))
}

func (p *procTree) addRaw(pid int32, fd int, content string) {
	p.files[fdinfoName(pid, fd)] = &fstest.MapFile{Data: []byte(content)}
}

func fdinfoName(pid int32, fd int) string {
	return strconv.Itoa(int(pid)) + "/fdinfo/" + strconv.Itoa(fd)
}

// indexOf numbers the inodes 0, 1, 2 … as the collector's inode index does.
func indexOf(inodes ...uint64) map[uint64]int32 {
	index := make(map[uint64]int32, len(inodes))
	for i, ino := range inodes {
		index[ino] = int32(i)
	}
	return index
}

// faultFS answers the named files with an error at Open, at the first Read or at ReadDir; the maps may change between runs.
type faultFS struct {
	fs.FS
	openErr map[string]error
	readErr map[string]error
	dirErr  map[string]error
}

func newFaultFS(f fs.FS) *faultFS {
	return &faultFS{FS: f, openErr: map[string]error{}, readErr: map[string]error{}, dirErr: map[string]error{}}
}

func (f *faultFS) Open(name string) (fs.File, error) {
	if err, ok := f.openErr[name]; ok {
		return nil, &fs.PathError{Op: "open", Path: name, Err: err}
	}
	file, err := f.FS.Open(name)
	if err != nil {
		return nil, err
	}
	if rerr, ok := f.readErr[name]; ok {
		return failingFile{File: file, err: &fs.PathError{Op: "read", Path: name, Err: rerr}}, nil
	}
	if derr, ok := f.dirErr[name]; ok {
		return failingDir{File: file, err: &fs.PathError{Op: "readdirent", Path: name, Err: derr}}, nil
	}
	return file, nil
}

// failingFile is a file whose Read fails.
type failingFile struct {
	fs.File
	err error
}

func (f failingFile) Read([]byte) (int, error) { return 0, f.err }

// failingDir is a directory whose ReadDir fails.
type failingDir struct {
	fs.File
	err error
}

func (f failingDir) ReadDir(int) ([]fs.DirEntry, error) { return nil, f.err }

func scan(t *testing.T, proc fs.FS, pids []int32, index map[uint64]int32) (owners, int, error) {
	t.Helper()

	return scanOwners(context.Background(), proc, pids, index, socketMount)
}

// wantOwner checks the owner of one tracked socket.
func wantOwner(t *testing.T, o owners, slot int32, want int32) {
	t.Helper()

	if int(slot) >= len(o.owner) {
		t.Fatalf("owners hold %d slots, want slot %d", len(o.owner), slot)
	}
	if o.owner[slot] != want {
		t.Errorf("owner of socket slot %d = %d, want %d", slot, o.owner[slot], want)
	}
}

// wantCount checks the number of distinct sockets of one PID.
func wantCount(t *testing.T, o owners, pid int32, want uint32) {
	t.Helper()

	if got := o.perPID[pid]; got != want {
		t.Errorf("distinct sockets of PID %d = %d, want %d", pid, got, want)
	}
}

func TestParseFdinfo_Samples(t *testing.T) {
	want := map[string]Fdinfo{
		"tcp-listen":       {MountID: 10, Inode: 6087},
		"udp":              {MountID: 10, Inode: 6088},
		"unix":             {MountID: 10, Inode: 6089},
		"pipe":             {MountID: 16, Inode: 6090},
		"regular-file":     {MountID: 28, Inode: 210788},
		"epoll-40-watches": {MountID: 17, Inode: 26}, // the tfd: lines repeat ino: in hex and are ignored
	}
	entries, err := os.ReadDir(fdinfoDir)
	if err != nil {
		t.Fatalf("reading the fixture directory: %v", err)
	}
	if len(entries) != len(want) {
		t.Fatalf("fixture directory holds %d files, want the %d the test names", len(entries), len(want))
	}
	for _, e := range entries {
		w, ok := want[e.Name()]
		if !ok {
			t.Errorf("fixture fdinfo/%s is not named by the test", e.Name())
			continue
		}
		data := []byte(readFdinfoSample(t, e.Name()))
		t.Run(e.Name(), func(t *testing.T) {
			got, err := ParseFdinfo(data)
			if err != nil || got != w {
				t.Errorf("ParseFdinfo(%s) = %+v, %v, want %+v, nil", e.Name(), got, err, w)
			}
		})
		t.Run(e.Name()+" cut at 256 bytes", func(t *testing.T) {
			cut := data[:min(len(data), 256)]
			got, err := ParseFdinfo(cut)
			if err != nil || got != w {
				t.Errorf("ParseFdinfo(first %d bytes of %s) = %+v, %v, want %+v, nil", len(cut), e.Name(), got, err, w)
			}
		})
	}
}

func TestParseFdinfo_Rejects(t *testing.T) {
	valid := "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t6087\n"
	if got, err := ParseFdinfo([]byte(valid)); err != nil || got != (Fdinfo{MountID: 10, Inode: 6087}) {
		t.Fatalf("ParseFdinfo(valid) = %+v, %v, want {10 6087}, nil (a reject case would prove nothing)", got, err)
	}

	rejects := []struct {
		name, data string
	}{
		{"empty", ""},
		{"one line", "pos:\t0\n"},
		{"three lines", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\n"},
		{"no ino line but a tfd line", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\ntfd:       44 events:       19 data:               2c  pos:0 ino:17ed sdev:9\n"},
		{"ino before mnt_id", "pos:\t0\nflags:\t02000002\nino:\t6087\nmnt_id:\t10\n"},
		{"flags missing", "pos:\t0\nmnt_id:\t10\nino:\t6087\nscm_fds: 0\n"},
		{"pos missing", "flags:\t02000002\nmnt_id:\t10\nino:\t6087\nscm_fds: 0\n"},
		{"space instead of a tab in mnt_id", "pos:\t0\nflags:\t02000002\nmnt_id: 10\nino:\t6087\n"},
		{"space instead of a tab in ino", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino: 6087\n"},
		{"pos not digits", "pos:\tabc\nflags:\t02000002\nmnt_id:\t10\nino:\t6087\n"},
		{"flags empty", "pos:\t0\nflags:\t\nmnt_id:\t10\nino:\t6087\n"},
		{"mnt_id zero", "pos:\t0\nflags:\t02000002\nmnt_id:\t0\nino:\t6087\n"},
		{"mnt_id empty", "pos:\t0\nflags:\t02000002\nmnt_id:\t\nino:\t6087\n"},
		{"mnt_id negative", "pos:\t0\nflags:\t02000002\nmnt_id:\t-5\nino:\t6087\n"},
		{"mnt_id above int32", "pos:\t0\nflags:\t02000002\nmnt_id:\t2147483648\nino:\t6087\n"},
		{"mnt_id with 11 digits", "pos:\t0\nflags:\t02000002\nmnt_id:\t00000000010\nino:\t6087\n"},
		{"mnt_id hex", "pos:\t0\nflags:\t02000002\nmnt_id:\t1f\nino:\t6087\n"},
		{"ino empty", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t\n"},
		{"ino hex", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t17ed\n"},
		{"ino negative", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t-1\n"},
		{"ino above 64 bits", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t18446744073709551616\n"},
		{"ino with 21 digits", "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t000000000000000000001\n"},
		{"binary", "\x00\x01\x02\xff\xfe"},
	}
	for _, c := range rejects {
		t.Run(c.name, func(t *testing.T) {
			got, err := ParseFdinfo([]byte(c.data))
			if err == nil {
				t.Errorf("ParseFdinfo(%q) = %+v, nil, want an error", c.data, got)
			}
		})
	}

	t.Run("boundary values are accepted", func(t *testing.T) {
		data := "pos:\t0\nflags:\t02000002\nmnt_id:\t2147483647\nino:\t18446744073709551615\n"
		got, err := ParseFdinfo([]byte(data))
		want := Fdinfo{MountID: 2147483647, Inode: 18446744073709551615}
		if err != nil || got != want {
			t.Errorf("ParseFdinfo(%q) = %+v, %v, want %+v, nil", data, got, err, want)
		}
	})

	t.Run("the error holds no file content", func(t *testing.T) {
		const secret = "TOPSECRETVALUE"
		_, err := ParseFdinfo([]byte("pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\t" + secret + "\n"))
		if err == nil {
			t.Fatal("ParseFdinfo(non-numeric ino) error = nil, want an error")
		}
		if strings.Contains(err.Error(), secret) {
			t.Errorf("ParseFdinfo error %q holds the file's content", err)
		}
	})
}

func TestScanOwners_LowestPIDOwnsSharedSocket(t *testing.T) {
	p := newProcTree(t)
	p.add(10, 3, socketMount, 100)
	p.add(20, 5, socketMount, 100)
	p.add(30, 4, socketMount, 100)
	p.add(20, 6, socketMount, 101)

	o, unreadable, err := scan(t, p.files, []int32{10, 20, 30}, indexOf(100, 101))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
	}
	wantOwner(t, o, 0, 10)
	wantOwner(t, o, 1, 20)
	wantCount(t, o, 10, 1)
	wantCount(t, o, 20, 2)
	wantCount(t, o, 30, 1)
}

func TestScanOwners_CountsDistinctSocketsPerProcess(t *testing.T) {
	p := newProcTree(t)
	p.add(10, 3, socketMount, 100)
	p.add(10, 4, socketMount, 101)
	p.add(10, 5, socketMount, 102)
	p.add(20, 3, socketMount, 102)

	o, unreadable, err := scan(t, p.files, []int32{10, 20}, indexOf(100, 101, 102))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
	}
	wantCount(t, o, 10, 3)
	wantCount(t, o, 20, 1)
	wantOwner(t, o, 0, 10)
	wantOwner(t, o, 1, 10)
	wantOwner(t, o, 2, 10)
}

func TestScanOwners_IgnoresSocketsOutsideTables(t *testing.T) {
	t.Run("captured descriptor types", func(t *testing.T) {
		p := newProcTree(t)
		p.addRaw(10, 3, readFdinfoSample(t, "unix"))
		p.addRaw(10, 4, readFdinfoSample(t, "pipe"))
		p.addRaw(10, 5, readFdinfoSample(t, "regular-file"))
		p.addRaw(10, 6, readFdinfoSample(t, "epoll-40-watches"))
		p.addRaw(10, 7, readFdinfoSample(t, "tcp-listen"))
		// The unix socket (6089) is in none of the tables; a pipe may carry the number of a table socket but sits on its own mount.
		o, unreadable, err := scan(t, p.files, []int32{10}, indexOf(6087, 6090, 210788, 26))
		if err != nil || unreadable != 0 {
			t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
		}
		wantOwner(t, o, 0, 10)
		wantOwner(t, o, 1, 0)
		wantOwner(t, o, 2, 0)
		wantOwner(t, o, 3, 0)
		wantCount(t, o, 10, 1)
	})

	t.Run("a process with only such descriptors owns nothing", func(t *testing.T) {
		p := newProcTree(t)
		p.addRaw(10, 3, readFdinfoSample(t, "unix"))
		p.addRaw(10, 4, readFdinfoSample(t, "pipe"))
		p.add(10, 5, socketMount, 999)
		o, unreadable, err := scan(t, p.files, []int32{10}, indexOf(100, 101))
		if err != nil || unreadable != 0 {
			t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
		}
		wantOwner(t, o, 0, 0)
		wantOwner(t, o, 1, 0)
		if n, ok := o.perPID[10]; ok && n != 0 {
			t.Errorf("distinct sockets of PID 10 = %d, want none", n)
		}
	})
}

func TestScanOwners_EndedProcessIsSkipped(t *testing.T) {
	cases := []struct {
		name    string
		present bool // the process has a descriptor in the tree
		setup   func(f *faultFS)
	}{
		{"fdinfo directory is gone", false, func(*faultFS) {}},
		{"opening the directory fails with ENOENT", true, func(f *faultFS) { f.openErr["10/fdinfo"] = syscall.ENOENT }},
		{"opening the directory fails with ESRCH", true, func(f *faultFS) { f.openErr["10/fdinfo"] = syscall.ESRCH }},
		{"listing the directory fails with ESRCH", true, func(f *faultFS) { f.dirErr["10/fdinfo"] = syscall.ESRCH }},
		{"listing the directory fails with ENOENT", true, func(f *faultFS) { f.dirErr["10/fdinfo"] = syscall.ENOENT }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			p := newProcTree(t)
			p.add(20, 3, socketMount, 100)
			if c.present {
				p.add(10, 3, socketMount, 100)
			}
			f := newFaultFS(p.files)
			c.setup(f)

			o, unreadable, err := scan(t, f, []int32{10, 20}, indexOf(100))
			if err != nil || unreadable != 0 {
				t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil (an ended process is no error)", unreadable, err)
			}
			wantOwner(t, o, 0, 20)
			wantCount(t, o, 10, 0)
			wantCount(t, o, 20, 1)
		})
	}
}

func TestScanOwners_ClosedDescriptorKeepsTheOtherSockets(t *testing.T) {
	p := newProcTree(t)
	p.add(10, 3, socketMount, 100)
	p.add(10, 4, socketMount, 101)
	p.add(10, 5, socketMount, 102)
	f := newFaultFS(p.files)
	f.openErr["10/fdinfo/4"] = syscall.ENOENT // closed after the directory was listed

	o, unreadable, err := scan(t, f, []int32{10}, indexOf(100, 101, 102))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil (one closed descriptor is no error)", unreadable, err)
	}
	wantOwner(t, o, 0, 10)
	wantOwner(t, o, 1, 0)
	wantOwner(t, o, 2, 10)
	wantCount(t, o, 10, 2)
}

func TestScanOwners_UnreadableProcessCountedOnce(t *testing.T) {
	cases := []struct {
		name  string
		setup func(f *faultFS, p *procTree)
	}{
		{"opening a file fails with EACCES", func(f *faultFS, _ *procTree) { f.openErr["10/fdinfo/4"] = syscall.EACCES }},
		{"opening a file fails with EPERM", func(f *faultFS, _ *procTree) { f.openErr["10/fdinfo/4"] = syscall.EPERM }},
		{"opening a file fails with EIO", func(f *faultFS, _ *procTree) { f.openErr["10/fdinfo/4"] = syscall.EIO }},
		{"reading a file fails", func(f *faultFS, _ *procTree) { f.readErr["10/fdinfo/4"] = syscall.EIO }},
		{"opening the directory fails with EACCES", func(f *faultFS, _ *procTree) { f.openErr["10/fdinfo"] = syscall.EACCES }},
		{"listing the directory fails with EPERM", func(f *faultFS, _ *procTree) { f.dirErr["10/fdinfo"] = syscall.EPERM }},
		{"a file without an ino line", func(_ *faultFS, p *procTree) { p.addRaw(10, 4, "pos:\t0\nflags:\t02000002\nmnt_id:\t10\n") }},
		{"a file in another shape", func(_ *faultFS, p *procTree) { p.addRaw(10, 4, "not an fdinfo file\n") }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			p := newProcTree(t)
			p.add(10, 3, socketMount, 100)
			p.add(10, 4, socketMount, 100)
			p.add(10, 5, socketMount, 101)
			p.add(20, 3, socketMount, 101)
			f := newFaultFS(p.files)
			c.setup(f, p)

			o, unreadable, err := scan(t, f, []int32{10, 20}, indexOf(100, 101))
			if unreadable != 1 {
				t.Errorf("scanOwners() unreadable = %d, want 1 (the process counts once)", unreadable)
			}
			wantErrPrefix(t, err, "connstats: 10/fdinfo: ")
			// The scan of PID 10 stops at its first failure, so descriptor 5 is never read.
			wantOwner(t, o, 1, 20)
			wantCount(t, o, 20, 1)
		})
	}

	t.Run("several failing descriptors of one process count once", severalFailingDescriptors)
	t.Run("two unreadable processes count twice and the first error stays", twoUnreadableProcesses)
	t.Run("the error holds no file content", unreadableErrorHoldsNoContent)
}

func wantErrPrefix(t *testing.T, err error, prefix string) {
	t.Helper()

	if err == nil || !strings.HasPrefix(err.Error(), prefix) {
		t.Errorf("scanOwners() first error = %v, want one starting with %q", err, prefix)
	}
}

func severalFailingDescriptors(t *testing.T) {
	p := newProcTree(t)
	for fd := 3; fd < 8; fd++ {
		p.add(10, fd, socketMount, uint64(100+fd))
	}
	f := newFaultFS(p.files)
	for fd := 3; fd < 8; fd++ {
		f.openErr[fdinfoName(10, fd)] = syscall.EACCES
	}
	_, unreadable, err := scan(t, f, []int32{10}, indexOf(103, 104))
	if unreadable != 1 || err == nil {
		t.Errorf("scanOwners() unreadable = %d, error = %v, want 1 and an error", unreadable, err)
	}
}

func twoUnreadableProcesses(t *testing.T) {
	p := newProcTree(t)
	p.add(10, 3, socketMount, 100)
	p.add(20, 3, socketMount, 100)
	p.add(30, 3, socketMount, 100)
	f := newFaultFS(p.files)
	f.openErr["10/fdinfo/3"] = syscall.EACCES
	f.openErr["20/fdinfo/3"] = syscall.EPERM

	o, unreadable, err := scan(t, f, []int32{10, 20, 30}, indexOf(100))
	if unreadable != 2 {
		t.Errorf("scanOwners() unreadable = %d, want 2", unreadable)
	}
	wantErrPrefix(t, err, "connstats: 10/fdinfo: ")
	wantOwner(t, o, 0, 30)
}

func unreadableErrorHoldsNoContent(t *testing.T) {
	p := newProcTree(t)
	p.addRaw(10, 3, "pos:\t0\nflags:\t02000002\nmnt_id:\t10\nino:\tTOPSECRETVALUE\n")
	_, unreadable, err := scan(t, p.files, []int32{10}, indexOf(100))
	if unreadable != 1 || err == nil {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 1 and an error", unreadable, err)
	}
	if strings.Contains(err.Error(), "TOPSECRETVALUE") {
		t.Errorf("scanOwners() error %q holds the file's content", err)
	}
}

func TestScanOwners_DescriptorOffTheSocketMountIsNotAnOwner(t *testing.T) {
	const foreignMount = 76
	p := newProcTree(t)
	// A low PID holds files with the inode of a listener on a mount of its own, 10,000 times.
	for fd := 3; fd < 10003; fd++ {
		p.add(5, fd, foreignMount, 100)
	}
	p.add(9, 3, socketMount, 100)
	p.add(9, 4, socketMount, 101)

	o, unreadable, err := scan(t, p.files, []int32{5, 9}, indexOf(100, 101))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
	}
	wantOwner(t, o, 0, 9)
	wantOwner(t, o, 1, 9)
	wantCount(t, o, 5, 0)
	wantCount(t, o, 9, 2)
}

func TestScanOwners_DuplicatedDescriptorsCountOnce(t *testing.T) {
	p := newProcTree(t)
	// PID 10 holds socket 100 three times with another socket between the copies.
	p.add(10, 3, socketMount, 100)
	p.add(10, 4, socketMount, 101)
	p.add(10, 5, socketMount, 100)
	p.add(10, 6, socketMount, 100)
	p.add(20, 3, socketMount, 100)
	p.add(20, 4, socketMount, 100)

	o, unreadable, err := scan(t, p.files, []int32{10, 20}, indexOf(100, 101))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
	}
	wantCount(t, o, 10, 2)
	wantCount(t, o, 20, 1)
	wantOwner(t, o, 0, 10)
	wantOwner(t, o, 1, 10)
}

func TestScanOwners_ManyDescriptorsOfALowPIDStarveNoOne(t *testing.T) {
	const copies = 300000
	data := []byte(fdinfoOf(t, socketMount, 100))
	files := fstest.MapFS{}
	for fd := 3; fd < 3+copies; fd++ {
		files[fdinfoName(1, fd)] = &fstest.MapFile{Data: data}
	}
	files[fdinfoName(2, 3)] = &fstest.MapFile{Data: []byte(fdinfoOf(t, socketMount, 101))}

	o, unreadable, err := scan(t, files, []int32{1, 2}, indexOf(100, 101))
	if err != nil || unreadable != 0 {
		t.Fatalf("scanOwners() unreadable = %d, error = %v, want 0, nil", unreadable, err)
	}
	wantOwner(t, o, 0, 1)
	wantOwner(t, o, 1, 2)
	wantCount(t, o, 1, 1)
	wantCount(t, o, 2, 1)
}
