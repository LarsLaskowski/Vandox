package sysstats_test

import (
	"os"
	"strconv"
	"strings"
	"testing"
	"testing/fstest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

// diskSpec is the expected content of the disk records of one sample.
type diskSpec struct {
	name     string
	counters [5]float64 // reads, read bytes, writes, written bytes, seconds with I/O in flight
	rates    []float64  // reads/s, read bytes/s, writes/s, written bytes/s, busy percent; nil: no rates
}

// sampleSpec is what varies between the expected system samples of the tests.
type sampleSpec struct {
	cpu    []float64 // cpu_percent in the order of cpuModes; nil: none
	vm     [4]float64
	vmRate []float64 // per second, in the order pswpin, pswpout, pgmajfault, oom_kill; nil: none
	disks  []diskSpec
}

var vmCounters = []string{"pswpin", "pswpout", "pgmajfault", "oom_kill"}

// baseSpec is the first sample of the fixture tree.
func baseSpec() sampleSpec {
	return sampleSpec{
		vm: [4]float64{0, 0, 7795, 0},
		disks: []diskSpec{{
			name:     "vda",
			counters: [5]float64{vdaReads, vdaReadSectors * 512, vdaWrites, vdaWriteSectors * 512, vdaIOMilliSecond / 1000.0},
		}},
	}
}

// raisedStat and raisedSpec are the counters 30 seconds after the fixture: the CPU counters grew by 3000 in total, pswpin by 60,
// pgmajfault by 90, oom_kill by 3, and vda read 300 times, wrote 600 times and was busy for 1500 ms.
func raisedStat() string { return statText(14873, 1074218, 9433, 6449752, 8264) }

func raisedSpec() sampleSpec {
	return sampleSpec{
		cpu:    []float64{10, 5, 1, 80, 2, 0, 1, 1},
		vm:     [4]float64{60, 0, 7885, 3},
		vmRate: []float64{2, 0, 3, 0.1},
		disks: []diskSpec{{
			name:     "vda",
			counters: [5]float64{14873, 1074218 * 512, 9433, 6449752 * 512, 8.264},
			rates:    []float64{10, 6000 * 512 / 30, 20, 12000 * 512 / 30, 5},
		}},
	}
}

// applyRaised turns the fixture tree into the one of raisedSpec.
func applyRaised(t *testing.T, tr *tree) {
	t.Helper()

	edit(t, tr.proc, "stat", fixtureCPU, "cpu  28410 30 10382 891661 579 0 1252 1594 0 0")
	edit(t, tr.proc, "vmstat", "\npswpin 0\n", "\npswpin 60\n")
	edit(t, tr.proc, "vmstat", "\npgmajfault 7795\n", "\npgmajfault 7885\n")
	edit(t, tr.proc, "vmstat", "\noom_kill 0\n", "\noom_kill 3\n")
	set(tr.sys, "block/vda/stat", raisedStat())
}

// meminfoRecords returns the expected records of every line of the fixture's meminfo.
func meminfoRecords(t *testing.T) []wantRec {
	t.Helper()

	data, err := os.ReadFile(procDir + "/meminfo")
	if err != nil {
		t.Fatalf("reading the fixture meminfo: %v", err)
	}
	var out []wantRec
	for _, line := range strings.Split(strings.TrimSuffix(string(data), "\n"), "\n") {
		fields := strings.Fields(line)
		value, err := strconv.ParseFloat(fields[1], 64)
		if err != nil {
			t.Fatalf("fixture meminfo line %q: %v", line, err)
		}
		name := strings.TrimSuffix(fields[0], ":")
		if len(fields) == 3 && fields[2] == "kB" {
			out = append(out, rec("meminfo_bytes", value*kib, "bytes", "field", name))
		} else {
			out = append(out, rec("meminfo", value, "", "field", name))
		}
	}
	if len(out) != fixtureLines {
		t.Fatalf("fixture meminfo has %d lines, want %d", len(out), fixtureLines)
	}
	return out
}

// psiValues is a pressure file line of the fixture.
type psiValues struct {
	resource, kind string
	avg            [3]float64
	total          float64
}

// fixturePressure is testdata/proc/pressure/{memory,cpu,io}, in the order of the records.
var fixturePressure = []psiValues{
	{"memory", "some", [3]float64{0, 0, 0}, 0},
	{"memory", "full", [3]float64{0, 0, 0}, 0},
	{"cpu", "some", [3]float64{0.12, 0.93, 0.39}, 41802781},
	{"cpu", "full", [3]float64{0, 0, 0}, 0},
	{"io", "some", [3]float64{0, 0.01, 0}, 1941688},
	{"io", "full", [3]float64{0, 0, 0}, 1671808},
}

// wantSystem returns the records of a system sample of the fixture tree, varied by s, in the order of the Agent area.
func wantSystem(t *testing.T, s sampleSpec) []wantRec {
	t.Helper()

	var out []wantRec
	for i, v := range s.cpu {
		out = append(out, rec("cpu_percent", v, "percent", "mode", cpuModes[i]))
	}
	out = append(out,
		rec("load1", 0, ""), rec("load5", 0.02, ""), rec("load15", 0.07, ""),
		rec("mem_total_bytes", memTotalKB*kib, "bytes"),
		rec("mem_free_bytes", memFreeKB*kib, "bytes"),
		rec("mem_used_bytes", (memTotalKB-memFreeKB-buffersKB-cachedKB-sreclaimKB)*kib, "bytes"),
		rec("mem_buff_cache_bytes", (buffersKB+cachedKB+sreclaimKB)*kib, "bytes"),
		rec("mem_available_bytes", memAvailKB*kib, "bytes"),
		rec("swap_total_bytes", 0, "bytes"), rec("swap_free_bytes", 0, "bytes"), rec("swap_used_bytes", 0, "bytes"))
	out = append(out, meminfoRecords(t)...)
	for _, p := range fixturePressure {
		for i, name := range []string{"pressure_avg10", "pressure_avg60", "pressure_avg300"} {
			out = append(out, rec(name, p.avg[i], "percent", "resource", p.resource, "kind", p.kind))
		}
		out = append(out, rec("pressure_stall_microseconds_total", p.total, "microseconds", "resource", p.resource, "kind", p.kind))
	}
	for i, name := range vmCounters {
		out = append(out, rec("vmstat_total", s.vm[i], "", "counter", name))
	}
	for i, v := range s.vmRate {
		out = append(out, rec("vmstat_per_second", v, "per_second", "counter", vmCounters[i]))
	}
	for _, d := range s.disks {
		out = append(out, diskRecords(d)...)
	}
	return out
}

// diskRecords returns the expected records of one disk.
func diskRecords(d diskSpec) []wantRec {
	out := []wantRec{
		rec("disk_reads_total", d.counters[0], "", "device", d.name),
		rec("disk_read_bytes_total", d.counters[1], "bytes", "device", d.name),
		rec("disk_writes_total", d.counters[2], "", "device", d.name),
		rec("disk_written_bytes_total", d.counters[3], "bytes", "device", d.name),
		rec("disk_io_time_seconds_total", d.counters[4], "seconds", "device", d.name),
	}
	if d.rates == nil {
		return out
	}
	return append(out,
		rec("disk_reads_per_second", d.rates[0], "per_second", "device", d.name),
		rec("disk_read_bytes_per_second", d.rates[1], "bytes_per_second", "device", d.name),
		rec("disk_writes_per_second", d.rates[2], "per_second", "device", d.name),
		rec("disk_written_bytes_per_second", d.rates[3], "bytes_per_second", "device", d.name),
		rec("disk_busy_percent", d.rates[4], "percent", "device", d.name))
}

func fixtureSystemSources() sysstats.Sources {
	return sysstats.Sources{Proc: os.DirFS(procDir), Sys: os.DirFS(sysDir)}
}

func TestSystem_RecordsFixtureTree(t *testing.T) {
	c := sysstats.NewSystem(fixtureSystemSources(), nil)

	got := mustCollect(t, c, runStart)

	requireRecords(t, runStart, got, wantSystem(t, baseSpec()))
	if hasPrefix(got, "cpu_percent") {
		t.Errorf("the first sample holds cpu_percent records, want none without a predecessor")
	}
}

func TestSystem_RecordsPassTheWireCheck(t *testing.T) {
	c := sysstats.NewSystem(fixtureSystemSources(), nil)

	got := mustCollect(t, c, runStart)

	if len(got) == 0 {
		t.Fatalf("Collect() returned no records, want the system sample")
	}
	for i, r := range got {
		if r.Kind() != model.KindMetric {
			t.Errorf("record %d is a %q, want a metric", i, r.Kind())
		}
		requireStamp(t, i, r, runStart)
	}
}

func TestSystem_RatesBetweenSamples(t *testing.T) {
	tr := loadTree(t)
	c := sysstats.NewSystem(tr.sources(), nil)
	first := mustCollect(t, c, runStart)
	requireRecords(t, runStart, first, wantSystem(t, baseSpec()))
	applyRaised(t, tr)
	second := runStart.Add(30 * time.Second)

	got := mustCollect(t, c, second)

	requireRecords(t, second, got, wantSystem(t, raisedSpec()))
}

func TestSystem_NoRateWithoutPredecessor(t *testing.T) {
	noRates := func(s sampleSpec) sampleSpec {
		s.cpu, s.vmRate = nil, nil
		s.disks = []diskSpec{{name: "vda", counters: s.disks[0].counters}}
		return s
	}
	cases := []struct {
		name   string
		mutate func(t *testing.T, tr *tree)
		start  time.Time
		spec   func() sampleSpec
		gone   func(wantRec) bool
	}{
		{
			name: "a vmstat counter that fell has no rate", start: runStart.Add(30 * time.Second),
			mutate: func(t *testing.T, tr *tree) {
				edit(t, tr.proc, "vmstat", "\npgmajfault 7885\n", "\npgmajfault 7000\n")
			},
			spec: func() sampleSpec { s := raisedSpec(); s.vm[2] = 7000; return s },
			gone: func(w wantRec) bool { return w.name == "vmstat_per_second" && w.labels["counter"] == "pgmajfault" },
		},
		{
			name: "a disk counter that fell has no rate", start: runStart.Add(30 * time.Second),
			mutate: func(_ *testing.T, tr *tree) {
				set(tr.sys, "block/vda/stat", statText(14000, 1074218, 9433, 6449752, 8264))
			},
			spec: func() sampleSpec {
				s := raisedSpec()
				s.disks[0].counters[0] = 14000
				return s
			},
			gone: func(w wantRec) bool { return w.name == "disk_reads_per_second" },
		},
		{
			name: "a cpu mode that fell drops all cpu rates", start: runStart.Add(30 * time.Second),
			mutate: func(t *testing.T, tr *tree) {
				edit(t, tr.proc, "stat", "cpu  28410 30 10382", "cpu  28000 30 10382")
			},
			spec: func() sampleSpec { s := raisedSpec(); s.cpu = nil; return s },
			gone: func(wantRec) bool { return false },
		},
		{
			name: "no increase of the cpu counters gives no cpu rate", start: runStart.Add(30 * time.Second),
			mutate: func(t *testing.T, tr *tree) {
				edit(t, tr.proc, "stat", "cpu  28410 30 10382 891661 579 0 1252 1594 0 0", fixtureCPU)
			},
			spec: func() sampleSpec { s := raisedSpec(); s.cpu = nil; return s },
			gone: func(wantRec) bool { return false },
		},
		{
			name: "a start equal to the previous one gives no rates", start: runStart,
			mutate: func(*testing.T, *tree) {},
			spec:   func() sampleSpec { return noRates(raisedSpec()) },
			gone:   func(wantRec) bool { return false },
		},
		{
			name: "a start before the previous one gives no rates", start: runStart.Add(-30 * time.Second),
			mutate: func(*testing.T, *tree) {},
			spec:   func() sampleSpec { return noRates(raisedSpec()) },
			gone:   func(wantRec) bool { return false },
		},
		{
			name: "a new disk has counters and no rates", start: runStart.Add(30 * time.Second),
			mutate: func(_ *testing.T, tr *tree) { tr.addDisk("vdb", statText(5, 6, 7, 8, 9)) },
			spec: func() sampleSpec {
				s := raisedSpec()
				s.disks = append(s.disks, diskSpec{name: "vdb", counters: [5]float64{5, 6 * 512, 7, 8 * 512, 0.009}})
				return s
			},
			gone: func(wantRec) bool { return false },
		},
	}
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			tr := loadTree(t)
			c := sysstats.NewSystem(tr.sources(), nil)
			mustCollect(t, c, runStart)
			applyRaised(t, tr)
			tt.mutate(t, tr)

			got := mustCollect(t, c, tt.start)

			requireRecords(t, tt.start, got, drop(wantSystem(t, tt.spec()), tt.gone))
		})
	}

	t.Run("a failed sample is not a predecessor", func(t *testing.T) {
		tr := loadTree(t)
		c := sysstats.NewSystem(tr.sources(), nil)
		mustCollect(t, c, runStart)
		applyRaised(t, tr)
		valid := text(t, tr.proc, "meminfo")
		set(tr.proc, "meminfo", valid+"broken\n")
		requireFails(t, c, runStart.Add(30*time.Second), "sysstats: meminfo: line "+strconv.Itoa(fixtureLines+1)+": not a meminfo line")
		set(tr.proc, "meminfo", valid)
		third := runStart.Add(60 * time.Second)

		got := mustCollect(t, c, third)

		spec := raisedSpec()
		spec.vmRate = []float64{1, 0, 1.5, 0.05}
		spec.disks[0].rates = []float64{5, 3000 * 512 / 30, 10, 6000 * 512 / 30, 2.5}
		requireRecords(t, third, got, wantSystem(t, spec))
	})
}

