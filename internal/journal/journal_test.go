package journal

import (
	"errors"
	"reflect"
	"runtime"
	"strconv"
	"strings"
	"testing"
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
		{"compact layout", setIncompat(incompatKeyed | incompatZSTD | incompatCompact)},
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
