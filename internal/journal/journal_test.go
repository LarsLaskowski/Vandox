package journal

import (
	"bytes"
	"encoding/hex"
	"errors"
	"io"
	"io/fs"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"testing"
	"testing/fstest"
	"time"
)

const kernelPayload = "_TRANSPORT=kernel"

var (
	testFileID    = hexID("f1f2f3f4f5f6f7f8f9fafbfcfdfeff00")
	testMachineID = hexID("0badc0de0badc0de0badc0de0badc0de")
	testSeqnumID  = hexID("00000000000000000000000000000001")
)

func testOptions() bOptions {
	return bOptions{fileID: testFileID, machineID: testMachineID, seqnumID: testSeqnumID}
}

// openRef opens a reader over the bytes behind g.
func (g *refReader) openRef(setAside bool) (*File, error) {
	return Open(g, g.size(), setAside)
}

func openBytes(t *testing.T, b []byte, setAside bool) *File {
	t.Helper()
	f, err := newRefReader(b).openRef(setAside)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	return f
}

func kernelList(t *testing.T, f *File) Data {
	t.Helper()
	d, found, err := f.Lookup([]byte(kernelPayload))
	if err != nil || !found {
		t.Fatalf("Lookup(%q) = found %v, err %v, want found true", kernelPayload, found, err)
	}
	return d
}

func kernelOnly(entries []bEntry) []bEntry {
	var out []bEntry
	for _, e := range entries {
		for _, it := range e.items {
			if string(it.payload) == kernelPayload {
				out = append(out, e)
				break
			}
		}
	}
	return out
}

func messageOf(e bEntry) string {
	for _, it := range e.items {
		if v, ok := strings.CutPrefix(string(it.payload), "MESSAGE="); ok {
			return v
		}
	}
	return ""
}

func microTime(us uint64) time.Time { return time.UnixMicro(i64(us)).UTC() }

func TestFile_ReadsKernelEntriesInOrder(t *testing.T) {
	entries := kernelEntries(300, true)
	b, _ := buildFile(entries, testOptions())
	want := kernelOnly(entries)
	f := openBytes(t, b, false)
	var res walkResult
	if err := readAll(f, kernelList(t, f), 0, &res); err != nil {
		t.Fatalf("reading the kernel entries: error = %v, want nil", err)
	}
	if len(res.entries) != len(want) {
		t.Fatalf("read %d kernel entries, want %d", len(res.entries), len(want))
	}
	for i, got := range res.entries {
		checkKernelEntry(t, i, got, want[i])
	}
}

func checkKernelEntry(t *testing.T, i int, got echoEntry, w bEntry) {
	t.Helper()
	if got.seqnum != w.seqnum || got.message != messageOf(w) || got.boot != w.boot || got.items != len(w.items) {
		t.Fatalf("entry %d = seqnum %d items %d message %q, want seqnum %d items %d message %q", i, got.seqnum, got.items, got.message, w.seqnum, len(w.items), messageOf(w))
	}
	if len(got.fac) != 1 || got.fac[0] != "0" {
		t.Fatalf("entry %d SYSLOG_FACILITY = %q, want [0]", i, got.fac)
	}
	if got.names != len(wantNames) {
		t.Fatalf("entry %d holds %d fields, want only the %d named in want", i, got.names, len(wantNames))
	}
}

func TestFile_EntryCarriesTimesAndBootID(t *testing.T) {
	b, _ := buildFile(kernelEntries(3, false), testOptions())
	f := openBytes(t, b, false)
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	if ok, err := f.Next(&c, wantNames, &e); err != nil || !ok {
		t.Fatalf("Next() = %v, %v, want true, nil", ok, err)
	}
	if want := microTime(testBase); !e.Realtime.Equal(want) {
		t.Errorf("Realtime = %v, want %v", e.Realtime, want)
	}
	if want := time.Duration(testMono) * time.Microsecond; e.Monotonic != want {
		t.Errorf("Monotonic = %v, want %v", e.Monotonic, want)
	}
	if e.BootID != hexID(testBoot) {
		t.Errorf("BootID = %x, want %s", e.BootID, testBoot)
	}
}

func TestFile_SeekFindsTheFirstEntryAfterASeqnum(t *testing.T) {
	entries := kernelEntries(300, true)
	b, _ := buildFile(entries, testOptions())
	want := kernelOnly(entries)
	f := openBytes(t, b, false)
	d := kernelList(t, f)
	last := want[len(want)-1].seqnum
	for _, after := range []uint64{0, 5, 10, 11, want[150].seqnum - 1, want[150].seqnum, want[150].seqnum + 1, last - 1, last, last + 1, 1 << 62} {
		var wantSeq []uint64
		for _, w := range want {
			if w.seqnum > after {
				wantSeq = append(wantSeq, w.seqnum)
			}
		}
		var res walkResult
		if err := readAll(f, d, after, &res); err != nil {
			t.Fatalf("Seek(after %d) then Next: error = %v, want nil", after, err)
		}
		got := seqnumsOf(res.entries)
		if !sameSeqnums(got, wantSeq) {
			t.Fatalf("after %d: read %d entries (first %v), want %d (first %v)", after, len(got), head(got), len(wantSeq), head(wantSeq))
		}
	}
}

func sameSeqnums(a, b []uint64) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func head(s []uint64) []uint64 { return s[:min(3, len(s))] }

func TestFile_ReportsEveryItemOfARepeatedField(t *testing.T) {
	entries := kernelEntries(3, false)
	entries[1].items = append(entries[1].items, bItem{payload: []byte("SYSLOG_FACILITY=3")})
	b, _ := buildFile(entries, testOptions())
	f := openBytes(t, b, false)
	var res walkResult
	if err := readAll(f, kernelList(t, f), 0, &res); err != nil {
		t.Fatalf("reading: error = %v, want nil", err)
	}
	if got := res.entries[1].fac; len(got) != 2 || got[0] != "0" || got[1] != "3" {
		t.Errorf("SYSLOG_FACILITY of the entry with two items = %q, want [0 3]", got)
	}
	if got := res.entries[0].fac; len(got) != 1 {
		t.Errorf("SYSLOG_FACILITY of a plain entry = %q, want one item", got)
	}
}

func TestFile_ReportsCompressedItemsWithoutDecompressingThem(t *testing.T) {
	entries := kernelEntries(3, false)
	long := "MESSAGE=Out of memory: Killed process 4247 (pzstd) " + strings.Repeat("handmade padding ", 40)
	entries[1].items[4] = bItem{payload: []byte(long), compressed: true}
	b, _ := buildFile(entries, testOptions())
	f := openBytes(t, b, false)
	var res walkResult
	if err := readAll(f, kernelList(t, f), 0, &res); err != nil {
		t.Fatalf("reading: error = %v, want nil", err)
	}
	if res.entries[0].compOne || res.entries[2].compOne {
		t.Errorf("a plain entry reports a compressed item")
	}
	got := res.entries[1]
	if !got.compOne {
		t.Errorf("the entry with a compressed MESSAGE reports no compressed item")
	}
	if got.message != "" {
		t.Errorf("the compressed MESSAGE came back as plain text %.40q", got.message)
	}
	if len(got.fac) != 1 || got.fac[0] != "0" {
		t.Errorf("SYSLOG_FACILITY of that entry = %q, want [0]", got.fac)
	}
}

func TestFile_ReadsAtMost4096BytesOfAPayload(t *testing.T) {
	entries := kernelEntries(3, false)
	big := strings.Repeat("0123456789abcdef", 1<<16) // 1 MiB
	entries[1].items[4] = bItem{payload: []byte("MESSAGE=" + big)}
	entries[1].items = append(entries[1].items, bItem{payload: []byte(strings.Repeat("N", 5000) + "=value")})
	b, _ := buildFile(entries, testOptions())
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	for i := 0; i < 3; i++ {
		if ok, err := f.Next(&c, wantNames, &e); err != nil || !ok {
			t.Fatalf("Next() %d = %v, %v, want true, nil", i, ok, err)
		}
		if i == 1 {
			checkCappedMessage(t, &e, big)
		}
	}
	if r.violated != "" {
		t.Errorf("the reader slurped data: %s; want reads of at most 64 KiB", r.violated)
	}
}