// malformedCase is a variant of the fixture tree that the system sample refuses.
type malformedCase struct {
	name   string
	mutate func(t *testing.T, tr *tree)
	want   string
}

func procFile(name, content string) func(*testing.T, *tree) {
	return func(_ *testing.T, tr *tree) { set(tr.proc, name, content) }
}

func sysFile(name, content string) func(*testing.T, *tree) {
	return func(_ *testing.T, tr *tree) { set(tr.sys, name, content) }
}

// appendProc appends extra to the fixture file name.
func appendProc(name, extra string) func(*testing.T, *tree) {
	return func(t *testing.T, tr *tree) { set(tr.proc, name, text(t, tr.proc, name)+extra) }
}

// replaceProcLine replaces the line of the fixture file name that starts with prefix.
func replaceProcLine(name, prefix, line string) func(*testing.T, *tree) {
	return func(t *testing.T, tr *tree) { replaceLine(t, tr.proc, name, prefix, line) }
}

// extraMeminfo returns n valid meminfo lines with distinct names.
func extraMeminfo(n int) string {
	var b strings.Builder
	for i := 0; i < n; i++ {
		b.WriteString("Extra" + strconv.Itoa(i) + ":  1 kB\n")
	}
	return b.String()
}

func statCases() []malformedCase {
	const noCPU = "sysstats: stat: no cpu line"
	const badCPU = "sysstats: stat: cpu line: not 8 or more counters"
	cpu := func(last string) string { return "cpu  1 2 3 4 5 6 7 " + last + "\n" }
	return []malformedCase{
		{"stat empty file", procFile("stat", ""), noCPU},
		{"stat first line is not cpu", procFile("stat", "intr 1\n"), noCPU},
		{"stat per-cpu line first", procFile("stat", "cpu0 1 2 3 4 5 6 7 8\n"), noCPU},
		{"stat 7 counters", procFile("stat", "cpu  1 2 3 4 5 6 7\n"), badCPU},
		{"stat negative counter", procFile("stat", cpu("-1")), badCPU},
		{"stat counter with a letter", procFile("stat", cpu("1x")), badCPU},
		{"stat counter of 21 digits", procFile("stat", cpu("123456789012345678901")), badCPU},
		{"stat counter above 64 bits", procFile("stat", cpu("18446744073709551616")), badCPU},
		{
			"stat first line of 6020 bytes", procFile("stat", cpu(strings.Repeat(" 0", 3000))),
			"sysstats: stat: first line longer than 4096 bytes",
		},
	}
}

