package sysstats

import (
	"errors"
	"fmt"
	"io/fs"
	"strings"
)

// Limits of the disk reader.
const (
	maxBlockStatBytes = 1024
	minBlockStatFlds  = 11
	sectorBytes       = 512
)

// diskCounters are the counters of a disk that are recorded.
type diskCounters struct {
	reads, readSectors, writes, writeSectors, ioMillis uint64
}

// diskSample is the counters of one disk.
type diskSample struct {
	name string
	diskCounters
}

// readDisks returns the counters of the disks: the entries of block that have a device entry, at most MaxDisks, in name
// order.
func readDisks(sys fs.FS) ([]diskSample, error) {
	entries, err := fs.ReadDir(sys, "block")
	if err != nil {
		return nil, fmt.Errorf("sysstats: block: %w", err)
	}
	var disks []diskSample
	for _, e := range entries {
		if len(disks) == MaxDisks {
			break
		}
		name := e.Name()
		_, err := fs.Stat(sys, "block/"+name+"/device")
		if errors.Is(err, fs.ErrNotExist) {
			continue
		}
		if err != nil {
			return nil, fmt.Errorf("sysstats: block/%s/device: %w", name, err)
		}
		counters, err := readBlockStat(sys, name)
		if err != nil {
			return nil, err
		}
		disks = append(disks, diskSample{name: name, diskCounters: counters})
	}
	return disks, nil
}

// readBlockStat reads block/<name>/stat: at least 11 fields of ASCII digits.
func readBlockStat(sys fs.FS, name string) (diskCounters, error) {
	file := "block/" + name + "/stat"
	data, err := readFile(sys, file, maxBlockStatBytes)
	if err != nil {
		return diskCounters{}, err
	}
	fields := strings.Fields(data)
	if len(fields) < minBlockStatFlds {
		return diskCounters{}, notBlockStat(file)
	}
	values := make([]uint64, len(fields))
	for i, f := range fields {
		v, ok := parseCounter(f)
		if !ok {
			return diskCounters{}, notBlockStat(file)
		}
		values[i] = v
	}
	return diskCounters{reads: values[0], readSectors: values[2], writes: values[4], writeSectors: values[6], ioMillis: values[9]}, nil
}

// notBlockStat is the error of a stat file that is not a block device stat line.
func notBlockStat(file string) error {
	return fmt.Errorf("sysstats: %s: not a block device stat line", file)
}

// addTo adds the five counters of the disk and, with a predecessor and the seconds dt between the samples, its five rates.
func (d diskSample) addTo(r *recorder, prev *diskCounters, dt float64) {
	r.add("disk_reads_total", float64(d.reads), "", "device", d.name)
	r.add("disk_read_bytes_total", float64(d.readSectors)*sectorBytes, "bytes", "device", d.name)
	r.add("disk_writes_total", float64(d.writes), "", "device", d.name)
	r.add("disk_written_bytes_total", float64(d.writeSectors)*sectorBytes, "bytes", "device", d.name)
	r.add("disk_io_time_seconds_total", float64(d.ioMillis)/1000, "seconds", "device", d.name)
	if prev == nil {
		return
	}
	rates := []struct {
		name, unit string
		cur, prev  uint64
		scale      float64
	}{
		{"disk_reads_per_second", "per_second", d.reads, prev.reads, 1},
		{"disk_read_bytes_per_second", "bytes_per_second", d.readSectors, prev.readSectors, sectorBytes},
		{"disk_writes_per_second", "per_second", d.writes, prev.writes, 1},
		{"disk_written_bytes_per_second", "bytes_per_second", d.writeSectors, prev.writeSectors, sectorBytes},
		{"disk_busy_percent", "percent", d.ioMillis, prev.ioMillis, 0.1},
	}
	for _, x := range rates {
		if v, ok := rate(x.cur, x.prev, dt); ok {
			r.add(x.name, v*x.scale, x.unit, "device", d.name)
		}
	}
}

// rate returns the increase from prev to cur per second over dt seconds; there is none when the counter fell.
func rate(cur, prev uint64, dt float64) (float64, bool) {
	if cur < prev {
		return 0, false
	}
	return float64(cur-prev) / dt, true
}
