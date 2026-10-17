package sysstats

import (
	"errors"
	"fmt"
	"io/fs"
	"strconv"
	"strings"
	"syscall"
)

// maxPressureBytes is the most of a pressure file that is read.
const maxPressureBytes = 256

// maxPressureHundredths is 100.00, the largest average, in hundredths.
const maxPressureHundredths = 10000

// pressureResources are the resources of /proc/pressure in record order.
var pressureResources = []string{"memory", "cpu", "io"}

// pressureKinds are the kinds of a pressure file in line order.
var pressureKinds = []string{"some", "full"}

// pressureLine is one line of a pressure file.
type pressureLine struct {
	kind  string
	avg   [3]float64 // avg10, avg60, avg300
	total uint64     // microseconds
}

// pressureFile is the content of one pressure file.
type pressureFile struct {
	resource string
	lines    []pressureLine
}

// readPressure reads the pressure files. A resource whose file does not exist or whose read answers "operation not
// supported" is returned in unavailable and has no file; any other error fails.
func readPressure(proc fs.FS) (files []pressureFile, unavailable []string, err error) {
	for _, resource := range pressureResources {
		file, err := readPressureFile(proc, resource)
		switch {
		case errors.Is(err, fs.ErrNotExist) || errors.Is(err, syscall.EOPNOTSUPP):
			unavailable = append(unavailable, resource)
		case err != nil:
			return nil, nil, err
		default:
			files = append(files, file)
		}
	}
	return files, unavailable, nil
}

// readPressureFile reads and checks /proc/pressure/<resource>.
func readPressureFile(proc fs.FS, resource string) (pressureFile, error) {
	name := "pressure/" + resource
	file := pressureFile{resource: resource}
	data, err := readFile(proc, name, maxPressureBytes)
	if err != nil {
		return file, err
	}
	lines := splitLines(data)
	if len(lines) == 0 {
		return file, fmt.Errorf("sysstats: %s: no some line", name)
	}
	for i, line := range lines {
		if i >= len(pressureKinds) {
			return file, notPressureLine(name, i+1)
		}
		l, ok := parsePressureLine(line, pressureKinds[i])
		if !ok {
			return file, notPressureLine(name, i+1)
		}
		file.lines = append(file.lines, l)
	}
	return file, nil
}

// notPressureLine is the error of line n of the pressure file name.
func notPressureLine(name string, n int) error {
	return fmt.Errorf("sysstats: %s: line %d: not a pressure line", name, n)
}

// parsePressureLine parses "<kind> avg10=A avg60=A avg300=A total=T" with single spaces.
func parsePressureLine(line, kind string) (pressureLine, bool) {
	l := pressureLine{kind: kind}
	parts := strings.Split(line, " ")
	if len(parts) != 5 || parts[0] != kind {
		return l, false
	}
	for i, key := range []string{"avg10=", "avg60=", "avg300="} {
		text, ok := strings.CutPrefix(parts[i+1], key)
		if !ok {
			return l, false
		}
		avg, ok := parseAverage(text)
		if !ok {
			return l, false
		}
		l.avg[i] = avg
	}
	text, ok := strings.CutPrefix(parts[4], "total=")
	if !ok {
		return l, false
	}
	l.total, ok = parseCounter(text)
	return l, ok
}

// parseAverage parses 1 to 3 ASCII digits, a point and 2 digits, at most 100.00.
func parseAverage(s string) (float64, bool) {
	whole, frac, found := strings.Cut(s, ".")
	if !found || len(whole) < 1 || len(whole) > 3 || len(frac) != 2 || !allDigits(whole) || !allDigits(frac) {
		return 0, false
	}
	hundredths, err := strconv.Atoi(whole + frac)
	if err != nil || hundredths > maxPressureHundredths {
		return 0, false
	}
	v, err := strconv.ParseFloat(s, 64)
	return v, err == nil
}

// addTo adds the records of the file: per line its three averages and its total.
func (f pressureFile) addTo(r *recorder) {
	for _, l := range f.lines {
		r.add("pressure_avg10", l.avg[0], "percent", "resource", f.resource, "kind", l.kind)
		r.add("pressure_avg60", l.avg[1], "percent", "resource", f.resource, "kind", l.kind)
		r.add("pressure_avg300", l.avg[2], "percent", "resource", f.resource, "kind", l.kind)
		r.add("pressure_stall_microseconds_total", float64(l.total), "microseconds", "resource", f.resource, "kind", l.kind)
	}
}
