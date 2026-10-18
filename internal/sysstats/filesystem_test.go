package sysstats_test

import (
	"errors"
	"log/slog"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"syscall"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

// Statistics the fake statfs reports for the tests.
var (
	rootStats = sysstats.FSStats{BlockSize: 4096, Blocks: 1000, BlocksFree: 400, BlocksAvailable: 300, Inodes: 200, InodesFree: 150}
	procStats = sysstats.FSStats{BlockSize: 1024, Blocks: 50, BlocksFree: 10, BlocksAvailable: 10}
)

// rootRecords are the records of "/" with rootStats.
func rootRecords() []wantRec {
	return []wantRec{
		rec("filesystem_size_bytes", 4096000, "bytes", "mount", "/"),
		rec("filesystem_used_bytes", 2457600, "bytes", "mount", "/"),
		rec("filesystem_free_bytes", 1638400, "bytes", "mount", "/"),
		rec("filesystem_available_bytes", 1228800, "bytes", "mount", "/"),
		rec("filesystem_inodes", 200, "", "mount", "/"),
		rec("filesystem_inodes_used", 50, "", "mount", "/"),
		rec("filesystem_inodes_free", 150, "", "mount", "/"),
	}
}

// procRecords are the records of "/proc" with procStats (no inodes).
func procRecords() []wantRec {
	return []wantRec{
		rec("filesystem_size_bytes", 51200, "bytes", "mount", "/proc"),
		rec("filesystem_used_bytes", 40960, "bytes", "mount", "/proc"),
		rec("filesystem_free_bytes", 10240, "bytes", "mount", "/proc"),
		rec("filesystem_available_bytes", 10240, "bytes", "mount", "/proc"),
	}
}

// newStatFake returns a fake statfs that knows "/" and "/proc".
func newStatFake() *statFake {
	return &statFake{stats: map[string]sysstats.FSStats{"/": rootStats, "/proc": procStats}, errs: map[string]error{}}
}

func fixtureProcSources(f *statFake) sysstats.Sources {
	return sysstats.Sources{Proc: os.DirFS(procDir), StatFS: f.statFS}
}

func TestFilesystem_RecordsMountPoints(t *testing.T) {
	cases := []struct {
		name   string
		mounts []string
		want   []wantRec
	}{
		{"configuration order", []string{"/", "/proc"}, slices.Concat(rootRecords(), procRecords())},
		{"reversed configuration order", []string{"/proc", "/"}, slices.Concat(procRecords(), rootRecords())},
		{"one mount point", []string{"/proc"}, procRecords()},
	}
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			fake := newStatFake()
			c := sysstats.NewFilesystem(fixtureProcSources(fake), tt.mounts, nil)

			got := mustCollect(t, c, runStart)

			requireRecords(t, runStart, got, tt.want)
			if calls := fake.called(); !slices.Equal(calls, tt.mounts) {
				t.Errorf("statfs was asked for %q, want %q", calls, tt.mounts)
			}
		})
	}

	t.Run("the collector copies the mount points", func(t *testing.T) {
		mounts := []string{"/", "/proc"}
		c := sysstats.NewFilesystem(fixtureProcSources(newStatFake()), mounts, nil)
		mounts[0] = "/dev"

		got := mustCollect(t, c, runStart)

		requireRecords(t, runStart, got, slices.Concat(rootRecords(), procRecords()))
	})
}

// srvMountinfo is the fixture mount table, with a line for /srv when mounted.
func srvMountinfo(t *testing.T, mounted bool) string {
	t.Helper()

	table := text(t, loadDir(t, procDir), "self/mountinfo")
	if mounted {
		table += "60 28 8:1 / /srv rw,relatime - ext4 /dev/vdb rw\n"
	}
	return table
}

func TestFilesystem_NotMountedLoggedOnChange(t *testing.T) {
	notMounted := wantLog{slog.LevelWarn, "mount point not mounted", map[string]string{"collector": "filesystem", "mount": "/srv"}}
	mounted := wantLog{slog.LevelInfo, "mount point mounted", map[string]string{"mount": "/srv"}}
	steps := []struct {
		name   string
		mount  bool
		logged []wantLog
	}{
		{"not mounted at the first sample", false, []wantLog{notMounted}},
		{"still not mounted", false, []wantLog{notMounted}},
		{"and again", false, []wantLog{notMounted}},
		{"mounted", true, []wantLog{notMounted, mounted}},
		{"still mounted", true, []wantLog{notMounted, mounted}},
		{"unmounted again", false, []wantLog{notMounted, mounted, notMounted}},
	}
	fake := newStatFake()
	fake.stats["/srv"] = sysstats.FSStats{BlockSize: 512, Blocks: 8, BlocksFree: 4, BlocksAvailable: 4}
	store, log := newLogStore()
	proc := fstest.MapFS{}
	c := sysstats.NewFilesystem(sysstats.Sources{Proc: proc, StatFS: fake.statFS}, []string{"/", "/srv"}, log)
	for i, step := range steps {
		set(proc, "self/mountinfo", srvMountinfo(t, step.mount))

		got := mustCollect(t, c, runStart.Add(time.Duration(i)*time.Minute))

		srv := 0
		for _, r := range find(got, "filesystem_size_bytes") {
			if r.labels["mount"] == "/srv" {
				srv++
			}
		}
		if want := map[bool]int{true: 1, false: 0}[step.mount]; srv != want {
			t.Errorf("%s: %d filesystem_size_bytes records for /srv, want %d", step.name, srv, want)
		}
		requireLogged(t, store, step.name, step.logged)
	}
	srvCalls := 0
	for _, call := range fake.called() {
		if call == "/srv" {
			srvCalls++
		}
	}
	if srvCalls != 2 {
		t.Errorf("statfs was asked for /srv %d times, want 2 (only in the samples where it was mounted)", srvCalls)
	}
}

