package procstats

// parseSmapsRollup parses the content of /proc/<pid>/smaps_rollup into the resident and proportional set size in bytes.
func parseSmapsRollup(data string) (rss, pss uint64, err error) {
	return 0, 0, errNotImplemented
}
