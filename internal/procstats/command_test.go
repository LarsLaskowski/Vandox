package procstats

import (
	"errors"
	"fmt"
	"io/fs"
	"slices"
	"strings"
	"syscall"
	"testing"
)

// hostileSpecs are processes whose names and command lines are text a local user chose.
func hostileSpecs() []procSpec {
	return []procSpec{
		{pid: 100, comm: "plain", cmdline: "/usr/sbin/plain\x00-a\x00b\x00", rssKB: 10, pssKB: 5, start: 100},
		{pid: 101, comm: "bad\xffutf8", cmdline: "\xff\xfe/bin/bad\xc3\x28 x", rssKB: 10, pssKB: 5, start: 100},
		{pid: 102, comm: "ctl\x01\x1b[31m", cmdline: "\x01\x02\x1b]0;title\x07 y\x00", rssKB: 10, pssKB: 5, start: 100},
		{pid: 103, comm: "empty", cmdline: "", rssKB: 10, pssKB: 5, start: 100},
		{pid: 104, comm: "", cmdline: "", rssKB: 10, pssKB: 5, start: 100},
		{pid: 105, comm: "long", cmdline: longControlLine("/opt/" + strings.Repeat("d", 2000) + "/x"), rssKB: 10, pssKB: 5, start: 100},
		{pid: 106, comm: strings.Repeat("c", 15), cmdline: "[kworker/0:1-events]", rssKB: 10, pssKB: 5, start: 100},
		{pid: 107, comm: "e€", cmdline: strings.Repeat("€", 700), rssKB: 10, pssKB: 5, start: 100},
	}
}

func TestCommandReader_MatchesProcessSnapshotNames(t *testing.T) {
	t.Run("captured processes", commandsMatchCapturedProcesses)

	t.Run("hostile names", commandsMatchHostileNames)

	t.Run("reused buffers leak nothing between processes", commandsDoNotLeakBetweenProcesses)
}

func commandsMatchCapturedProcesses(t *testing.T) {
	tr := newTree(t)
	c, _ := newCollector(tr, nil)
	snap := snapshotOf(t, c, runStart, true)
	r := NewCommandReader(tr.proc)

	if len(snap.Processes) != len(fixtureProcs) {
		t.Fatalf("snapshot holds %d processes, want %d", len(snap.Processes), len(fixtureProcs))
	}
	for _, p := range snap.Processes {
		got, err := r.Command(p.PID)
		if err != nil || got != p.Command {
			t.Errorf("Command(%d) = %.80q, %v, want %.80q, nil (the snapshot's command)", p.PID, got, err, p.Command)
		}
	}
}

func commandsMatchHostileNames(t *testing.T) {
	tr := synthTree()
	for _, s := range hostileSpecs() {
		tr.add(s)
	}
	c, _ := newCollector(tr, nil)
	snap := snapshotOf(t, c, runStart, true)
	r := NewCommandReader(tr.proc)

	if len(snap.Processes) != len(hostileSpecs()) {
		t.Fatalf("snapshot holds %d processes, want %d", len(snap.Processes), len(hostileSpecs()))
	}
	for _, p := range snap.Processes {
		got, err := r.Command(p.PID)
		if err != nil || got != p.Command {
			t.Errorf("Command(%d) = %.80q, %v, want %.80q, nil (the snapshot's command)", p.PID, got, err, p.Command)
		}
	}
}

func commandsDoNotLeakBetweenProcesses(t *testing.T) {
	tr := synthTree()
	for _, s := range hostileSpecs() {
		tr.add(s)
	}
	c, _ := newCollector(tr, nil)
	snap := snapshotOf(t, c, runStart, true)
	r := NewCommandReader(tr.proc)

	// A long command line first, then the short ones, then the long one again.
	order := []int32{105, 103, 100, 104, 105, 107, 103}
	want := map[int32]string{}
	for _, p := range snap.Processes {
		want[p.PID] = p.Command
	}
	for _, pid := range order {
		got, err := r.Command(pid)
		if err != nil || got != want[pid] {
			t.Errorf("Command(%d) = %.80q, %v, want %.80q, nil", pid, got, err, want[pid])
		}
	}
}

func TestCommandReader_Command_Errors(t *testing.T) {
	t.Run("a missing process has ended", commandOfMissingProcessEnded)

	t.Run("a process that ends between its files has ended", commandOfProcessEndingBetweenFiles)

	t.Run("a refused file is an error and not an ended process", commandOfRefusedFile)

	t.Run("a malformed stat is an error and not an ended process", commandOfMalformedStat)
}

func commandOfMissingProcessEnded(t *testing.T) {
	r := NewCommandReader(synthTree().proc)

	got, err := r.Command(4242)
	if err == nil || got != "" {
		t.Fatalf("Command(4242) = %q, %v, want an empty command and an error", got, err)
	}
	if !Ended(err) {
		t.Errorf("Ended(%v) = false, want true", err)
	}
}

func commandOfProcessEndingBetweenFiles(t *testing.T) {
	tr := synthTree()
	tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1})
	delete(tr.procFiles, fileOf(7, "cmdline"))
	r := NewCommandReader(tr.proc)

	_, err := r.Command(7)
	if err == nil || !Ended(err) {
		t.Errorf("Command(7) error = %v, Ended = %v, want an error that says the process ended", err, Ended(err))
	}
}