func checkCappedMessage(t *testing.T, e *Entry, full string) {
	t.Helper()
	m, ok := e.Field("MESSAGE")
	if !ok || len(m) != 1 {
		t.Fatalf("MESSAGE of the large entry = %d fields, want 1", len(m))
	}
	if n := len(m[0].Value); n > 4096 || n < 4000 {
		t.Errorf("MESSAGE value holds %d bytes, want the first 4000 to 4096 bytes of the value", n)
	}
	if !strings.HasPrefix(full, string(m[0].Value)) {
		t.Errorf("MESSAGE value is not a prefix of the stored value")
	}
}

func TestFile_HeaderReportsTheFilesIdentity(t *testing.T) {
	entries := kernelEntries(5, false)
	for _, online := range []bool{false, true} {
		o := testOptions()
		o.online = online
		b, _ := buildFile(entries, o)
		for _, setAside := range []bool{false, true} {
			h := openBytes(t, b, setAside).Header()
			checkHeader(t, h, entries, online, setAside)
		}
	}
}

func checkHeader(t *testing.T, h Header, entries []bEntry, online, setAside bool) {
	t.Helper()
	first, last := entries[0], entries[len(entries)-1]
	if h.FileID != testFileID || h.MachineID != testMachineID || h.SeqnumID != testSeqnumID {
		t.Errorf("online=%v: IDs = %x %x %x, want the file, machine and seqnum IDs written", online, h.FileID, h.MachineID, h.SeqnumID)
	}
	if h.Online != online || h.SetAside != setAside {
		t.Errorf("online=%v setAside=%v: Header.Online, SetAside = %v, %v", online, setAside, h.Online, h.SetAside)
	}
	if h.HeadSeqnum != first.seqnum || h.TailSeqnum != last.seqnum {
		t.Errorf("seqnums = %d..%d, want %d..%d", h.HeadSeqnum, h.TailSeqnum, first.seqnum, last.seqnum)
	}
	if !h.HeadRealtime.Equal(microTime(first.realtime)) || !h.TailRealtime.Equal(microTime(last.realtime)) {
		t.Errorf("times = %v..%v, want the first and last entry's real time", h.HeadRealtime, h.TailRealtime)
	}
}

func TestFile_HeaderTimesOutsideTheWireRangeAreAbsent(t *testing.T) {
	const firstBadYear = 253402300800000000
	b, _ := buildFile(kernelEntries(3, false), testOptions())
	cases := []struct {
		name    string
		us      uint64
		present bool
	}{
		{"zero", 0, false},
		{"first microsecond of year 10000", firstBadYear, false},
		{"1<<62", 1 << 62, false},
		{"all ones", ^uint64(0), false},
		{"last microsecond of year 9999", firstBadYear - 1, true},
		{"one microsecond after the epoch", 1, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			m := mutated(mutated(b, hdrHeadRealtime, c.us), hdrTailRealtime, c.us)
			h := openBytes(t, m, false).Header()
			if got := !h.HeadRealtime.IsZero() && !h.TailRealtime.IsZero(); got != c.present {
				t.Errorf("head %v tail %v present = %v, want %v", h.HeadRealtime, h.TailRealtime, got, c.present)
			}
			if c.present && !h.HeadRealtime.Equal(microTime(c.us)) {
				t.Errorf("HeadRealtime = %v, want %v", h.HeadRealtime, microTime(c.us))
			}
		})
	}
}

func withHeader(base []byte, f func(b []byte)) []byte {
	c := append([]byte(nil), base...)
	f(c)
	return c
}

func TestFile_RefusesUnsupportedLayout(t *testing.T) {
	base, _ := buildFile(kernelEntries(3, false), testOptions())
	setIncompat := func(v uint32) []byte { return withHeader(base, func(b []byte) { le.PutUint32(b[hdrIncompat:], v) }) }
	setHeaderSize := func(v uint64) []byte { return withHeader(base, func(b []byte) { le.PutUint64(b[hdrHeaderSize:], v) }) }
	refused := []struct {
		name string
		file []byte
	}{
		{"wrong signature", withHeader(base, func(b []byte) { b[0] = 'X' })},
		{"signature of a text file", withHeader(base, func(b []byte) { copy(b, "{\"kind\":\"") })},
		{"header size 248", setHeaderSize(248)},
		{"header size 4104", setHeaderSize(4104)},
		{"header size 1<<40", setHeaderSize(1 << 40)},
		{"compact without keyed hash", setIncompat(incompatZSTD | incompatCompact)},
		{"no keyed hash", setIncompat(incompatZSTD)},
		{"no flag at all", setIncompat(0)},
		{"unknown flag 1<<5", setIncompat(incompatKeyed | 1<<5)},
		{"unknown flag 1<<31", setIncompat(incompatKeyed | 1<<31)},
	}
	for _, c := range refused {
		t.Run(c.name, func(t *testing.T) {
			f, err := newRefReader(c.file).openRef(false)
			if !errors.Is(err, ErrUnsupported) || f != nil {
				t.Errorf("Open() = %v, %v, want nil and %v", f, err, ErrUnsupported)
			}
			if err != nil && err.Error() != "journal: unsupported layout" {
				t.Errorf("Open() error text = %q, want %q", err.Error(), "journal: unsupported layout")
			}
		})
	}
}

func TestFile_ReadsEveryAcceptedFlagCombination(t *testing.T) {
	base, _ := buildFile(kernelEntries(3, false), testOptions())
	accepted := map[string]uint32{
		"keyed hash only":        incompatKeyed,
		"keyed hash with XZ":     incompatKeyed | incompatXZ,
		"keyed hash with LZ4":    incompatKeyed | incompatLZ4,
		"keyed hash with ZSTD":   incompatKeyed | incompatZSTD,
		"all three compressions": incompatKeyed | incompatXZ | incompatLZ4 | incompatZSTD,
	}
	for name, flags := range accepted {
		t.Run(name, func(t *testing.T) {
			b := withHeader(base, func(b []byte) { le.PutUint32(b[hdrIncompat:], flags) })
			if _, err := newRefReader(b).openRef(false); err != nil {
				t.Errorf("Open() error = %v, want nil", err)
			}
		})
	}
}

func TestFile_ReadsEveryAcceptedCompactFlagCombination(t *testing.T) {
	accepted := map[string]uint32{
		"compact with keyed hash":      incompatKeyed | incompatCompact,
		"compact with XZ":              incompatKeyed | incompatCompact | incompatXZ,
		"compact with LZ4":             incompatKeyed | incompatCompact | incompatLZ4,
		"compact with ZSTD":            incompatKeyed | incompatCompact | incompatZSTD,
		"compact with all compression": incompatKeyed | incompatCompact | incompatXZ | incompatLZ4 | incompatZSTD,
	}
	entries := kernelEntries(30, true)
	want := kernelOnly(entries)
	for name, flags := range accepted {
		for _, hs := range []int{264, 272} {
			t.Run(name+" header "+strconv.Itoa(hs), func(t *testing.T) {
				o := testOptions()
				o.compact, o.incompat, o.headerSize = true, flags, hs
				b, _ := buildFile(entries, o)
				var res walkResult
				f := openBytes(t, b, false)
				if err := readAll(f, kernelList(t, f), 0, &res); err != nil || len(res.entries) != len(want) {
					t.Fatalf("reading the compact file: %d entries, error %v, want %d entries", len(res.entries), err, len(want))
				}
				for i, got := range res.entries {
					checkKernelEntry(t, i, got, want[i])
				}
			})
		}
	}
}

func TestFile_ReadsHeadersOfEverySupportedSize(t *testing.T) {
	for _, hs := range []int{256, 264, 1024, 4096} {
		t.Run(strconv.Itoa(hs), func(t *testing.T) {
			o := testOptions()
			o.headerSize = hs
			b, _ := buildFile(kernelEntries(3, false), o)
			var res walkResult
			f := openBytes(t, b, false)
			if err := readAll(f, kernelList(t, f), 0, &res); err != nil || len(res.entries) != 3 {
				t.Errorf("reading with header size %d: %d entries, error %v, want 3 entries", hs, len(res.entries), err)
			}
		})
	}
}

func TestFile_RefusesAFileThatEndsInsideTheHeader(t *testing.T) {
	base, _ := buildFile(kernelEntries(3, false), testOptions())
	for _, n := range []int{0, 7, 8, 100, 255} {
		f, err := newRefReader(base[:n]).openRef(false)
		if f != nil || (!errors.Is(err, ErrUnsupported) && !errors.Is(err, ErrMalformed)) {
			t.Errorf("Open() of %d bytes = %v, %v, want nil and an unsupported or malformed error", n, f, err)
		}
	}
}

