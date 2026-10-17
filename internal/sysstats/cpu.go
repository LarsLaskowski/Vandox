package sysstats

import (
	"errors"
	"fmt"
	"io"
	"io/fs"
	"strconv"
	"strings"
)

// Limits of the CPU and load readers.
const (
	maxStatLineBytes = 4096
	maxLoadavgBytes  = 256
)

// cpuModes are the modes of cpu_percent in record order and their index in the cpu line of /proc/stat.
var cpuModes = []struct {
	name  string
	index int
}{
	{"user", 0}, {"system", 2}, {"nice", 1}, {"idle", 3}, {"iowait", 4}, {"irq", 5}, {"softirq", 6}, {"steal", 7},
}

// cpuCounters are the first 8 counters of the cpu line of /proc/stat.
type cpuCounters [8]uint64

// readStat returns the counters of the first line of stat, which must be the sum over all CPUs.
func readStat(proc fs.FS) (cpuCounters, error) {
	var counters cpuCounters
	f, err := proc.Open("stat")
	if err != nil {
		return counters, fmt.Errorf("sysstats: stat: %w", err)
	}
	defer func() { _ = f.Close() }()

	data, err := io.ReadAll(io.LimitReader(f, maxStatLineBytes+1))
	if err != nil {
		return counters, fmt.Errorf("sysstats: stat: %w", err)
	}
	line, _, _ := strings.Cut(string(data), "\n")
	if len(line) > maxStatLineBytes {
		return counters, fmt.Errorf("sysstats: stat: first line longer than %d bytes", maxStatLineBytes)
	}
	rest, ok := strings.CutPrefix(line, "cpu ")
	if !ok {
		return counters, errNoCPULine
	}
	fields := strings.Fields(rest)
	if len(fields) < len(counters) {
		return counters, errCPULine
	}
	for i := range counters {
		v, valid := parseCounter(fields[i])
		if !valid {
			return counters, errCPULine
		}
		counters[i] = v
	}
	return counters, nil
}

// errNoCPULine is the error of a stat file whose first line is not the cpu line.
var errNoCPULine = errors.New("sysstats: stat: no cpu line")

// errCPULine is the error of a cpu line that is not 8 or more counters.
var errCPULine = errors.New("sysstats: stat: cpu line: not 8 or more counters")

// readLoadavg returns the three load averages of loadavg.
func readLoadavg(proc fs.FS) ([3]float64, error) {
	var load [3]float64
	data, err := readFile(proc, "loadavg", maxLoadavgBytes)
	if err != nil {
		return load, err
	}
	line, _, _ := strings.Cut(data, "\n")
	fields := strings.Fields(line)
	if len(fields) < len(load) {
		return load, errLoadavg
	}
	for i := range load {
		if !validLoad(fields[i]) {
			return load, errLoadavg
		}
		v, err := strconv.ParseFloat(fields[i], 64)
		if err != nil {
			return load, errLoadavg
		}
		load[i] = v
	}
	return load, nil
}

// errLoadavg is the error of a loadavg file that is not a load average line.
var errLoadavg = errors.New("sysstats: loadavg: not a load average line")

// validLoad reports whether s is 1 to 10 ASCII digits, a point and 2 digits.
func validLoad(s string) bool {
	whole, frac, found := strings.Cut(s, ".")
	if !found || len(whole) < 1 || len(whole) > 10 || len(frac) != 2 {
		return false
	}
	return allDigits(whole) && allDigits(frac)
}

// allDigits reports whether s consists of ASCII digits only.
func allDigits(s string) bool {
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}