func TestFilesystem_Errors(t *testing.T) {
	const prefix = "sysstats: filesystem.mount_points["
	const huge = 1<<64 - 1
	bad := func(s sysstats.FSStats) func(*statFake) {
		return func(f *statFake) { f.stats["/proc"] = s }
	}
	cases := []struct {
		name  string
		setup func(f *statFake)
		want  string
	}{
		{"statfs fails for mount 1", func(f *statFake) { f.errs["/proc"] = syscall.EACCES }, prefix + "1]: statfs: permission denied"},
		{"statfs fails for mount 0", func(f *statFake) { f.errs["/"] = syscall.EACCES }, prefix + "0]: statfs: permission denied"},
		{
			"free blocks above the total",
			bad(sysstats.FSStats{BlockSize: 1024, Blocks: 50, BlocksFree: 51, BlocksAvailable: 10}), prefix + "1]: statfs: free above total",
		},
		{
			"available blocks above the total",
			bad(sysstats.FSStats{BlockSize: 1024, Blocks: 50, BlocksFree: 10, BlocksAvailable: 51}), prefix + "1]: statfs: free above total",
		},
		{
			"free inodes above the total",
			bad(sysstats.FSStats{BlockSize: 1024, Blocks: 50, Inodes: 10, InodesFree: 11}), prefix + "1]: statfs: free above total",
		},
		{
			"free blocks above the total of mount 0",
			func(f *statFake) { f.stats["/"] = sysstats.FSStats{BlockSize: 1024, Blocks: 5, BlocksFree: 6} }, prefix + "0]: statfs: free above total",
		},
		{"size of 2^64 from 2^63 blocks of 2 bytes", bad(sysstats.FSStats{BlockSize: 2, Blocks: 1 << 63}), prefix + "1]: statfs: size above 64 bits"},
		{"size from the largest block count", bad(sysstats.FSStats{BlockSize: 4096, Blocks: huge}), prefix + "1]: statfs: size above 64 bits"},
		{"size of exactly 2^64", bad(sysstats.FSStats{BlockSize: 4096, Blocks: 1 << 52}), prefix + "1]: statfs: size above 64 bits"},
	}
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			fake := newStatFake()
			tt.setup(fake)
			c := sysstats.NewFilesystem(fixtureProcSources(fake), []string{"/", "/proc"}, nil)

			requireFails(t, c, runStart, tt.want)
		})
	}

	t.Run("the error does not name the path", func(t *testing.T) {
		fake := newStatFake()
		fake.errs["/proc"] = syscall.EACCES
		c := sysstats.NewFilesystem(fixtureProcSources(fake), []string{"/", "/proc"}, nil)

		_, err := collect(c, runStart)

		if err == nil || strings.Contains(err.Error(), "/proc") || !errors.Is(err, syscall.EACCES) {
			t.Errorf("Collect() error = %v, want one without the path that wraps syscall.EACCES", err)
		}
	})

	t.Run("the largest size below 2^64 is recorded", func(t *testing.T) {
		fake := newStatFake()
		fake.stats["/proc"] = sysstats.FSStats{BlockSize: 4096, Blocks: 1<<52 - 1}
		c := sysstats.NewFilesystem(fixtureProcSources(fake), []string{"/proc"}, nil)

		got := mustCollect(t, c, runStart)

		sizes := find(got, "filesystem_size_bytes")
		want := float64(uint64(1<<52-1) * 4096)
		if len(sizes) != 1 || sizes[0].value != want {
			t.Errorf("filesystem_size_bytes records = %v, want one with the exact value %v", sizes, want)
		}
	})
}

func TestStatFS_ReadsTheRootFilesystem(t *testing.T) {
	got, err := sysstats.StatFS("/")

	if err != nil {
		t.Fatalf("StatFS(%q) error = %v, want nil", "/", err)
	}
	if got.BlockSize == 0 || got.Blocks < got.BlocksFree || got.BlocksFree < got.BlocksAvailable {
		t.Errorf("StatFS(%q) = %+v, want BlockSize > 0 and Blocks >= BlocksFree >= BlocksAvailable", "/", got)
	}

	missing := filepath.Join(string(filepath.Separator), "vandox-missing-path-x7", "child")
	if _, err := sysstats.StatFS(missing); err == nil || strings.Contains(err.Error(), "vandox-missing-path-x7") {
		t.Errorf("StatFS of a missing path error = %v, want an error without the path", err)
	}
}

func TestFilesystem_HangingStatFSIsAbandoned(t *testing.T) {
	hangingSample(t, func(_ *testing.T, g *gate) scheduler.Collector {
		src := sysstats.Sources{
			Proc: os.DirFS(procDir),
			StatFS: func(string) (sysstats.FSStats, error) {
				g.wait()
				return rootStats, nil
			},
		}
		return sysstats.NewFilesystem(src, []string{"/"}, nil)
	})
}