type boundsCase struct {
	name   string
	mutate func(b []byte, l *layout) []byte
}

func boundsCases() []boundsCase {
	kd := func(l *layout) uint64 { return l.data[kernelPayload] }
	arrays := func(l *layout) []uint64 { return l.dataArrays[kernelPayload] }
	end := func(b []byte) uint64 { return u64(len(b)) + 4096 }
	return []boundsCase{
		{"hash chain: object links to itself", func(b []byte, l *layout) []byte { return mutated(b, l.dataOrder[3]+24, l.dataOrder[3]) }},
		{"hash chain: last object links back to the first (cycle)", func(b []byte, l *layout) []byte {
			return mutated(b, l.dataOrder[len(l.dataOrder)-1]+24, l.dataOrder[0])
		}},
		{"hash chain: link to an earlier object (backward)", func(b []byte, l *layout) []byte { return mutated(b, l.dataOrder[3]+24, l.dataOrder[1]) }},
		{"hash chain: link beyond the file", func(b []byte, l *layout) []byte { return mutated(b, l.dataOrder[3]+24, end(b)) }},
		{"hash chain: link not 8-aligned", func(b []byte, l *layout) []byte { return mutated(b, l.dataOrder[2]+24, l.dataOrder[3]+4) }},
		{"hash chain: link into the header", func(b []byte, l *layout) []byte { return mutated(b, l.dataOrder[3]+24, 64) }},
		{"hash table: bucket head not 8-aligned", func(b []byte, l *layout) []byte { return mutated(b, l.tableItems, l.dataOrder[0]+4) }},
		{"hash table: bucket head inside the header", func(b []byte, l *layout) []byte { return mutated(b, l.tableItems, 64) }},
		{"hash table: offset in the header not 8-aligned", func(b []byte, l *layout) []byte { return mutated(b, hdrDataTable, l.tableItems+4) }},
		{"hash table: offset beyond the file", func(b []byte, _ *layout) []byte { return mutated(b, hdrDataTable, end(b)) }},
		{"header: arena size zero", func(b []byte, _ *layout) []byte { return mutated(b, hdrArenaSize, 0) }},
		{"data object: smaller than its fixed part", func(b []byte, l *layout) []byte { return mutated(b, kd(l)+8, 40) }},
		{"data object: ends beyond the arena", func(b []byte, l *layout) []byte { return mutated(b, kd(l)+8, 1<<40) }},
		{"entry array: links to itself", func(b []byte, l *layout) []byte { a := arrays(l); return mutated(b, a[0]+16, a[0]) }},
		{"entry array: links back to the first (backward)", func(b []byte, l *layout) []byte { a := arrays(l); return mutated(b, a[1]+16, a[0]) }},
		{"entry array: link beyond the file", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+16, end(b)) }},
		{"entry array: link not 8-aligned", func(b []byte, l *layout) []byte { a := arrays(l); return mutated(b, a[0]+16, a[1]+4) }},
		{"entry array: entry offset inside the header", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+24, 16) }},
		{"entry array: entry offset not 8-aligned", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+24, l.entries[2]+4) }},
		{"entry array: entry offset beyond the arena", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+24, end(b)) }},
		{"entry: smaller than its fixed part", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+8, 40) }},
		{"entry: ends beyond the arena", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+8, 1<<40) }},
		{"entry item: offset beyond the arena", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+64, end(b)) }},
		{"entry item: offset inside the header", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+64, 32) }},
		{"entry item: offset not 8-aligned", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+64, kd(l)+4) }},
	}
}

func boundsBase(kernel int) ([]byte, *layout, int) {
	o := testOptions()
	o.buckets = 1 // one hash chain holds every data object
	entries := kernelEntries(kernel, true)
	b, l := buildFile(entries, o)
	return b, l, len(kernelOnly(entries))
}

func TestFile_BoundsEveryOffset(t *testing.T) {
	base, lay, kernel := boundsBase(30) // the kernel list spans four entry arrays
	if res := walkBytes(base, false); res.err != nil || len(res.entries) != kernel {
		t.Fatalf("the unmodified file reads %d entries, error %v, want %d entries", len(res.entries), res.err, kernel)
	}
	for _, c := range boundsCases() {
		for _, online := range []bool{false, true} {
			t.Run(c.name+" online="+strconv.FormatBool(online), func(t *testing.T) {
				b := c.mutate(base, lay)
				if online {
					b[hdrState] = stateOnline
				}
				expectMalformed(t, b)
			})
		}
	}
}

func walkBytes(b []byte, setAside bool) walkResult {
	r := newRefReader(b)
	return walk(r, r.size(), setAside)
}

// expectMalformed walks b and wants the malformed-file error without a panic or an abusive read.
func expectMalformed(t *testing.T, b []byte) {
	t.Helper()
	r := newRefReader(b)
	res := walk(r, r.size(), false)
	if res.panic != nil {
		t.Fatalf("panic: %v", res.panic)
	}
	if !errors.Is(res.err, ErrMalformed) {
		t.Errorf("walk error = %v (entries %d), want %v", res.err, len(res.entries), ErrMalformed)
	}
	if res.err != nil && errors.Is(res.err, ErrMalformed) && res.err.Error() != "journal: malformed file" {
		t.Errorf("error text = %q, want %q with no offset or value", res.err.Error(), "journal: malformed file")
	}
	if r.violated != "" {
		t.Errorf("reader misbehaved: %s", r.violated)
	}
}

func TestFile_BoundsEveryOffsetEntryArrayChains(t *testing.T) {
	one := func(int) int { return 1 }
	for _, c := range []struct {
		arrays int
		ok     bool
	}{{64, true}, {65, false}, {70, false}} {
		t.Run(strconv.Itoa(c.arrays)+" arrays", func(t *testing.T) {
			o := testOptions()
			o.arrayCap = one
			b, _ := buildFile(kernelEntries(c.arrays+1, false), o) // one entry inline, the rest in arrays of one
			expectChainOutcome(t, b, c.ok, c.arrays+1)
		})
	}
}

func TestFile_BoundsEveryOffsetItemsPerEntry(t *testing.T) {
	for _, c := range []struct {
		items int
		ok    bool
	}{{512, true}, {513, false}, {4000, false}} {
		t.Run(strconv.Itoa(c.items)+" items", func(t *testing.T) {
			entries := kernelEntries(3, false)
			for i := len(entries[1].items); i < c.items; i++ {
				entries[1].items = append(entries[1].items, bItem{payload: []byte("X" + strconv.Itoa(i) + "=v")})
			}
			b, _ := buildFile(entries, testOptions())
			if !c.ok {
				expectMalformed(t, b)
				return
			}
			res := walkBytes(b, false)
			if res.err != nil || len(res.entries) != 3 || res.entries[1].items != c.items {
				t.Errorf("read %d entries, error %v, want 3 entries with %d items in the second", len(res.entries), res.err, c.items)
			}
		})
	}
}

func expectChainOutcome(t *testing.T, b []byte, ok bool, entries int) {
	t.Helper()
	if !ok {
		expectMalformed(t, b)
		return
	}
	res := walkBytes(b, false)
	if res.panic != nil || res.err != nil || len(res.entries) != entries {
		t.Errorf("walk: %v, want %d entries and no error", res, entries)
	}
}

func TestFile_BoundsEveryOffsetSweep(t *testing.T) {
	base, _, _ := boundsBase(12)
	sweepAll(t, base)
}

// sweepAll changes every byte of base in four ways, one byte at a time, and walks each file.
func sweepAll(t *testing.T, base []byte) {
	t.Helper()
	flips := []func(byte) byte{
		func(byte) byte { return 0xff },
		func(byte) byte { return 0x00 },
		func(c byte) byte { return c ^ 0x01 },
		func(c byte) byte { return c ^ 0x80 },
	}
	b := append([]byte(nil), base...)
	for off := range base {
		for _, flip := range flips {
			b[off] = flip(base[off])
			sweepOne(t, b, off)
		}
		b[off] = base[off]
	}
}