func loadavgCases() []malformedCase {
	const bad = "sysstats: loadavg: not a load average line"
	return []malformedCase{
		{"loadavg one decimal", procFile("loadavg", "1.0 2.00 3.00 1/2 3\n"), bad},
		{"loadavg decimal comma", procFile("loadavg", "1,00 2.00 3.00 1/2 3\n"), bad},
		{"loadavg plus sign", procFile("loadavg", "+1.00 2.00 3.00 1/2 3\n"), bad},
		{"loadavg two fields", procFile("loadavg", "1.00 2.00\n"), bad},
		{"loadavg 11 integer digits", procFile("loadavg", "12345678901.00 2.00 3.00 1/2 3\n"), bad},
	}
}

func meminfoCases(t *testing.T) []malformedCase {
	t.Helper()

	const bad = "sysstats: meminfo: line "
	last := strconv.Itoa(fixtureLines + 1)
	probe := loadTree(t)
	active := strconv.Itoa(lineNumber(t, probe.proc, "meminfo", "Active:"))
	lines := []struct{ name, line string }{
		{"no colon", "MemTotal 100 kB"},
		{"name of 65 characters", strings.Repeat("a", 65) + ":  1 kB"},
		{"name with a dash", "Mem-Total:  1 kB"},
		{"name with a non-ASCII letter", "MémTotal:  1 kB"},
		{"no value", "Foo:"},
		{"unit MB", "Foo:  12 MB"},
		{"kB without a space", "Foo:  12kB"},
		{"tab before the value", "Foo:\t12 kB"},
		{"value of 21 digits", "Foo:  123456789012345678901 kB"},
		{"value above 64 bits", "Foo:  18446744073709551616"},
		{"value whose product with 1024 overflows", "Foo:  18014398509481984 kB"},
	}
	var cases []malformedCase
	for _, l := range lines {
		cases = append(cases, malformedCase{"meminfo " + l.name, appendProc("meminfo", l.line+"\n"), bad + last + ": not a meminfo line"})
	}
	return append(cases,
		malformedCase{
			"meminfo empty line in the middle",
			func(t *testing.T, tr *tree) { edit(t, tr.proc, "meminfo", "\nActive:", "\n\nActive:") },
			bad + active + ": not a meminfo line",
		},
		malformedCase{"meminfo repeated field", appendProc("meminfo", "MemTotal:  1 kB\n"), bad + last + ": repeated field"},
		malformedCase{"meminfo 129 lines", appendProc("meminfo", extraMeminfo(75)), "sysstats: meminfo: more than 128 lines"})
}

