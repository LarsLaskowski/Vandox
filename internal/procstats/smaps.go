package procstats

import (
	"errors"
	"strings"
)

// maxSmapsBytes is the most of /proc/<pid>/smaps_rollup that is read.
const maxSmapsBytes = 4096

// errSmapsFormat is the reason a smaps_rollup file is refused; it names no content.
var errSmapsFormat = errors.New("not in the expected format")

// parseSmapsRollup parses the content of /proc/<pid>/smaps_rollup into the resident and proportional set size in bytes.
//
// The first line (the [rollup] range) is not read; of the rest only "Rss:" and "Pss:" are, each exactly once.
func parseSmapsRollup(data string) (rss, pss uint64, err error) {
	_, rest, _ := strings.Cut(data, "\n")
	var hasRSS, hasPSS bool
	for rest != "" {
		var line string
		line, rest, _ = strings.Cut(rest, "\n")
		switch {
		case strings.HasPrefix(line, "Rss:"):
			err = setSmaps(line[len("Rss:"):], &rss, &hasRSS)
		case strings.HasPrefix(line, "Pss:"):
			err = setSmaps(line[len("Pss:"):], &pss, &hasPSS)
		}
		if err != nil {
			return 0, 0, err
		}
	}
	if !hasRSS || !hasPSS {
		return 0, 0, errSmapsFormat
	}
	return rss, pss, nil
}

// setSmaps reads a size line after its name: spaces, 1 to 20 digits and " kB". The value in bytes goes to value, and seen must
// not be set yet.
func setSmaps(rest string, value *uint64, seen *bool) error {
	trimmed := strings.TrimLeft(rest, " ")
	if *seen || trimmed == rest {
		return errSmapsFormat
	}
	v, ok := parseKB(trimmed)
	if !ok {
		return errSmapsFormat
	}
	*value, *seen = v, true
	return nil
}
