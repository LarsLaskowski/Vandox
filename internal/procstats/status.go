package procstats

import (
	"errors"
	"math"
	"strconv"
	"strings"
)

// maxStatusBytes is the most of /proc/<pid>/status that is read.
const maxStatusBytes = 16384

// errStatusFormat is the reason a status file is refused; it names no content.
var errStatusFormat = errors.New("not in the expected format")

// statusFields are the fields of /proc/<pid>/status the collector uses.
type statusFields struct {
	euid            uint32
	rss, swap       uint64 // bytes
	hasRSS, hasSwap bool
}

// parseStatus parses the content of /proc/<pid>/status.
//
// Only the lines that start with "Uid:", "VmRSS:" and "VmSwap:" are read, each at most once. The kernel escapes a line feed
// in the name, so no name can start such a line.
func parseStatus(data string) (statusFields, error) {
	var sf statusFields
	hasUID := false
	for data != "" {
		var line string
		line, data, _ = strings.Cut(data, "\n")
		var err error
		switch {
		case strings.HasPrefix(line, "Uid:"):
			err = sf.setUID(line[len("Uid:"):], &hasUID)
		case strings.HasPrefix(line, "VmRSS:"):
			err = setKB(line[len("VmRSS:"):], &sf.rss, &sf.hasRSS)
		case strings.HasPrefix(line, "VmSwap:"):
			err = setKB(line[len("VmSwap:"):], &sf.swap, &sf.hasSwap)
		}
		if err != nil {
			return statusFields{}, err
		}
	}
	if !hasUID {
		return statusFields{}, errStatusFormat
	}
	return sf, nil
}

// setUID reads the values of a Uid line after its name: a tab and four tab-separated decimal numbers within 32 bits, the
// second of which is the effective UID.
func (sf *statusFields) setUID(rest string, seen *bool) error {
	values, ok := strings.CutPrefix(rest, "\t")
	parts := strings.Split(values, "\t")
	if *seen || !ok || len(parts) != 4 {
		return errStatusFormat
	}
	*seen = true
	for i, part := range parts {
		if !isDigits(part) {
			return errStatusFormat
		}
		v, err := strconv.ParseUint(part, 10, 32)
		if err != nil || v > math.MaxUint32 {
			return errStatusFormat
		}
		if i == 1 {
			sf.euid = uint32(v)
		}
	}
	return nil
}

// setKB reads a size line after its name: spaces or tabs, 1 to 20 digits and " kB". The value in bytes goes to value, and
// seen must not be set yet.
func setKB(rest string, value *uint64, seen *bool) error {
	if *seen {
		return errStatusFormat
	}
	v, ok := parseKB(strings.TrimLeft(rest, " \t"))
	if !ok {
		return errStatusFormat
	}
	*value, *seen = v, true
	return nil
}

// parseKB parses "<digits> kB" into bytes; it fails when the value times 1,024 does not fit 64 bits.
func parseKB(s string) (uint64, bool) {
	digits, unit, found := strings.Cut(s, " ")
	if !found || unit != "kB" || !isDigits(digits) {
		return 0, false
	}
	v, err := strconv.ParseUint(digits, 10, 64)
	if err != nil || v > math.MaxUint64/1024 {
		return 0, false
	}
	return v * 1024, true
}