func vmstatCases(t *testing.T) []malformedCase {
	t.Helper()

	const bad = "sysstats: vmstat: line "
	probe := loadTree(t)
	swpin := strconv.Itoa(lineNumber(t, probe.proc, "vmstat", "pswpin "))
	last := strconv.Itoa(len(strings.Split(strings.TrimSuffix(text(t, probe.proc, "vmstat"), "\n"), "\n")) + 1)
	var cases []malformedCase
	for _, l := range []string{"pswpin -1", "pswpin", "pswpin  1", "pswpin 1 2"} {
		cases = append(cases, malformedCase{"vmstat " + l, replaceProcLine("vmstat", "pswpin ", l), bad + swpin + ": not a counter line"})
	}
	return append(cases,
		malformedCase{"vmstat repeated counter", appendProc("vmstat", "pgmajfault 5\n"), bad + last + ": repeated counter"},
		malformedCase{"vmstat no oom_kill line", replaceProcLine("vmstat", "oom_kill ", ""), "sysstats: vmstat: no oom_kill line"})
}

func pressureCases() []malformedCase {
	const bad = "sysstats: pressure/memory: line "
	const notPSI = ": not a pressure line"
	first := []struct{ name, content string }{
		{"full before some", goodFull + "\n" + goodSome + "\n"},
		{"one decimal", "some avg10=0.0 avg60=0.00 avg300=0.00 total=0\n"},
		{"avg10 above 100", "some avg10=100.01 avg60=0.00 avg300=0.00 total=0\n"},
		{"avg10 of 4 integer digits", "some avg10=1000.00 avg60=0.00 avg300=0.00 total=0\n"},
		{"keys in another order", "some avg60=0.00 avg10=0.00 avg300=0.00 total=0\n"},
		{"two spaces", "some  avg10=0.00 avg60=0.00 avg300=0.00 total=0\n"},
		{"negative total", "some avg10=0.00 avg60=0.00 avg300=0.00 total=-1\n"},
		{"total above 64 bits", "some avg10=0.00 avg60=0.00 avg300=0.00 total=18446744073709551616\n"},
		{"not available", "n/a\n"},
	}
	var cases []malformedCase
	for _, f := range first {
		cases = append(cases, malformedCase{"pressure " + f.name, procFile("pressure/memory", f.content), bad + "1" + notPSI})
	}
	return append(cases,
		malformedCase{"pressure third line", procFile("pressure/memory", goodSome+"\n"+goodFull+"\n"+goodFull+"\n"), bad + "3" + notPSI},
		malformedCase{
			"pressure bad second line",
			procFile("pressure/memory", goodSome+"\nfull avg10=0.0 avg60=0.00 avg300=0.00 total=0\n"), bad + "2" + notPSI,
		},
		malformedCase{"pressure empty file", procFile("pressure/memory", ""), "sysstats: pressure/memory: no some line"})
}