// sweepOne walks a file with one byte changed: it must end in success or a bounds error, never a panic or a runaway.
func sweepOne(t *testing.T, b []byte, off int) {
	t.Helper()
	r := newRefReader(b)
	res := walk(r, r.size(), false)
	switch {
	case res.panic != nil:
		t.Fatalf("byte %d set to %#02x: panic %v", off, b[off], res.panic)
	case res.err != nil && !errors.Is(res.err, ErrMalformed) && !errors.Is(res.err, ErrUnsupported):
		t.Fatalf("byte %d set to %#02x: error %v, want nil, %v or %v", off, b[off], res.err, ErrMalformed, ErrUnsupported)
	case r.violated != "":
		t.Fatalf("byte %d set to %#02x: reader misbehaved: %s", off, b[off], r.violated)
	}
}

// tornFile builds a file of n kernel entries and cuts it inside (or at the start of) entry cutEntry.
func tornFile(n, cutEntry, into int, o bOptions) (full, cut []byte, lay *layout) {
	full, lay = buildFile(kernelEntries(n, false), o)
	return full, full[:asInt(lay.entries[cutEntry])+into], lay
}

func seqnumsOf(es []echoEntry) []uint64 {
	var out []uint64
	for _, e := range es {
		out = append(out, e.seqnum)
	}
	return out
}

// firstSeqnums are the seqnums of the first n entries kernelEntries builds.
func firstSeqnums(n int) []uint64 {
	var out []uint64
	for _, e := range kernelEntries(n, false) {
		out = append(out, e.seqnum)
	}
	return out
}

func TestFile_TornTailIsEndOfFile(t *testing.T) {
	cases := []struct {
		name     string
		online   bool
		setAside bool
		into     int
	}{
		{"archived file cut inside an entry", false, false, 20},
		{"archived file cut between entries", false, false, 0},
		{"set-aside file cut inside an entry", true, true, 20},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			o := testOptions()
			o.online = c.online
			full, cut, _ := tornFile(10, 7, c.into, o)
			res := walkBytes(cut, c.setAside)
			if res.panic != nil || res.err != nil {
				t.Fatalf("walk: %v, want the entries before the tear and no error", res)
			}
			if got, want := seqnumsOf(res.entries), firstSeqnums(7); !sameSeqnums(got, want) {
				t.Errorf("read seqnums %v, want %v (the entries before the torn one)", got, want)
			}
			h, whole := openBytes(t, cut, c.setAside).Header(), openBytes(t, full, c.setAside).Header()
			if !h.TailRealtime.Equal(whole.TailRealtime) || !h.TailRealtime.After(microTime(testBase+6*1000000)) {
				t.Errorf("Header.TailRealtime = %v, want the header's own tail time %v, later than the last entry read", h.TailRealtime, whole.TailRealtime)
			}
		})
	}
}

func TestFile_TornTailOfAnOnlineFileIsNeverAnotherError(t *testing.T) {
	o := testOptions()
	o.online = true
	_, cut, _ := tornFile(10, 7, 20, o)
	res := walkBytes(cut, false)
	if res.panic != nil || (res.err != nil && !errors.Is(res.err, ErrMalformed)) {
		t.Errorf("walk of a torn online file: %v, want no panic and no error or %v", res, ErrMalformed)
	}
	if len(res.entries) > 7 {
		t.Errorf("walk returned %d entries, want at most the 7 before the tear", len(res.entries))
	}
}

// drain reads entries from c until the list ends or an error comes; it returns the seqnums read.
func drain(f *File, c *Cursor) (seen []uint64, stop error) {
	var e Entry
	for i := 0; i < 100; i++ {
		ok, err := f.Next(c, wantNames, &e)
		if err != nil || !ok {
			return seen, err
		}
		seen = append(seen, e.Seqnum)
	}
	return seen, errRunaway
}

func TestFile_TornTailOfAnOnlineFileIsRetriedAfterRefresh(t *testing.T) {
	o := testOptions()
	o.online = true
	full, _, lay := tornFile(10, 7, 0, o)
	r := newRefReader(full)
	r.limit = asInt(lay.entries[7]) + 20
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	d := kernelList(t, f)
	c, err := f.Seek(d, 0)
	var seen []uint64
	if err == nil {
		var stop error
		if seen, stop = drain(f, &c); stop != nil && !errors.Is(stop, ErrMalformed) {
			t.Fatalf("Next() at the torn tail: error = %v, want nil or %v", stop, ErrMalformed)
		}
	} else if !errors.Is(err, ErrMalformed) {
		t.Fatalf("Seek() error = %v, want nil or %v", err, ErrMalformed)
	}
	if len(seen) > 7 {
		t.Fatalf("read %d entries from a file cut after the 7th, want at most 7", len(seen))
	}
	r.limit = 0
	if rerr := f.Refresh(r.size()); rerr != nil {
		t.Fatalf("Refresh() error = %v, want nil", rerr)
	}
	if err != nil { // the tear was met while seeking: seek again in the grown file
		seen = nil
		if c, err = f.Seek(d, 0); err != nil {
			t.Fatalf("Seek() after Refresh: error = %v, want nil", err)
		}
	}
	rest, stop := drain(f, &c)
	if stop != nil {
		t.Fatalf("Next() after Refresh: error = %v, want nil", stop)
	}
	if got, want := append(append([]uint64(nil), seen...), rest...), firstSeqnums(10); !sameSeqnums(got, want) {
		t.Fatalf("after the file grew, read seqnums %v, want %v (no entry lost or repeated)", got, want)
	}
}

// boundsBaseCompact is boundsBase in the COMPACT layout.
func boundsBaseCompact(kernel int) ([]byte, *layout, int) {
	o := testOptions()
	o.compact = true
	o.buckets = 1 // one hash chain holds every data object
	entries := kernelEntries(kernel, true)
	b, l := buildFile(entries, o)
	return b, l, len(kernelOnly(entries))
}

// compactBoundsCases are the bounds cases of the COMPACT layout: the cases of boundsCases that do not write an item or a slot (their
// 64-bit values cannot address a 4-byte one), and the cases that write 32-bit values.
func compactBoundsCases() []boundsCase {
	var out []boundsCase
	for _, c := range boundsCases() {
		if !strings.HasPrefix(c.name, "entry array: entry offset") && !strings.HasPrefix(c.name, "entry item:") {
			out = append(out, c)
		}
	}
	kd := func(l *layout) uint64 { return l.data[kernelPayload] }
	arrays := func(l *layout) []uint64 { return l.dataArrays[kernelPayload] }
	item := func(l *layout) uint64 { return l.itemAt(l.entries[1], 0) }
	slot := func(l *layout) uint64 { return l.slotAt(arrays(l)[0], 0) }
	end := func(b []byte) uint32 { return u32(len(b) + 4096) }
	return append(out, []boundsCase{
		{"data object: 71 bytes, one short of its fixed part", func(b []byte, l *layout) []byte { return mutated(b, kd(l)+8, 71) }},
		{"data object: 68 bytes, the size of the fixed part of the other layout", func(b []byte, l *layout) []byte { return mutated(b, kd(l)+8, 68) }},
		{"entry: size 1 byte over a whole number of items", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+8, 64+4*7+1) }},
		{"entry: size 2 bytes over a whole number of items", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+8, 64+4*7+2) }},
		{"entry: size 3 bytes over a whole number of items", func(b []byte, l *layout) []byte { return mutated(b, l.entries[1]+8, 64+4*7+3) }},
		{"entry array: size 2 bytes over a whole number of items", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+8, 24+4*4+2) }},
		{"entry array: size 1 byte over a whole number of items", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+8, 24+4*4+1) }},
		{"entry array: size without any item", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+8, 24) }},
		{"entry array: size 26, smaller than the fixed part and one item", func(b []byte, l *layout) []byte { return mutated(b, arrays(l)[0]+8, 26) }},
		{"entry item: offset beyond the arena", func(b []byte, l *layout) []byte { return mutated32(b, item(l), end(b)) }},
		{"entry item: offset inside the header", func(b []byte, l *layout) []byte { return mutated32(b, item(l), 32) }},
		{"entry item: offset not 8-aligned", func(b []byte, l *layout) []byte { return mutated32(b, item(l), u32(asInt(kd(l)+4))) }},
		{"entry array: slot beyond the arena", func(b []byte, l *layout) []byte { return mutated32(b, slot(l), end(b)) }},
		{"entry array: slot inside the header", func(b []byte, l *layout) []byte { return mutated32(b, slot(l), 16) }},
		{"entry array: slot not 8-aligned", func(b []byte, l *layout) []byte { return mutated32(b, slot(l), u32(asInt(l.entries[2]+4))) }},
		{"entry array: slot beyond the arena in the last array", func(b []byte, l *layout) []byte {
			a := arrays(l)
			return mutated32(b, l.slotAt(a[len(a)-1], 0), end(b))
		}},
	}...)
}

