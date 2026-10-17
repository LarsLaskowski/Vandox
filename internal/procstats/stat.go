package procstats

import (
	"errors"
	"math"
	"strconv"
	"strings"
)

// maxStatBytes is the most of /proc/<pid>/stat that is read.
const maxStatBytes = 4096

// statFieldsNeeded is the number of fields after comm the collector needs: field 3 (state) to field 22 (starttime).
const statFieldsNeeded = 20

// errStatFormat is the reason a stat file is refused; it names no content.
var errStatFormat = errors.New("not in the expected format")

// statFields are the fields of /proc/<pid>/stat the collector uses.
type statFields struct {
	pid, ppid               int32
	comm                    string
	state                   byte
	flags                   uint32
	utime, stime, starttime uint64
}

// parseStat parses the content of /proc/<pid>/stat of process pid.
//
// The line is "<pid> (<comm>) <state> <ppid> ..." where comm may hold any byte but NUL, also parentheses, spaces and line
// feeds: it ends at the last closing parenthesis of the file, so a name cannot imitate the fields after it.
func parseStat(pid int32, data string) (statFields, error) {
	prefix := strconv.Itoa(int(pid)) + " ("
	if pid <= 0 || !strings.HasPrefix(data, prefix) {
		return statFields{}, errStatFormat
	}
	end := strings.LastIndexByte(data, ')')
	if end < len(prefix) || end+1 >= len(data) || data[end+1] != ' ' {
		return statFields{}, errStatFormat
	}
	comm := data[len(prefix):end]
	if strings.IndexByte(comm, 0) >= 0 {
		return statFields{}, errStatFormat
	}
	fields := strings.Split(strings.TrimSuffix(data[end+2:], "\n"), " ")
	if len(fields) < statFieldsNeeded {
		return statFields{}, errStatFormat
	}
	return statFieldsOf(pid, comm, fields)
}

// statFieldsOf reads the numbers of the fields after comm: state is field 3, ppid 4, flags 9, utime 14, stime 15, starttime 22.
func statFieldsOf(pid int32, comm string, fields []string) (statFields, error) {
	sf := statFields{pid: pid, comm: comm}
	if len(fields[0]) != 1 {
		return statFields{}, errStatFormat
	}
	sf.state = fields[0][0]
	if !isDigits(fields[1]) || !isDigits(fields[6]) || !isDigits(fields[11]) || !isDigits(fields[12]) || !isDigits(fields[19]) {
		return statFields{}, errStatFormat
	}
	ppid, err := strconv.ParseInt(fields[1], 10, 32)
	if err != nil || ppid < 0 || ppid > math.MaxInt32 {
		return statFields{}, errStatFormat
	}
	sf.ppid = int32(ppid)
	flags, err := strconv.ParseUint(fields[6], 10, 32)
	if err != nil || flags > math.MaxUint32 {
		return statFields{}, errStatFormat
	}
	sf.flags = uint32(flags)
	if sf.utime, err = strconv.ParseUint(fields[11], 10, 64); err != nil {
		return statFields{}, errStatFormat
	}
	if sf.stime, err = strconv.ParseUint(fields[12], 10, 64); err != nil {
		return statFields{}, errStatFormat
	}
	if sf.starttime, err = strconv.ParseUint(fields[19], 10, 64); err != nil {
		return statFields{}, errStatFormat
	}
	return sf, nil
}

// isDigits reports whether s is 1 to 20 ASCII digits.
func isDigits(s string) bool {
	if len(s) < 1 || len(s) > 20 {
		return false
	}
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}