func blockCases() []malformedCase {
	const bad = "sysstats: block/vda/stat: not a block device stat line"
	return []malformedCase{
		{"block stat 10 fields", sysFile("block/vda/stat", "1 2 3 4 5 6 7 8 9 10\n"), bad},
		{"block stat field x", sysFile("block/vda/stat", "1 2 x 4 5 6 7 8 9 10 11\n"), bad},
	}
}

func TestSystem_RejectsMalformedInput(t *testing.T) {
	var cases []malformedCase
	cases = append(cases, statCases()...)
	cases = append(cases, loadavgCases()...)
	cases = append(cases, meminfoCases(t)...)
	cases = append(cases, vmstatCases(t)...)
	cases = append(cases, pressureCases()...)
	cases = append(cases, blockCases()...)
	for _, tt := range cases {
		t.Run(tt.name, func(t *testing.T) {
			tr := loadTree(t)
			tt.mutate(t, tr)

			requireFails(t, sysstats.NewSystem(tr.sources(), nil), runStart, tt.want)
		})
	}
}

// acceptedCase is a kernel variant the system sample reads.
type acceptedCase struct {
	name   string
	mutate func(t *testing.T, tr *tree)
	check  func(t *testing.T, got []model.Record)
}

func acceptedCases() []acceptedCase {
	vda := func(fields string) func(*testing.T, *tree) { return sysFile("block/vda/stat", fields+"\n") }
	return []acceptedCase{
		{"cpu line of exactly 8 counters", func(t *testing.T, tr *tree) {
			edit(t, tr.proc, "stat", fixtureCPU, "cpu  28110 0 10232 889261 519 0 1222 1564")
		}, nil},
		{"cpu line of 10 counters", func(*testing.T, *tree) {}, nil},
		{"block stat of 11 fields", vda("14573 8362 1068218 3840 8833 74381 6437752 39502 0 6764 45032"), diskCounters},
		{"block stat of 15 fields", vda("14573 8362 1068218 3840 8833 74381 6437752 39502 0 6764 45032 5911 0 1422192 1429"), diskCounters},
		{
			"block stat of 17 fields with leading spaces",
			vda("   14573     8362 1068218 3840 8833 74381 6437752 39502 0 6764 45032 5911 0 1422192 1429 114 259"), diskCounters,
		},
		{"vmstat lines of other names in any form", appendProc("vmstat", "nr_foo -3\nweird\nsome other line 1 2 3\n"), nil},
		{"loadavg with 10 integer digits", procFile("loadavg", "1234567890.00 2.00 3.00 1/2 3\n"), func(t *testing.T, got []model.Record) {
			if l := find(got, "load1"); len(l) != 1 || l[0].value != 1234567890 {
				t.Errorf("load1 records = %v, want one with the value 1234567890", l)
			}
		}},
		{
			"pressure with the largest numbers",
			procFile("pressure/io", "some avg10=100.00 avg60=100.00 avg300=100.00 total=18446744073709551615\n"+goodFull+"\n"), nil,
		},
		{"meminfo of exactly 128 lines", appendProc("meminfo", extraMeminfo(74)), func(t *testing.T, got []model.Record) {
			if n := len(find(got, "meminfo_bytes")) + len(find(got, "meminfo")); n != 128 {
				t.Errorf("meminfo records = %d, want 128", n)
			}
		}},
		{"meminfo line without a unit", func(*testing.T, *tree) {}, requireHugePagesWithoutUnit},
	}
}

