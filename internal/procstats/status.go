package procstats

// statusFields are the fields of /proc/<pid>/status the collector uses.
type statusFields struct {
	euid            uint32
	rss, swap       uint64 // bytes
	hasRSS, hasSwap bool
}

// parseStatus parses the content of /proc/<pid>/status.
func parseStatus(data string) (statusFields, error) {
	return statusFields{}, errNotImplemented
}