func TestFile_BoundsEveryOffsetCompact(t *testing.T) {
	base, lay, kernel := boundsBaseCompact(30) // the kernel list spans four entry arrays
	if res := walkBytes(base, false); res.err != nil || len(res.entries) != kernel {
		t.Fatalf("the unmodified compact file reads %d entries, error %v, want %d entries", len(res.entries), res.err, kernel)
	}
	for _, c := range compactBoundsCases() {
		for _, online := range []bool{false, true} {
			t.Run(c.name+" online="+strconv.FormatBool(online), func(t *testing.T) {
				b := c.mutate(base, lay)
				if online {
					b[hdrState] = stateOnline
				}
				expectMalformed(t, b)
			})
		}
	}
}

func TestFile_BoundsEveryOffsetSweepCompact(t *testing.T) {
	base, _, _ := boundsBaseCompact(12)
	if res := walkBytes(base, false); res.err != nil || res.panic != nil || len(res.entries) == 0 {
		t.Fatalf("the unmodified compact file: %v, want its kernel entries and no error", res)
	}
	sweepAll(t, base)
}

func TestFile_LayoutFlagFlippedIsNeverAPanic(t *testing.T) {
	for _, c := range []struct {
		name    string
		compact bool
	}{{"non-compact file with the COMPACT bit set", false}, {"compact file with the COMPACT bit cleared", true}} {
		for _, online := range []bool{false, true} {
			t.Run(c.name+" online="+strconv.FormatBool(online), func(t *testing.T) {
				o := testOptions()
				o.compact, o.online = c.compact, online
				b, _ := buildFile(kernelEntries(30, true), o)
				b[hdrIncompat] ^= incompatCompact
				r := newRefReader(b)

				res := walk(r, r.size(), false)

				requireNoPanicNoAbuse(t, res, r)
			})
		}
	}
}

// requireNoPanicNoAbuse wants the walk to end without a panic and without a read the reader flags, and in success or ErrMalformed.
func requireNoPanicNoAbuse(t *testing.T, res walkResult, r *refReader) {
	t.Helper()
	if res.panic != nil {
		t.Fatalf("panic: %v", res.panic)
	}
	if res.err != nil && !errors.Is(res.err, ErrMalformed) {
		t.Errorf("walk error = %v, want nil or %v", res.err, ErrMalformed)
	}
	if r.violated != "" {
		t.Errorf("reader misbehaved: %s", r.violated)
	}
}

func TestFile_RefreshRefusesAFlippedLayoutFlag(t *testing.T) {
	for _, compact := range []bool{false, true} {
		t.Run("compact="+strconv.FormatBool(compact), func(t *testing.T) {
			o := testOptions()
			o.compact, o.online = compact, true
			entries := kernelEntries(30, true)
			want := kernelOnly(entries)
			b, _ := buildFile(entries, o)
			r := newRefReader(b)
			f, err := r.openRef(false)
			if err != nil {
				t.Fatalf("Open() error = %v, want nil", err)
			}
			c := cursorAfter(t, f, 5)

			r.data[hdrIncompat] ^= incompatCompact
			err = f.Refresh(r.size())

			if !errors.Is(err, ErrMalformed) || err.Error() != "journal: malformed file" {
				t.Fatalf("Refresh() after the COMPACT bit changed: error = %v, want %v", err, ErrMalformed)
			}
			requireReadsOn(t, f, &c, len(want))
		})
	}
}

// requireReadsOn wants the cursor c, 5 entries into the kernel list of f, to read the other entries, and a new read to see all.
func requireReadsOn(t *testing.T, f *File, c *Cursor, entries int) {
	t.Helper()
	rest, stop := drain(f, c)
	if stop != nil || len(rest) != entries-5 {
		t.Errorf("reading on with the cursor after the refused Refresh: %d entries, error %v, want the other %d entries and no error", len(rest), stop, entries-5)
	}
	var res walkResult
	if err := readAll(f, kernelList(t, f), 0, &res); err != nil || len(res.entries) != entries {
		t.Errorf("reading again after the refused Refresh: %d entries, error %v, want %d entries", len(res.entries), err, entries)
	}
}

// cursorAfter returns a cursor on the kernel list of f that stands behind its first n entries.
func cursorAfter(t *testing.T, f *File, n int) Cursor {
	t.Helper()
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	for i := 0; i < n; i++ {
		if ok, err := f.Next(&c, wantNames, &e); err != nil || !ok {
			t.Fatalf("Next() %d = %v, %v, want true, nil", i, ok, err)
		}
	}
	return c
}

// tornCompact opens the compact file of 10 kernel entries cut inside (or at the start of) entry 7 and reads its kernel list.
func tornCompact(t *testing.T, o bOptions, into int, setAside bool) (seen []uint64, c Cursor, err error) {
	t.Helper()
	o.compact = true
	_, cut, _ := tornFile(10, 7, into, o)
	f := openBytes(t, cut, setAside)
	c, err = f.Seek(kernelList(t, f), 0)
	if err != nil {
		return nil, c, err
	}
	seen, err = drain(f, &c)
	return seen, c, err
}

func TestFile_TornTailIsEndOfFileCompact(t *testing.T) {
	cases := []struct {
		name     string
		online   bool
		setAside bool
		into     int
		torn     bool
	}{
		{"archived file cut inside an entry", false, false, 20, true},
		{"archived file cut inside the items of an entry", false, false, 66, true},
		{"archived file cut between entries", false, false, 0, false},
		{"set-aside file cut inside an entry", true, true, 20, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			o := testOptions()
			o.online = c.online

			seen, cur, err := tornCompact(t, o, c.into, c.setAside)

			if err != nil {
				t.Fatalf("reading the cut file: error = %v, want the entries before the tear and no error", err)
			}
			if want := firstSeqnums(7); !sameSeqnums(seen, want) {
				t.Errorf("read seqnums %v, want %v (the entries before the torn one)", seen, want)
			}
			if c.torn && !cur.Torn() {
				t.Errorf("Torn() = false, want true after the list ended at the tear")
			}
		})
	}
}

func TestFile_TornTailOfAnOnlineFileIsMalformedCompact(t *testing.T) {
	o := testOptions()
	o.online = true

	seen, _, err := tornCompact(t, o, 20, false)

	if !errors.Is(err, ErrMalformed) {
		t.Errorf("reading an online compact file cut inside an entry: error = %v (%d entries), want %v", err, len(seen), ErrMalformed)
	}
	if len(seen) > 7 {
		t.Errorf("read %d entries, want at most the 7 before the tear", len(seen))
	}
}

func TestFile_ReadsFieldNamesUpToTheNameWindowInBothLayouts(t *testing.T) {
	for _, compact := range []bool{false, true} {
		for _, n := range []int{8, 55, 56, 57, 60, 63} {
			t.Run("compact="+strconv.FormatBool(compact)+" name of "+strconv.Itoa(n)+" bytes", func(t *testing.T) {
				name := strings.Repeat("N", n)
				entries := kernelEntries(3, false)
				entries[1].items = append(entries[1].items, plain(name+"=long name value"))
				o := testOptions()
				o.compact = compact
				b, _ := buildFile(entries, o)

				viaNext, viaNextWith := namedValues(t, openBytes(t, b, false), name)

				if !slices.Equal(viaNext, []string{"long name value"}) {
					t.Errorf("Next() found the values %q of the %d-byte name, want [long name value]", viaNext, n)
				}
				if !slices.Equal(viaNextWith, []string{"long name value"}) {
					t.Errorf("NextWith() found the values %q of the %d-byte name, want [long name value]", viaNextWith, n)
				}
			})
		}
	}
}