func commandOfRefusedFile(t *testing.T) {
	for _, name := range []string{"stat", "cmdline"} {
		tr := synthTree()
		tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1})
		tr.proc.openErr[fileOf(7, name)] = syscall.EACCES
		r := NewCommandReader(tr.proc)

		got, err := r.Command(7)
		if err == nil || got != "" || Ended(err) {
			t.Errorf("Command(7) with %s refused = %q, %v (Ended %v), want an empty command and an error that is not an ended process", name, got, err, Ended(err))
		}
		if !errors.Is(err, syscall.EACCES) {
			t.Errorf("Command(7) with %s refused error = %v, want it to wrap EACCES", name, err)
		}
	}
}

func commandOfMalformedStat(t *testing.T) {
	tr := synthTree()
	tr.add(procSpec{pid: 7, comm: "x", cmdline: "x", start: 1})
	setFile(tr.procFiles, fileOf(7, "stat"), "garbage without parentheses\n")
	r := NewCommandReader(tr.proc)

	got, err := r.Command(7)
	if err == nil || got != "" || Ended(err) {
		t.Errorf("Command(7) = %q, %v (Ended %v), want an empty command and an error that is not an ended process", got, err, Ended(err))
	}
}

func TestEnded(t *testing.T) {
	tests := []struct {
		name string
		err  error
		want bool
	}{
		{"nil", nil, false},
		{"not exist", fs.ErrNotExist, true},
		{"ENOENT", syscall.ENOENT, true},
		{"ESRCH", syscall.ESRCH, true},
		{"wrapped path error", fmt.Errorf("procstats: 7/stat: %w", &fs.PathError{Op: "open", Path: "7/stat", Err: syscall.ENOENT}), true},
		{"wrapped ESRCH", fmt.Errorf("procstats: 7/cmdline: %w", syscall.ESRCH), true},
		{"EACCES", syscall.EACCES, false},
		{"EPERM", syscall.EPERM, false},
		{"permission", fs.ErrPermission, false},
		{"other", errors.New("boom"), false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := Ended(tt.err); got != tt.want {
				t.Errorf("Ended(%v) = %v, want %v", tt.err, got, tt.want)
			}
		})
	}
}

func TestListPIDs_MatchesTheProcessCollector(t *testing.T) {
	t.Run("captured processes", listPIDsOfCapturedProcesses)

	t.Run("more than MaxProcesses and directories that are no PID", listPIDsBeyondMaxProcesses)

	t.Run("a directory that cannot be listed fails", listPIDsOfUnlistableDirectory)
}

func listPIDsOfCapturedProcesses(t *testing.T) {
	tr := newTree(t)
	want, wantSeen, wantErr := listPIDs(tr.proc)
	if wantErr != nil || len(want) != len(fixtureProcs) {
		t.Fatalf("test setup: listPIDs() = %v, %d, %v, want the %d captured processes", want, wantSeen, wantErr, len(fixtureProcs))
	}

	got, seen, err := ListPIDs(tr.proc)
	if err != nil || seen != wantSeen || !slices.Equal(got, want) {
		t.Errorf("ListPIDs() = %v, %d, %v, want %v, %d, nil", got, seen, err, want, wantSeen)
	}
	if !slices.IsSorted(got) {
		t.Errorf("ListPIDs() = %v, want ascending order", got)
	}
}

func listPIDsBeyondMaxProcesses(t *testing.T) {
	const processes = 4100
	tr := synthTree()
	for pid := int32(1); pid <= processes; pid++ {
		setFile(tr.procFiles, fileOf(pid, "stat"), "x")
	}
	for _, name := range []string{"0123", "self", "thread-self", "0", "+5", "5x", "2147483648", "99999999999", "net"} {
		setFile(tr.procFiles, name+"/stat", "x")
	}

	got, seen, err := ListPIDs(tr.proc)
	if err != nil || seen != processes || len(got) != MaxProcesses || !slices.IsSorted(got) || got[0] != 1 || got[len(got)-1] != MaxProcesses {
		t.Errorf("ListPIDs() = %d PIDs, seen %d, %v, want the PIDs 1 to %d in ascending order, seen %d and nil", len(got), seen, err, MaxProcesses, processes)
	}
	want, wantSeen, _ := listPIDs(tr.proc)
	if seen != wantSeen || !slices.Equal(got, want) {
		t.Errorf("ListPIDs() differs from the process collector's listing: %d PIDs seen %d, want %d PIDs seen %d", len(got), seen, len(want), wantSeen)
	}
}

func listPIDsOfUnlistableDirectory(t *testing.T) {
	tr := synthTree()
	tr.proc.openErr["."] = syscall.EACCES

	got, seen, err := ListPIDs(tr.proc)
	if err == nil || len(got) != 0 || seen != 0 {
		t.Errorf("ListPIDs() = %v, %d, %v, want no PIDs, 0 and an error", got, seen, err)
	}
	if !errors.Is(err, syscall.EACCES) {
		t.Errorf("ListPIDs() error = %v, want it to wrap EACCES", err)
	}
}
