package procstats

import (
	"io/fs"
	"strconv"
)

// ListPIDs returns the lowest PIDs of proc in ascending order and the number of PIDs seen, as the process collector lists them.
func ListPIDs(proc fs.FS) (pids []int32, seen int, err error) {
	return listPIDs(proc)
}

// CommandReader derives the command of a process from its files with the rules of the process collector.
type CommandReader struct {
	r *reader
}

// NewCommandReader returns a reader over proc.
func NewCommandReader(proc fs.FS) *CommandReader {
	return &CommandReader{r: newReader(proc)}
}

// Command returns the command of pid; it is not safe for concurrent use.
func (c *CommandReader) Command(pid int32) (string, error) {
	dir := strconv.Itoa(int(pid)) + "/"
	st, err := c.r.stat(dir, pid)
	if err != nil {
		return "", err
	}
	cmdline, _, err := c.r.cmdline(dir)
	if err != nil {
		return "", err
	}
	comm, _ := cleanText(st.comm, maxTextBytes)
	return commandFor(cmdline, comm), nil
}

// Ended reports whether err says that the process ended (ENOENT, ESRCH).
func Ended(err error) bool {
	return hasEnded(err)
}