// namedValues reads the three entries of the file with Next (kernel list) and with NextWith (all entries) and returns the values of the
// field name each one found.
func namedValues(t *testing.T, f *File, name string) (viaNext, viaNextWith []string) {
	t.Helper()
	want := []string{name}
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	for i := 0; i < 3; i++ {
		if ok, err := f.Next(&c, want, &e); err != nil || !ok {
			t.Fatalf("Next() %d = %v, %v, want true, nil", i, ok, err)
		}
		viaNext = append(viaNext, valuesOf(&e, name)...)
	}
	all, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll() error = %v, want nil", err)
	}
	for i := 0; i < 3; i++ {
		nextWith(t, f, &all, want, &e, readOptions(nil))
		viaNextWith = append(viaNextWith, valuesOf(&e, name)...)
	}
	return viaNext, viaNextWith
}

func valuesOf(e *Entry, name string) []string {
	var out []string
	fields, _ := e.Field(name)
	for _, f := range fields {
		out = append(out, string(f.Value))
	}
	return out
}

// stampKey is the pair of stamps that identifies an entry of the exports.
type stampKey struct{ realtime, monotonic uint64 }

func sampleDir() string { return filepath.Join(fixtureDir(), "systemd-255") }

// exportByStamps reads the export file name of testdata/journal and returns its entries by their stamps.
func exportByStamps(t *testing.T, name string) map[stampKey][]exField {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(fixtureDir(), name))
	if err != nil {
		t.Fatalf("reading %s: %v", name, err)
	}
	out := map[stampKey][]exField{}
	err = readExport(bytes.NewReader(data), func(fields []exField) {
		rt, rerr := strconv.ParseUint(string(exValue(fields, "__REALTIME_TIMESTAMP")), 10, 64)
		mono, merr := strconv.ParseUint(string(exValue(fields, "__MONOTONIC_TIMESTAMP")), 10, 64)
		if rerr != nil || merr != nil {
			t.Fatalf("%s: an entry has no stamps", name)
		}
		out[stampKey{rt, mono}] = fields
	})
	if err != nil || len(out) == 0 {
		t.Fatalf("reading %s: %d entries, error %v", name, len(out), err)
	}
	return out
}

// exValue returns the value of the first field with the name, nil when there is none.
func exValue(fields []exField, name string) []byte {
	for _, f := range fields {
		if f.name == name {
			return f.value
		}
	}
	return nil
}

func exCount(fields []exField, name string) int {
	n := 0
	for _, f := range fields {
		if f.name == name {
			n++
		}
	}
	return n
}

// openSample opens a real journal file of testdata/journal/systemd-255 and returns it with its bytes and its reader.
func openSample(t *testing.T, name string) (*File, []byte, *refReader) {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(sampleDir(), name))
	if err != nil {
		t.Fatalf("reading the sample %s: %v", name, err)
	}
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open(%s) error = %v, want nil", name, err)
	}
	return f, b, r
}

func TestFile_ReadsTheRealCompactKernelSample(t *testing.T) {
	export := exportByStamps(t, "kernel.export")
	f, raw, r := openSample(t, "kernel.journal")
	if n := le.Uint64(raw[hdrNEntries:]); n != 100 {
		t.Fatalf("the sample counts %d entries in its header, want 100", n)
	}
	d := kernelList(t, f)
	c, err := f.Seek(d, 0)
	if err != nil {
		t.Fatalf("Seek(0) error = %v, want nil", err)
	}

	var seqs, kills []uint64
	var e Entry
	for {
		ok, err := f.Next(&c, []string{"MESSAGE", "SYSLOG_FACILITY"}, &e)
		if err != nil {
			t.Fatalf("Next() after %d entries: error = %v, want nil", len(seqs), err)
		}
		if !ok {
			break
		}
		seqs = append(seqs, e.Seqnum)
		if checkSampleEntry(t, &e, export) {
			kills = append(kills, e.Seqnum)
		}
	}

	if len(seqs) != 95 || !slices.IsSorted(seqs) || len(slices.Compact(slices.Clone(seqs))) != 95 {
		t.Fatalf("read %d kernel entries with the seqnums %v, want 95 in strictly increasing order", len(seqs), seqs)
	}
	if want := []uint64{4, 10, 14, 20, 25, 29, 97, 100}; !slices.Equal(kills, want) {
		t.Errorf("entries with one facility 0 and a kill message = %v, want %v", kills, want)
	}
	if c.Torn() {
		t.Error("Torn() = true after the end of the list, want false for a whole file")
	}
	requireSeekAfterEverySeqnum(t, f, d, seqs)
	if r.violated != "" {
		t.Errorf("reader misbehaved: %s", r.violated)
	}
}

// checkSampleEntry compares an entry with the export entry of the same stamps and reports whether it is a kill line.
func checkSampleEntry(t *testing.T, e *Entry, export map[stampKey][]exField) bool {
	t.Helper()
	pair, ok := export[stampKey{e.RealtimeUS, e.MonotonicUS}]
	if !ok {
		t.Fatalf("entry %d (realtime %d, monotonic %d) has no entry of the same stamps in kernel.export", e.Seqnum, e.RealtimeUS, e.MonotonicUS)
	}
	if got, want := hex.EncodeToString(e.BootID[:]), string(exValue(pair, "_BOOT_ID")); got != want {
		t.Errorf("entry %d boot ID = %s, want %s", e.Seqnum, got, want)
	}
	msg, _ := e.Field("MESSAGE")
	if len(msg) != 1 || !bytes.Equal(msg[0].Value, exValue(pair, "MESSAGE")) {
		t.Errorf("entry %d holds %d MESSAGE fields, want 1 equal to %q", e.Seqnum, len(msg), exValue(pair, "MESSAGE"))
		return false
	}
	fac, _ := e.Field("SYSLOG_FACILITY")
	if len(fac) != exCount(pair, "SYSLOG_FACILITY") {
		t.Errorf("entry %d has %d SYSLOG_FACILITY items, want %d", e.Seqnum, len(fac), exCount(pair, "SYSLOG_FACILITY"))
	}
	return len(fac) == 1 && string(fac[0].Value) == "0" && strings.HasPrefix(string(msg[0].Value), "Out of memory: Killed process ")
}

// requireSeekAfterEverySeqnum wants Seek(d, s) to stand on the first entry of seqs after s, for every s up to the last one.
func requireSeekAfterEverySeqnum(t *testing.T, f *File, d Data, seqs []uint64) {
	t.Helper()
	last := seqs[len(seqs)-1]
	var e Entry
	for s := uint64(0); s <= last; s++ {
		c, err := f.Seek(d, s)
		if err != nil {
			t.Fatalf("Seek(%d) error = %v, want nil", s, err)
		}
		ok, err := f.Next(&c, []string{"MESSAGE"}, &e)
		want, _ := slices.BinarySearch(seqs, s+1) // the index of the first seqnum of seqs that is at least s+1
		if want < len(seqs) {
			if err != nil || !ok || e.Seqnum != seqs[want] {
				t.Fatalf("Seek(%d) then Next() = seqnum %d, %v, %v, want seqnum %d", s, e.Seqnum, ok, err, seqs[want])
			}
			continue
		}
		if err != nil || ok {
			t.Fatalf("Seek(%d) then Next() = %v, %v, want the end of the list", s, ok, err)
		}
	}
}

var perfFiles = map[int][]byte{}

// perfFile returns a file of n kernel entries of about 150 bytes of message each.
func perfFile(n int) []byte {
	if b, ok := perfFiles[n]; ok {
		return b
	}
	entries := make([]bEntry, n)
	for i := range entries {
		entries[i] = kernelEntry(i, 10, 120)
	}
	o := testOptions()
	o.buckets = 3 * n
	b, _ := buildFile(entries, o)
	perfFiles[n] = b
	return b
}

func heapAfterGC() int64 {
	runtime.GC()
	var m runtime.MemStats
	runtime.ReadMemStats(&m)
	return i64(m.HeapAlloc)
}

// readWithHeapSamples reads the whole kernel list and returns the entries read and the largest growth of the live heap seen.
func readWithHeapSamples(t *testing.T, f *File, before int64) (count int, peak int64) {
	t.Helper()
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	for {
		ok, err := f.Next(&c, wantNames, &e)
		if err != nil {
			t.Fatalf("Next() after %d entries: error = %v, want nil", count, err)
		}
		if !ok {
			break
		}
		count++
		if count%10000 == 0 {
			peak = max(peak, heapAfterGC()-before)
		}
	}
	peak = max(peak, heapAfterGC()-before)
	runtime.KeepAlive(&e)
	return count, peak
}

