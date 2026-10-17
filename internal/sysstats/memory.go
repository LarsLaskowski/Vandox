package sysstats

import (
	"errors"
	"fmt"
	"io/fs"
	"strings"
)

// Limits of the memory readers.
const (
	maxMeminfoBytes = 16384
	maxMeminfoLines = 128
	maxMeminfoName  = 64
	maxVmstatBytes  = 65536
	maxKBValue      = (1<<64 - 1) / 1024
)

// vmstatCounters are the counters of /proc/vmstat that are recorded, in record order.
var vmstatCounters = [4]string{"pswpin", "pswpout", "pgmajfault", "oom_kill"}

// requiredMemFields are the meminfo fields every sample needs, as kB lines.
var requiredMemFields = []string{"MemTotal", "MemFree", "MemAvailable", "Buffers", "Cached", "SReclaimable", "SwapTotal", "SwapFree"}

// memLine is one line of /proc/meminfo.
type memLine struct {
	name  string
	value uint64 // as written, of a line without unit
	kB    bool   // whether the line has the unit kB
	kBVal int64  // the value of a kB line, at most maxKBValue
}

// memory is the content of /proc/meminfo.
type memory struct {
	lines  []memLine
	values map[string]int64 // the kB lines by name
}

// readMeminfo reads and checks /proc/meminfo.
func readMeminfo(proc fs.FS) (*memory, error) {
	data, err := readFile(proc, "meminfo", maxMeminfoBytes)
	if err != nil {
		return nil, err
	}
	lines := splitLines(data)
	if len(lines) > maxMeminfoLines {
		return nil, fmt.Errorf("sysstats: meminfo: more than %d lines", maxMeminfoLines)
	}
	m := &memory{values: make(map[string]int64, len(lines))}
	seen := make(map[string]bool, len(lines))
	for i, line := range lines {
		l, ok := parseMemLine(line)
		if !ok {
			return nil, fmt.Errorf("sysstats: meminfo: line %d: not a meminfo line", i+1)
		}
		if seen[l.name] {
			return nil, fmt.Errorf("sysstats: meminfo: line %d: repeated field", i+1)
		}
		seen[l.name] = true
		m.lines = append(m.lines, l)
		if l.kB {
			m.values[l.name] = l.kBVal
		}
	}
	for _, field := range requiredMemFields {
		if _, ok := m.values[field]; !ok {
			return nil, fmt.Errorf("sysstats: meminfo: no %s value in kB", field)
		}
	}
	return m, nil
}

// parseMemLine parses "Name:", one or more spaces, 1 to 20 digits and optionally " kB".
func parseMemLine(line string) (memLine, bool) {
	name, rest, found := strings.Cut(line, ":")
	if !found || !validMemName(name) {
		return memLine{}, false
	}
	value := strings.TrimLeft(rest, " ")
	if len(value) == len(rest) {
		return memLine{}, false
	}
	end := 0
	for end < len(value) && value[end] >= '0' && value[end] <= '9' {
		end++
	}
	l := memLine{name: name}
	switch value[end:] {
	case "":
	case " kB":
		l.kB = true
	default:
		return memLine{}, false
	}
	v, ok := parseCounter(value[:end])
	if !ok {
		return memLine{}, false
	}
	if l.kB {
		if v > maxKBValue {
			return memLine{}, false
		}
		l.kBVal = int64(v)
		return l, true
	}
	l.value = v
	return l, true
}

// validMemName reports whether name is 1 to 64 characters of A-Z a-z 0-9 _ ( ).
func validMemName(name string) bool {
	if len(name) < 1 || len(name) > maxMeminfoName {
		return false
	}
	for i := 0; i < len(name); i++ {
		c := name[i]
		if (c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '_' && c != '(' && c != ')' {
			return false
		}
	}
	return true
}

// addTo adds the memory, swap and meminfo records, the memory values as top prints them (procps 3.3.17).
func (m *memory) addTo(r *recorder) {
	total, free := m.values["MemTotal"], m.values["MemFree"]
	buffCache := m.values["Buffers"] + m.values["Cached"] + m.values["SReclaimable"]
	used := total - free - buffCache
	if used < 0 {
		used = total - free
	}
	r.add("mem_total_bytes", kbBytes(total), "bytes")
	r.add("mem_free_bytes", kbBytes(free), "bytes")
	if used >= 0 {
		r.add("mem_used_bytes", kbBytes(used), "bytes")
	}
	r.add("mem_buff_cache_bytes", kbBytes(buffCache), "bytes")
	r.add("mem_available_bytes", kbBytes(m.values["MemAvailable"]), "bytes")

	swapTotal, swapFree := m.values["SwapTotal"], m.values["SwapFree"]
	r.add("swap_total_bytes", kbBytes(swapTotal), "bytes")
	r.add("swap_free_bytes", kbBytes(swapFree), "bytes")
	if swapFree <= swapTotal {
		r.add("swap_used_bytes", kbBytes(swapTotal-swapFree), "bytes")
	}
	for _, l := range m.lines {
		if l.kB {
			r.add("meminfo_bytes", kbBytes(l.kBVal), "bytes", "field", l.name)
		} else {
			r.add("meminfo", float64(l.value), "", "field", l.name)
		}
	}
}

// kbBytes returns kB kibibytes in bytes.
func kbBytes(kB int64) float64 {
	return float64(kB) * 1024
}

// readVmstat returns the four recorded counters of vmstat.
func readVmstat(proc fs.FS) ([4]uint64, error) {
	var counters [4]uint64
	data, err := readFile(proc, "vmstat", maxVmstatBytes)
	if err != nil {
		return counters, err
	}
	var seen [4]bool
	for i, line := range splitLines(data) {
		name, value, _ := strings.Cut(line, " ")
		idx := vmstatIndex(name)
		if idx < 0 {
			continue
		}
		v, ok := parseCounter(value)
		if !ok {
			return counters, fmt.Errorf("sysstats: vmstat: line %d: not a counter line", i+1)
		}
		if seen[idx] {
			return counters, fmt.Errorf("sysstats: vmstat: line %d: repeated counter", i+1)
		}
		seen[idx], counters[idx] = true, v
	}
	for i, name := range vmstatCounters {
		if !seen[i] {
			return counters, errors.New("sysstats: vmstat: no " + name + " line")
		}
	}
	return counters, nil
}

// vmstatIndex returns the position of name in vmstatCounters, or -1.
func vmstatIndex(name string) int {
	for i, n := range vmstatCounters {
		if n == name {
			return i
		}
	}
	return -1
}