// requireHugePagesWithoutUnit checks the meminfo record of HugePages_Total, a line without unit.
func requireHugePagesWithoutUnit(t *testing.T, got []model.Record) {
	t.Helper()

	for _, r := range find(got, "meminfo") {
		if r.labels["field"] == "HugePages_Total" && r.value == 0 && r.unit == "" {
			return
		}
	}
	t.Errorf("no meminfo record for HugePages_Total without a unit in %d records", len(got))
}

func TestSystem_AcceptsKernelVariants(t *testing.T) {
	for _, tt := range acceptedCases() {
		t.Run(tt.name, func(t *testing.T) {
			tr := loadTree(t)
			tt.mutate(t, tr)

			got := mustCollect(t, sysstats.NewSystem(tr.sources(), nil), runStart)

			if len(got) == 0 {
				t.Fatalf("Collect() returned no records")
			}
			if tt.check != nil {
				tt.check(t, got)
			}
		})
	}
}

func TestSystem_CPULineOfEightCounters(t *testing.T) {
	tr := loadTree(t)
	edit(t, tr.proc, "stat", fixtureCPU, "cpu  28110 0 10232 889261 519 0 1222 1564")
	c := sysstats.NewSystem(tr.sources(), nil)
	mustCollect(t, c, runStart)
	edit(t, tr.proc, "stat", "cpu  28110 0 10232 889261 519 0 1222 1564", "cpu  28410 30 10382 891661 579 0 1252 1594")
	second := runStart.Add(30 * time.Second)

	got := mustCollect(t, c, second)

	var percent []float64
	for _, r := range find(got, "cpu_percent") {
		percent = append(percent, r.value)
	}
	want := raisedSpec().cpu
	if len(percent) != len(want) {
		t.Fatalf("cpu_percent values = %v, want %v", percent, want)
	}
	for i := range want {
		if !near(percent[i], want[i]) {
			t.Errorf("cpu_percent of %s = %v, want %v", cpuModes[i], percent[i], want[i])
		}
	}
}