func TestFile_MemoryDoesNotGrowWithJournalSize(t *testing.T) {
	const bound = 4 << 20
	for _, n := range []int{1000, 100000} {
		t.Run(strconv.Itoa(n)+" kernel entries", func(t *testing.T) {
			b := perfFile(n)
			before := heapAfterGC()
			f, err := newRefReader(b).openRef(false)
			if err != nil {
				t.Fatalf("Open() error = %v, want nil", err)
			}
			count, peak := readWithHeapSamples(t, f, before)
			runtime.KeepAlive(f)
			if count != n {
				t.Fatalf("read %d entries, want %d", count, n)
			}
			if peak > bound {
				t.Errorf("live heap grew by %d bytes while reading %d entries, want at most %d (independent of the journal's size)", peak, n, bound)
			}
		})
	}
}

func TestFile_SeekAndNextWorkDoNotGrowWithEarlierEntries(t *testing.T) {
	const budget = 400
	for _, n := range []int{1000, 100000} {
		t.Run(strconv.Itoa(n)+" kernel entries", func(t *testing.T) {
			r := newRefReader(perfFile(n))
			readLastEntry(t, r, 10+3*u64(n-1))
			if r.reads > budget {
				t.Errorf("Open, Lookup, Seek to the last entry and two Next made %d reads on %d entries, want at most %d", r.reads, n, budget)
			}
			if r.violated != "" {
				t.Errorf("reader misbehaved: %s", r.violated)
			}
		})
	}
}

// readLastEntry seeks to the entry with seqnum last, the final one, reads it and reads past the end.
func readLastEntry(t *testing.T, r *refReader, last uint64) {
	t.Helper()
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	c, err := f.Seek(kernelList(t, f), last-3)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	if ok, err := f.Next(&c, wantNames, &e); err != nil || !ok || e.Seqnum != last {
		t.Fatalf("Next() = %v, %v with seqnum %d, want true, nil, %d", ok, err, e.Seqnum, last)
	}
	if ok, err := f.Next(&c, wantNames, &e); err != nil || ok {
		t.Fatalf("second Next() = %v, %v, want false, nil", ok, err)
	}
}

// omittedOf reads Entry.Omitted by reflection so that the suite builds before the field exists.
func omittedOf(t *testing.T, e *Entry) int {
	t.Helper()
	f := reflect.ValueOf(e).Elem().FieldByName("Omitted")
	if !f.IsValid() || f.Kind() != reflect.Int {
		t.Fatalf("Entry has no int field Omitted")
	}
	return int(f.Int())
}

func TestFile_FieldsBeyondCapAreCounted(t *testing.T) {
	const keep = 16
	cases := []struct {
		name string
		item func(i int) bItem
	}{
		{"wanted items", func(i int) bItem { return bItem{payload: []byte("SYSLOG_FACILITY=" + strconv.Itoa(i))} }},
		{"compressed items", func(i int) bItem {
			return bItem{payload: []byte("X" + strconv.Itoa(i) + "=" + strings.Repeat("handmade ", 8)), compressed: true}
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			entries := kernelEntries(3, false)
			// the second entry keeps only items that are not wanted, plus 17 of the kind under test
			entries[1].items = []bItem{{payload: []byte(kernelPayload)}, {payload: []byte("PRIORITY=6")}}
			for i := 0; i < keep+1; i++ {
				entries[1].items = append(entries[1].items, c.item(i))
			}
			b, _ := buildFile(entries, testOptions())
			f := openBytes(t, b, false)
			cur, err := f.Seek(kernelList(t, f), 0)
			if err != nil {
				t.Fatalf("Seek() error = %v, want nil", err)
			}
			var e Entry
			for i := 0; i < 3; i++ {
				if ok, err := f.Next(&cur, wantNames, &e); err != nil || !ok {
					t.Fatalf("Next() %d = %v, %v, want true, nil", i, ok, err)
				}
				checkOmitted(t, i, &e, keep)
			}
		})
	}
}

func checkOmitted(t *testing.T, i int, e *Entry, keep int) {
	t.Helper()
	got := omittedOf(t, e)
	if i == 1 && (len(e.Fields) != keep || got != 1) {
		t.Errorf("entry with %d items of one kind: %d Fields, Omitted %d, want %d and 1", keep+1, len(e.Fields), got, keep)
	}
	if i != 1 && got != 0 {
		t.Errorf("entry %d: Omitted = %d, want 0 (reset for each entry)", i, got)
	}
}

var errBoom = errors.New("boom: device gone")

// failingReader serves b but fails every read after the first okReads ones with errBoom.
type failingReader struct {
	r       *refReader
	okReads int
	calls   int
}

func (f *failingReader) ReadAt(p []byte, off int64) (int, error) {
	f.calls++
	if f.calls > f.okReads {
		return 0, errBoom
	}
	return f.r.ReadAt(p, off)
}

func TestFile_ReaderFailureIsErrReadWithoutDetails(t *testing.T) {
	b, _ := buildFile(kernelEntries(20, false), testOptions())
	sawRead := false
	for ok := 0; ok < 40; ok++ {
		fr := &failingReader{r: newRefReader(b), okReads: ok}
		res := walk(fr, int64(len(b)), false)
		if res.panic != nil {
			t.Fatalf("after %d good reads: panic %v", ok, res.panic)
		}
		if res.err == nil {
			continue
		}
		if !errors.Is(res.err, ErrRead) || !errors.Is(res.err, errBoom) {
			t.Fatalf("after %d good reads: error %v, want one that is %v and wraps the reader's error", ok, res.err, ErrRead)
		}
		if res.err.Error() != "journal: read failed" {
			t.Fatalf("error text = %q, want %q with no path, offset or cause", res.err.Error(), "journal: read failed")
		}
		sawRead = true
	}
	if !sawRead {
		t.Errorf("no failing read surfaced as %v", ErrRead)
	}
}

func TestFile_StampsAreKeptAsStoredAndConvertedInRange(t *testing.T) {
	const firstBad = 253402300800000000
	maxMono := uint64(math.MaxInt64 / 1000)
	cases := []struct {
		name         string
		real, mono   uint64
		realZero     bool
		monotonicZer bool
	}{
		{"ordinary", testBase, testMono, false, false},
		{"real-time zero", 0, testMono, true, false},
		{"real-time first bad microsecond", firstBad, testMono, true, false},
		{"real-time last good microsecond", firstBad - 1, testMono, false, false},
		{"real-time 1<<62", 1 << 62, testMono, true, false},
		{"real-time all ones", ^uint64(0), testMono, true, false},
		{"monotonic at the limit", testBase, maxMono, false, false},
		{"monotonic one above the limit", testBase, maxMono + 1, false, true},
		{"monotonic all ones", testBase, ^uint64(0), false, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			e := readStampedEntry(t, c.real, c.mono)
			if e.RealtimeUS != c.real || e.MonotonicUS != c.mono {
				t.Errorf("RealtimeUS, MonotonicUS = %d, %d, want the stored %d, %d", e.RealtimeUS, e.MonotonicUS, c.real, c.mono)
			}
			if e.Realtime.IsZero() != c.realZero || (!c.realZero && !e.Realtime.Equal(microTime(c.real))) {
				t.Errorf("Realtime = %v, want zero %v (stamp %d)", e.Realtime, c.realZero, c.real)
			}
			if wantMono := wantMonotonic(c.mono, c.monotonicZer); e.Monotonic != wantMono {
				t.Errorf("Monotonic = %v, want %v (stamp %d)", e.Monotonic, wantMono, c.mono)
			}
		})
	}
}

func wantMonotonic(us uint64, zero bool) time.Duration {
	if zero {
		return 0
	}
	return time.Duration(i64(us)) * time.Microsecond
}

// readStampedEntry builds a one-entry file with the given stamps and returns the entry as read.
func readStampedEntry(t *testing.T, realUS, mono uint64) Entry {
	t.Helper()
	entries := kernelEntries(1, false)
	entries[0].realtime, entries[0].mono = realUS, mono
	b, _ := buildFile(entries, testOptions())
	f := openBytes(t, b, false)
	cur, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	var e Entry
	if ok, err := f.Next(&cur, wantNames, &e); err != nil || !ok {
		t.Fatalf("Next() = %v, %v, want true, nil", ok, err)
	}
	return e
}

