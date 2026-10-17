package procstats

// statFields are the fields of /proc/<pid>/stat the collector uses.
type statFields struct {
	pid, ppid               int32
	comm                    string
	state                   byte
	flags                   uint32
	utime, stime, starttime uint64
}

// parseStat parses the content of /proc/<pid>/stat of process pid.
func parseStat(pid int32, data string) (statFields, error) {
	return statFields{}, errNotImplemented
}