// diskCounters checks that vda has its five counters.
func diskCounters(t *testing.T, got []model.Record) {
	t.Helper()

	want := diskRecords(baseSpec().disks[0])
	var have []wantRec
	for _, r := range got {
		if g, ok := gotRec(r); ok && g.labels["device"] == "vda" {
			have = append(have, g)
		}
	}
	if len(have) != len(want) {
		t.Fatalf("disk records of vda = %v, want %v", have, want)
	}
	for i, w := range want {
		if have[i].name != w.name || !near(have[i].value, w.value) || have[i].unit != w.unit {
			t.Errorf("disk record %d = %s, want %s", i, have[i], w)
		}
	}
}

func TestSystem_ReadLimits(t *testing.T) {
	fixtureMeminfo := func(t *testing.T) string { return text(t, loadTree(t).proc, "meminfo") }
	fixtureVmstat := func(t *testing.T) string { return text(t, loadTree(t).proc, "vmstat") }
	pad := func(prefix string, total int, fill, suffix string) string {
		return prefix + strings.Repeat(fill, total-len(prefix)-len(suffix)) + suffix
	}
	cases := []struct {
		name    string
		sys     bool
		file    string
		limit   int
		atLimit func(t *testing.T) string
		wantAt  string // the error of the file of exactly the limit; "" when it is read
		over    func(t *testing.T) string
		display string
	}{
		{
			name: "loadavg", file: "loadavg", limit: 256, display: "loadavg",
			atLimit: func(*testing.T) string { return strings.Repeat("x", 255) + "\n" },
			wantAt:  "sysstats: loadavg: not a load average line",
			over:    func(*testing.T) string { return strings.Repeat("x", 256) + "\n" },
		},
		{
			name: "meminfo", file: "meminfo", limit: 16384, display: "meminfo",
			atLimit: func(t *testing.T) string { return pad(fixtureMeminfo(t)+"Padding:", 16384, " ", "0 kB\n") },
			over:    func(t *testing.T) string { return pad(fixtureMeminfo(t)+"Padding:", 16385, " ", "0 kB\n") },
		},
		{
			name: "vmstat", file: "vmstat", limit: 65536, display: "vmstat",
			atLimit: func(t *testing.T) string { return pad(fixtureVmstat(t)+"x", 65536, "y", "\n") },
			over:    func(t *testing.T) string { return pad(fixtureVmstat(t)+"x", 65537, "y", "\n") },
		},
		{
			name: "pressure/cpu", file: "pressure/cpu", limit: 256, display: "pressure/cpu",
			atLimit: func(*testing.T) string { return strings.Repeat("x", 255) + "\n" },
			wantAt:  "sysstats: pressure/cpu: line 1: not a pressure line",
			over:    func(*testing.T) string { return strings.Repeat("x", 256) + "\n" },
		},
		{
			name: "block/vda/stat", sys: true, file: "block/vda/stat", limit: 1024, display: "block/vda/stat",
			atLimit: func(*testing.T) string { return strings.Repeat("x", 1023) + "\n" },
			wantAt:  "sysstats: block/vda/stat: not a block device stat line",
			over:    func(*testing.T) string { return strings.Repeat("x", 1024) + "\n" },
		},
	}
	for _, tt := range cases {
		files := func(tr *tree) fstest.MapFS {
			if tt.sys {
				return tr.sys
			}
			return tr.proc
		}
		t.Run(tt.name+" of exactly the limit is read", func(t *testing.T) {
			tr := loadTree(t)
			content := tt.atLimit(t)
			if len(content) != tt.limit {
				t.Fatalf("test input has %d bytes, want %d", len(content), tt.limit)
			}
			set(files(tr), tt.file, content)
			c := sysstats.NewSystem(tr.sources(), nil)

			if tt.wantAt == "" {
				mustCollect(t, c, runStart)
				return
			}
			requireFails(t, c, runStart, tt.wantAt)
		})
		t.Run(tt.name+" above the limit fails", func(t *testing.T) {
			tr := loadTree(t)
			content := tt.over(t)
			if len(content) != tt.limit+1 {
				t.Fatalf("test input has %d bytes, want %d", len(content), tt.limit+1)
			}
			set(files(tr), tt.file, content)

			requireFails(t, sysstats.NewSystem(tr.sources(), nil), runStart,
				"sysstats: "+tt.display+": larger than "+strconv.Itoa(tt.limit)+" bytes")
		})
	}
}

func TestSystem_HangingSourceIsAbandoned(t *testing.T) {
	hangingSample(t, func(t *testing.T, g *gate) scheduler.Collector {
		tr := loadTree(t)
		src := sysstats.Sources{Proc: blockingFS{FS: tr.proc, name: "meminfo", gate: g}, Sys: tr.sys}
		return sysstats.NewSystem(src, nil)
	})
}