func TestLookup_DoesNotFindLookalikes(t *testing.T) {
	want := []byte("AAAA=1")
	hash := refSipHash24(testFileID, want)
	entries := kernelEntries(1, false)
	entries[0].items = append(entries[0].items, bItem{payload: []byte("AAAA=2")}) // stored with the hash of AAAA=1
	o := testOptions()
	o.hash = func(p []byte) uint64 {
		if string(p) == "AAAA=2" {
			return hash
		}
		return refSipHash24(testFileID, p)
	}
	o.buckets = 1
	b, _ := buildFile(entries, o)
	f := openBytes(t, b, false)
	for name, p := range map[string][]byte{"same hash and size, other bytes": want, "empty": nil, "too long": make([]byte, 5000), "absent": []byte("ZZZZ=9")} {
		if _, found, err := f.Lookup(p); found || err != nil {
			t.Errorf("%s: Lookup() = found %v, err %v, want found false and no error", name, found, err)
		}
	}
}

func TestLookup_SkipsCompressedObjectsOfTheSameHash(t *testing.T) {
	plain := []byte("MESSAGE=" + strings.Repeat("x", 300))
	entries := kernelEntries(1, false)
	entries[0].items = append(entries[0].items, bItem{payload: plain, compressed: true})
	b, _ := buildFile(entries, testOptions())
	if _, found, err := openBytes(t, b, false).Lookup(plain); found || err != nil {
		t.Errorf("Lookup() of a payload stored compressed = found %v, err %v, want found false and no error", found, err)
	}
}

func TestLookup_RefusesAnUnusableHashTable(t *testing.T) {
	b, _ := buildFile(kernelEntries(2, false), testOptions())
	for name, size := range map[string]uint64{"zero": 0, "not a multiple of 16": 24} {
		f := openBytes(t, mutated(b, hdrDataTableSz, size), false)
		if _, _, err := f.Lookup([]byte(kernelPayload)); !errors.Is(err, ErrMalformed) {
			t.Errorf("table size %s: Lookup() error = %v, want %v", name, err, ErrMalformed)
		}
	}
}

func TestListFiles_SelectsJournalFiles(t *testing.T) {
	fsys := fstest.MapFS{
		"system.journal":        {Data: []byte("x")},
		"system@a-1-2.journal":  {Data: []byte("x")},
		"system@a-1-2.journal~": {Data: []byte("x")},
		"system@b-1-2.journal":  {Data: []byte("x")},
		"user-1000.journal":     {Data: []byte("x")},
		"system.journal~":       {Data: []byte("x")},
		"system@c.txt":          {Data: []byte("x")},
		"system@d.journal.bak":  {Data: []byte("x")},
		"sub/system.journal":    {Data: []byte("x")},
		"system@dir.journal/x":  {Data: []byte("x")},
	}
	got, err := ListFiles(fsys)
	want := []string{"system.journal", "system@a-1-2.journal", "system@a-1-2.journal~", "system@b-1-2.journal"}
	if err != nil || strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("ListFiles() = %v, %v, want %v", got, err, want)
	}
}

func TestListFiles_ReadsAtMost1024FilesInChunks(t *testing.T) {
	fsys := fstest.MapFS{}
	for i := 0; i < 1100; i++ {
		fsys["system@"+strconv.Itoa(1000+i)+".journal"] = &fstest.MapFile{Data: []byte("x")}
	}
	got, err := ListFiles(fsys)
	if err != nil || len(got) != 1024 {
		t.Errorf("ListFiles() = %d files, %v, want 1024 and no error", len(got), err)
	}
}

type brokenFS struct{ openErr error }

func (b brokenFS) Open(string) (fs.File, error) { return nil, b.openErr }

type plainFile struct{}

func (plainFile) Stat() (fs.FileInfo, error) { return nil, errBoom }
func (plainFile) Read([]byte) (int, error)   { return 0, io.EOF }
func (plainFile) Close() error               { return nil }

type notListableFS struct{}

func (notListableFS) Open(string) (fs.File, error) { return plainFile{}, nil }

type failingDir struct{ plainFile }

func (failingDir) ReadDir(int) ([]fs.DirEntry, error) { return nil, errBoom }

type failingDirFS struct{}

func (failingDirFS) Open(string) (fs.File, error) { return failingDir{}, nil }

func TestListFiles_ReportsADirectoryThatCannotBeRead(t *testing.T) {
	cases := map[string]fs.FS{
		"open fails":    brokenFS{openErr: fs.ErrPermission},
		"not listable":  notListableFS{},
		"listing fails": failingDirFS{},
	}
	for name, fsys := range cases {
		if got, err := ListFiles(fsys); err == nil || got != nil {
			t.Errorf("%s: ListFiles() = %v, %v, want nil and an error", name, got, err)
		}
	}
}

func TestMachineDir_TakesOnlyThirtyTwoLowerCaseHexDigits(t *testing.T) {
	const id = "0badc0de0badc0de0badc0de0badc0de"
	cases := []struct {
		name string
		data string
		ok   bool
	}{
		{"with line feed", id + "\n", true},
		{"without line feed", id, true},
		{"upper case", strings.ToUpper(id), false},
		{"31 digits", id[1:], false},
		{"33 digits", id + "0", false},
		{"not hex", strings.Repeat("g", 32), false},
		{"two lines", id + "\n" + id + "\n", false},
		{"two line feeds", id + "\n\n", false},
		{"empty", "", false},
		{"huge", strings.Repeat("a", 1<<20), false},
		{"path", "../../" + id[6:], false},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			got, err := MachineDir(fstest.MapFS{"machine-id": {Data: []byte(c.data)}})
			if c.ok && (err != nil || got != id) {
				t.Errorf("MachineDir() = %q, %v, want %q", got, err, id)
			}
			if !c.ok && (err == nil || got != "") {
				t.Errorf("MachineDir() = %q, %v, want an error", got, err)
			}
		})
	}
	if got, err := MachineDir(fstest.MapFS{}); err == nil || got != "" {
		t.Errorf("MachineDir() without the file = %q, %v, want an error", got, err)
	}
}

func TestFile_ArraySlotReportsAShortReadInBothLayouts(t *testing.T) {
	for _, compact := range []bool{false, true} {
		t.Run("compact="+strconv.FormatBool(compact), func(t *testing.T) {
			o := testOptions()
			o.compact = compact
			b, lay := buildFile(kernelEntries(10, true), o)
			r := newRefReader(b)
			f, err := r.openRef(false)
			if err != nil {
				t.Fatalf("Open() error = %v, want nil", err)
			}
			arr := lay.dataArrays[kernelPayload][0]

			if got, err := f.arraySlot(arr, 0); err != nil || !slices.Contains(lay.entries, got) {
				t.Fatalf("arraySlot(first array, 0) = %d, %v, want the offset of an entry of %v and no error", got, err, lay.entries)
			}
			r.limit = int(arr) + arrayFixed // the file ends where the first slot would start
			got, err := f.arraySlot(arr, 0)

			if !errors.Is(err, errTorn) || got != 0 {
				t.Errorf("arraySlot() on a file cut before the slot = %d, %v, want 0 and %v", got, err, errTorn)
			}
		})
	}
}

func TestFile_RefreshKeepsTheFileWhenTheHeaderIsRefused(t *testing.T) {
	for _, compact := range []bool{false, true} {
		t.Run("compact="+strconv.FormatBool(compact), func(t *testing.T) {
			o := testOptions()
			o.compact = compact
			b, _ := buildFile(kernelEntries(10, true), o)
			f, err := newRefReader(b).openRef(false)
			if err != nil {
				t.Fatalf("Open() error = %v, want nil", err)
			}
			before := f.Header()

			err = f.Refresh(minHeaderSize - 1)

			if !errors.Is(err, ErrUnsupported) {
				t.Errorf("Refresh() of a size below the header = %v, want %v", err, ErrUnsupported)
			}
			if f.Header() != before || len(kernelListEntries(t, f)) != 10 {
				t.Errorf("after the refused Refresh the header is %v, want the unchanged %v and all 10 entries readable", f.Header(), before)
			}
		})
	}
}

// kernelListEntries reads every entry of the kernel list of f.
func kernelListEntries(t *testing.T, f *File) []uint64 {
	t.Helper()
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	seen, stop := drain(f, &c)
	if stop != nil {
		t.Fatalf("Next() error = %v, want nil", stop)
	}
	return seen
}
