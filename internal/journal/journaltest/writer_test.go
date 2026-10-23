package journaltest

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/klauspost/compress/zstd"

	"github.com/LarsLaskowski/Vandox/internal/journal"
)

const (
	bootA = "c0ffee00c0ffee00c0ffee00c0ffee01"
	bootB = "c0ffee00c0ffee00c0ffee00c0ffee02"
	id1   = "00000000000000000000000000000001"
	id2   = "00000000000000000000000000000002"

	archivedName = "system@00000000000000000000000000000001-0000000000000001-0005f8d1b1a2c3d4.journal"
	onlineName   = "system.journal"
	setAsideName = "system@00000000000000000000000000000002-0000000000000001-0005f8d1b1a2c3d5.journal~"
)

type entry = map[string][]byte

func fixtureDir() string { return filepath.Join("..", "..", "..", "testdata", "journal") }

func readFixtureExport(t *testing.T) []entry {
	t.Helper()
	f, err := os.Open(filepath.Join(fixtureDir(), "kernel.export"))
	if err != nil {
		t.Fatalf("opening the export: %v", err)
	}
	defer func() { _ = f.Close() }()
	es, err := ReadExport(f)
	if err != nil {
		t.Fatalf("ReadExport(kernel.export) error = %v, want nil", err)
	}
	return es
}

func mustID(t *testing.T, s string) [16]byte {
	t.Helper()
	var id [16]byte
	b, err := hex.DecodeString(s)
	if err != nil || len(b) != 16 {
		t.Fatalf("bad ID %q", s)
	}
	copy(id[:], b)
	return id
}

// fixtureFile is the part of the export that one fixture file holds.
type fixtureFile struct {
	name    string
	entries []entry
	opts    Options
}

// fixtureFiles splits the export by the seqnum ID and the boot of each entry: ID 2 is the set-aside file, ID 1 with boot A the
// archived file, ID 1 with boot B the online one.
func fixtureFiles(t *testing.T, all []entry) []fixtureFile {
	t.Helper()
	files := []fixtureFile{
		{name: archivedName, opts: Options{FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), SeqnumID: mustID(t, id1)}},
		{name: onlineName, opts: Options{FileID: mustID(t, "b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1b1"), SeqnumID: mustID(t, id1), Online: true}},
		{name: setAsideName, opts: Options{FileID: mustID(t, "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1"), SeqnumID: mustID(t, id2)}},
	}
	for _, e := range all {
		switch {
		case string(e["__SEQNUM_ID"]) == id2:
			files[2].entries = append(files[2].entries, e)
		case string(e["_BOOT_ID"]) == bootA:
			files[0].entries = append(files[0].entries, e)
		case string(e["_BOOT_ID"]) == bootB:
			files[1].entries = append(files[1].entries, e)
		default:
			t.Fatalf("export entry with seqnum %s belongs to no fixture file", e["__SEQNUM"])
		}
	}
	for i := range files {
		files[i].opts.MachineID = mustID(t, string(all[0]["_MACHINE_ID"]))
		if len(files[i].entries) == 0 {
			t.Fatalf("no export entry for %s", files[i].name)
		}
	}
	return files
}

func TestReadExport_ParsesTextBinaryAndRepeatedFields(t *testing.T) {
	var in bytes.Buffer
	in.WriteString("A=1\nB=x=y\nEMPTY=\nSYSLOG_FACILITY=0\nSYSLOG_FACILITY=3\nSYSLOG_FACILITY=5\n\n")
	in.WriteString("MESSAGE\n")
	_ = binary.Write(&in, binary.LittleEndian, uint64(9))
	in.WriteString("one\ntwo\x00\xff\nC=2\n\n")
	got, err := ReadExport(&in)
	if err != nil {
		t.Fatalf("ReadExport() error = %v, want nil", err)
	}
	want := []entry{
		{"A": []byte("1"), "B": []byte("x=y"), "EMPTY": {}, "SYSLOG_FACILITY": []byte("0"), "SYSLOG_FACILITY#2": []byte("3"), "SYSLOG_FACILITY#3": []byte("5")},
		{"MESSAGE": []byte("one\ntwo\x00\xff"), "C": []byte("2")},
	}
	if len(got) != len(want) {
		t.Fatalf("ReadExport() = %d entries, want %d", len(got), len(want))
	}
	for i := range want {
		if d := diffEntries(want[i], got[i]); d != "" {
			t.Errorf("entry %d: %s", i, d)
		}
	}
}

func TestReadExport_RefusesTruncatedInput(t *testing.T) {
	var short bytes.Buffer
	short.WriteString("MESSAGE\n")
	_ = binary.Write(&short, binary.LittleEndian, uint64(100))
	short.WriteString("only ten b")
	cases := map[string]string{
		"binary field cut short":           short.String(),
		"binary length cut short":          "MESSAGE\n\x05\x00",
		"binary field without a line feed": "MESSAGE\n\x03\x00\x00\x00\x00\x00\x00\x00abcX",
	}
	if got, err := ReadExport(strings.NewReader("MESSAGE\n\x03\x00\x00\x00\x00\x00\x00\x00abc\n\n")); err != nil || len(got) != 1 || string(got[0]["MESSAGE"]) != "abc" {
		t.Fatalf("ReadExport() of a complete binary field = %v, %v, want one entry with MESSAGE abc", got, err)
	}
	for name, in := range cases {
		if got, err := ReadExport(strings.NewReader(in)); err == nil {
			t.Errorf("%s: ReadExport() = %d entries, nil error, want an error", name, len(got))
		}
	}
}

func TestReadExport_ReadsTheFixture(t *testing.T) {
	es := readFixtureExport(t)
	if len(es) < 30 {
		t.Fatalf("the export has %d entries, want the real lines and the hand-made entries", len(es))
	}
	var unmarked, multi, twoFac int
	for i, e := range es {
		if string(e["VANDOX_FIXTURE"]) != "handmade" {
			unmarked++
		}
		if bytes.Contains(e["MESSAGE"], []byte("\n")) {
			multi++
		}
		if _, ok := e["SYSLOG_FACILITY#2"]; ok {
			twoFac++
		}
		checkRequiredKeys(t, i, e)
	}
	if unmarked != 7 {
		t.Errorf("%d entries without VANDOX_FIXTURE=handmade, want the 7 real lines (3 kills, 3 invoked, 1 reaper)", unmarked)
	}
	if multi != 1 || twoFac != 1 {
		t.Errorf("entries with a line feed in MESSAGE = %d, with two SYSLOG_FACILITY items = %d, want 1 and 1", multi, twoFac)
	}
}

func checkRequiredKeys(t *testing.T, i int, e entry) {
	t.Helper()
	for _, k := range []string{"__REALTIME_TIMESTAMP", "__MONOTONIC_TIMESTAMP", "__SEQNUM", "__SEQNUM_ID", "_BOOT_ID", "MESSAGE"} {
		if len(e[k]) == 0 {
			t.Errorf("entry %d has no %s", i, k)
		}
	}
}

func diffEntries(want, got entry) string {
	var keys []string
	for k := range want {
		keys = append(keys, k)
	}
	for k := range got {
		if _, ok := want[k]; !ok {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	for _, k := range keys {
		w, wok := want[k]
		g, gok := got[k]
		if wok != gok || !bytes.Equal(w, g) {
			return "field " + k + ": got " + strconv.Quote(string(g)) + " (present " + strconv.FormatBool(gok) + "), want " + strconv.Quote(string(w)) + " (present " + strconv.FormatBool(wok) + ")"
		}
	}
	return ""
}

// nameValue is one field of `journalctl -o export` output.
type nameValue struct {
	name  string
	value []byte
}

// splitExport splits journalctl export output into entries of fields in output order.
func splitExport(out []byte) ([][]nameValue, error) {
	br := bufio.NewReader(bytes.NewReader(out))
	var res [][]nameValue
	var cur []nameValue
	for {
		line, err := br.ReadBytes('\n')
		if len(line) == 0 && errors.Is(err, io.EOF) {
			break
		}
		line = bytes.TrimSuffix(line, []byte("\n"))
		if len(line) == 0 {
			res, cur = append(res, cur), nil
			continue
		}
		nv, perr := parseLine(br, line)
		if perr != nil {
			return nil, perr
		}
		cur = append(cur, nv)
	}
	if len(cur) > 0 {
		res = append(res, cur)
	}
	return res, nil
}

func parseLine(br *bufio.Reader, line []byte) (nameValue, error) {
	if i := bytes.IndexByte(line, '='); i >= 0 {
		return nameValue{string(line[:i]), append([]byte(nil), line[i+1:]...)}, nil
	}
	var n [8]byte
	if _, err := io.ReadFull(br, n[:]); err != nil {
		return nameValue{}, err
	}
	v := make([]byte, binary.LittleEndian.Uint64(n[:]))
	if _, err := io.ReadFull(br, v); err != nil {
		return nameValue{}, err
	}
	_, err := br.ReadByte()
	return nameValue{string(line), v}, err
}

// parseJournalctl reads `journalctl -o export` output into entries in the same shape as ReadExport (a repeated field N is N, N#2, ..),
// without the __CURSOR field.
func parseJournalctl(t *testing.T, out []byte) []entry {
	t.Helper()
	split, err := splitExport(out)
	if err != nil {
		t.Fatalf("journalctl output: %v", err)
	}
	res := make([]entry, 0, len(split))
	for _, fields := range split {
		e := entry{}
		count := map[string]int{}
		for _, f := range fields {
			if f.name == "__CURSOR" {
				continue
			}
			count[f.name]++
			name := f.name
			if count[name] > 1 {
				name += "#" + strconv.Itoa(count[name])
			}
			e[name] = f.value
		}
		res = append(res, e)
	}
	return res
}

func writeFixtureFiles(t *testing.T, dir string, files []fixtureFile) {
	t.Helper()
	for _, f := range files {
		var buf bytes.Buffer
		if err := Write(&buf, f.entries, f.opts); err != nil {
			t.Fatalf("Write(%s) error = %v, want nil", f.name, err)
		}
		if err := os.WriteFile(filepath.Join(dir, f.name), buf.Bytes(), 0o600); err != nil {
			t.Fatal(err)
		}
	}
}

func TestWrite_JournalctlAcceptsFixture(t *testing.T) {
	if _, err := exec.LookPath("journalctl"); err != nil {
		t.Skip("journalctl is not installed")
	}
	files := fixtureFiles(t, readFixtureExport(t))
	dir := t.TempDir()
	writeFixtureFiles(t, dir, files)
	for _, f := range files {
		path := filepath.Join(dir, f.name)
		if out, err := exec.Command("journalctl", "--verify", "--file="+path).CombinedOutput(); err != nil {
			t.Errorf("journalctl --verify of %s failed: %v\n%s", f.name, err, out)
		}
		out, err := exec.Command("journalctl", "--file="+path, "-o", "export", "--no-pager").Output()
		if err != nil {
			t.Errorf("journalctl -o export of %s failed: %v", f.name, err)
			continue
		}
		got := parseJournalctl(t, out)
		if len(got) != len(f.entries) {
			t.Errorf("%s: journalctl exports %d entries, want %d", f.name, len(got), len(f.entries))
			continue
		}
		for i := range got {
			if d := diffEntries(f.entries[i], got[i]); d != "" {
				t.Errorf("%s: entry %d (seqnum %s): %s", f.name, i, f.entries[i]["__SEQNUM"], d)
			}
		}
	}
}

// dataObjects walks the objects of a written file and returns the flags and stored payload of every data object.
func dataObjects(t *testing.T, b []byte) (flags []byte, payloads [][]byte) {
	t.Helper()
	hs := binary.LittleEndian.Uint64(b[88:])
	end := min(uint64(len(b)), hs+binary.LittleEndian.Uint64(b[96:]))
	for off := hs; off+16 <= end; {
		size := binary.LittleEndian.Uint64(b[off+8:])
		if size < 16 || off+size > end {
			t.Fatalf("object at %d has size %d, want a well-formed file", off, size)
		}
		if b[off] == 1 {
			flags = append(flags, b[off+1])
			payloads = append(payloads, b[off+64:off+size])
		}
		off += (size + 7) &^ 7
	}
	return flags, payloads
}

func TestWrite_StoresLongFieldsAsZstdFramesOfRawBlocks(t *testing.T) {
	long := strings.Repeat("handmade syslog line ", 40)[:600]
	es := []entry{{
		"__REALTIME_TIMESTAMP": []byte("1790000000000000"), "__MONOTONIC_TIMESTAMP": []byte("5000000000"),
		"__SEQNUM": []byte("1"), "__SEQNUM_ID": []byte(id1), "_BOOT_ID": []byte(bootA),
		"_TRANSPORT": []byte("syslog"), "MESSAGE": []byte(long), "PRIORITY": []byte("6"),
	}}
	var buf bytes.Buffer
	opts := Options{FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), SeqnumID: mustID(t, id1), MachineID: mustID(t, "0badc0de0badc0de0badc0de0badc0de")}
	if err := Write(&buf, es, opts); err != nil {
		t.Fatalf("Write() error = %v, want nil", err)
	}
	flags, payloads := dataObjects(t, buf.Bytes())
	compressed := 0
	for i, f := range flags {
		if f == 0 {
			continue
		}
		compressed++
		if f != 4 {
			t.Errorf("data object %d has flags %d, want 4 (ZSTD)", i, f)
		}
		if !bytes.HasPrefix(payloads[i], []byte{0x28, 0xb5, 0x2f, 0xfd}) || !bytes.Contains(payloads[i], []byte(long)) {
			t.Errorf("data object %d is not a zstd frame that holds the field as a raw block", i)
		}
	}
	if compressed != 1 {
		t.Errorf("%d compressed data objects, want 1 (only the 600-byte MESSAGE)", compressed)
	}
	h := buf.Bytes()
	if string(h[:8]) != "LPKSHHRH" || binary.LittleEndian.Uint64(h[88:]) != 256 || binary.LittleEndian.Uint32(h[12:]) != 4|8 {
		t.Errorf("header = signature %q size %d incompatible flags %d, want LPKSHHRH, 256 and KEYED-HASH|COMPRESSED-ZSTD (12)", h[:8], binary.LittleEndian.Uint64(h[88:]), binary.LittleEndian.Uint32(h[12:]))
	}
}

func TestWrite_ReaderSeesEveryKernelEntry(t *testing.T) {
	for _, f := range fixtureFiles(t, readFixtureExport(t)) {
		var buf bytes.Buffer
		if err := Write(&buf, f.entries, f.opts); err != nil {
			t.Fatalf("Write(%s) error = %v, want nil", f.name, err)
		}
		jf, err := journal.Open(bytes.NewReader(buf.Bytes()), int64(buf.Len()), strings.HasSuffix(f.name, "~"))
		if err != nil {
			t.Fatalf("%s: journal.Open() error = %v, want nil", f.name, err)
		}
		compareKernelEntries(t, f, jf)
	}
}

func compareKernelEntries(t *testing.T, f fixtureFile, jf *journal.File) {
	t.Helper()
	c := seekKernel(t, f.name, jf)
	var e journal.Entry
	n := 0
	for _, w := range f.entries {
		if string(w["_TRANSPORT"]) != "kernel" {
			continue
		}
		ok, err := jf.Next(&c, []string{"MESSAGE", "SYSLOG_FACILITY"}, &e)
		if err != nil || !ok {
			t.Fatalf("%s: Next() %d = %v, %v, want true, nil", f.name, n, ok, err)
		}
		checkEntry(t, f.name, n, &e, w)
		n++
	}
	if ok, err := jf.Next(&c, nil, &e); err != nil || ok {
		t.Errorf("%s: Next() after the last kernel entry = %v, %v, want false, nil", f.name, ok, err)
	}
}

func seekKernel(t *testing.T, name string, jf *journal.File) journal.Cursor {
	t.Helper()
	d, found, err := jf.Lookup([]byte("_TRANSPORT=kernel"))
	if err != nil || !found {
		t.Fatalf("%s: Lookup(_TRANSPORT=kernel) = found %v, err %v, want found true", name, found, err)
	}
	c, err := jf.Seek(d, 0)
	if err != nil {
		t.Fatalf("%s: Seek() error = %v, want nil", name, err)
	}
	return c
}

func checkEntry(t *testing.T, file string, i int, e *journal.Entry, w entry) {
	t.Helper()
	checkTimes(t, file, i, e, w)
	msg, _ := e.Field("MESSAGE")
	if len(w["MESSAGE"]) >= 600 {
		compressed := false
		for _, f := range e.Fields {
			compressed = compressed || f.Compressed
		}
		if !compressed || len(msg) != 0 {
			t.Errorf("%s: entry %d with a 600-byte MESSAGE: compressed item reported %v, plain MESSAGE fields %d, want true and 0", file, i, compressed, len(msg))
		}
		return
	}
	if len(msg) != 1 || !bytes.Equal(msg[0].Value, w["MESSAGE"]) {
		t.Errorf("%s: entry %d MESSAGE differs from the export", file, i)
	}
	fac, _ := e.Field("SYSLOG_FACILITY")
	wantFac := 1
	if _, ok := w["SYSLOG_FACILITY#2"]; ok {
		wantFac = 2
	}
	if len(fac) != wantFac {
		t.Errorf("%s: entry %d has %d SYSLOG_FACILITY items, want %d", file, i, len(fac), wantFac)
	}
}

func checkTimes(t *testing.T, file string, i int, e *journal.Entry, w entry) {
	t.Helper()
	seq, _ := strconv.ParseUint(string(w["__SEQNUM"]), 10, 64)
	rt, _ := strconv.ParseInt(string(w["__REALTIME_TIMESTAMP"]), 10, 64)
	mono, _ := strconv.ParseInt(string(w["__MONOTONIC_TIMESTAMP"]), 10, 64)
	if e.Seqnum != seq || !e.Realtime.Equal(time.UnixMicro(rt).UTC()) || e.Monotonic != time.Duration(mono)*time.Microsecond {
		t.Errorf("%s: entry %d = seqnum %d at %v, monotonic %v, want seqnum %d at %v, monotonic %dµs", file, i, e.Seqnum, e.Realtime, e.Monotonic, seq, time.UnixMicro(rt).UTC(), mono)
	}
	if got := hex.EncodeToString(e.BootID[:]); got != string(w["_BOOT_ID"]) {
		t.Errorf("%s: entry %d boot ID = %s, want %s", file, i, got, w["_BOOT_ID"])
	}
}

// TestFixture_UpdateGolden writes the fixture journal files; run with VANDOX_UPDATE_GOLDEN=1.
func TestFixture_UpdateGolden(t *testing.T) {
	if os.Getenv("VANDOX_UPDATE_GOLDEN") != "1" {
		t.Skip("VANDOX_UPDATE_GOLDEN is not 1")
	}
	files := fixtureFiles(t, readFixtureExport(t))
	writeFixtureFiles(t, fixtureDir(), files)
}

func TestFixture_CommittedFilesMatchTheWriter(t *testing.T) {
	files := fixtureFiles(t, readFixtureExport(t))
	dir := t.TempDir()
	writeFixtureFiles(t, dir, files)
	for _, f := range files {
		want, err := os.ReadFile(filepath.Join(dir, f.name))
		if err != nil {
			t.Fatal(err)
		}
		got, err := os.ReadFile(filepath.Join(fixtureDir(), f.name))
		if err != nil {
			t.Errorf("%s is not committed: %v (regenerate with VANDOX_UPDATE_GOLDEN=1)", f.name, err)
			continue
		}
		if !bytes.Equal(got, want) {
			t.Errorf("%s differs from the writer's output (%d bytes committed, %d generated); regenerate with VANDOX_UPDATE_GOLDEN=1", f.name, len(got), len(want))
		}
	}
	if left, _ := filepath.Glob(filepath.Join(fixtureDir(), "system*")); len(left) != len(files) {
		t.Errorf("testdata/journal holds %d journal files, want exactly the %d the writer makes", len(left), len(files))
	}
}

// serverFrameForms returns the frame forms testdata/journal/zstd-frames.txt lists: descriptor, single segment, checksum,
// dictionary flag, bytes of the content size, block types, number of blocks and bytes after the last block, in the text of frameForm.
func serverFrameForms(t *testing.T) map[string]bool {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(fixtureDir(), "zstd-frames.txt"))
	if err != nil {
		t.Fatalf("reading zstd-frames.txt: %v", err)
	}
	forms := map[string]bool{}
	for _, line := range strings.Split(string(data), "\n") {
		fields := strings.Fields(line)
		if len(fields) != 10 || strings.HasPrefix(line, "#") {
			continue
		}
		forms[strings.Join(fields[1:9], " ")] = true
	}
	if len(forms) == 0 {
		t.Fatal("zstd-frames.txt lists no frame form")
	}
	return forms
}

// frameForm describes the header and the blocks of the zstd frame in the columns of zstd-frames.txt.
func frameForm(t *testing.T, frame []byte) string {
	t.Helper()
	if len(frame) < 6 || !bytes.Equal(frame[:4], []byte{0x28, 0xb5, 0x2f, 0xfd}) {
		t.Fatalf("stored payload %.12x is not a zstd frame", frame)
	}
	desc := frame[4]
	single := int(desc >> 5 & 1)
	checksum := int(desc >> 2 & 1)
	dictFlag := 0
	if desc&3 != 0 {
		dictFlag = 1
	}
	sizeBytes := []int{1, 2, 4, 8}[desc>>6]
	if single == 0 && desc>>6 == 0 {
		sizeBytes = 0
	}
	pos := 5
	if single == 0 {
		pos++
	}
	pos += []int{0, 1, 2, 4}[desc&3] + sizeBytes
	var types []string
	last := false
	for !last {
		if pos+3 > len(frame) {
			t.Fatalf("frame ends inside a block header at %d of %d bytes", pos, len(frame))
		}
		h := uint32(frame[pos]) | uint32(frame[pos+1])<<8 | uint32(frame[pos+2])<<16
		last = h&1 == 1
		kind := []string{"raw", "rle", "compressed", "reserved"}[h>>1&3]
		types = append(types, kind)
		size := int(h >> 3)
		if kind == "rle" {
			size = 1
		}
		pos += 3 + size
	}
	if pos > len(frame) {
		t.Fatalf("frame ends inside a block: blocks need %d of %d bytes", pos, len(frame))
	}
	return fmt.Sprintf("0x%02x %d %d %d %d %s %d %d", desc, single, checksum, dictFlag, sizeBytes, strings.Join(types, ","), len(types), len(frame)-pos)
}

func TestWrite_CompressedFramesMatchTheServerForm(t *testing.T) {
	f, err := os.Open(filepath.Join(fixtureDir(), "live.export"))
	if err != nil {
		t.Fatalf("opening live.export: %v", err)
	}
	defer func() { _ = f.Close() }()
	entries, err := ReadExport(f)
	if err != nil {
		t.Fatalf("ReadExport(live.export) error = %v, want nil", err)
	}
	enc, err := zstd.NewWriter(nil, zstd.WithEncoderConcurrency(1), zstd.WithEncoderCRC(false), zstd.WithSingleSegment(true))
	if err != nil {
		t.Fatalf("zstd.NewWriter() error = %v, want nil", err)
	}
	dec, err := zstd.NewReader(nil)
	if err != nil {
		t.Fatalf("zstd.NewReader() error = %v, want nil", err)
	}
	var buf bytes.Buffer
	opts := Options{
		FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), SeqnumID: mustID(t, "0000000000000000000000000000000f"),
		MachineID: mustID(t, "0badc0de0badc0de0badc0de0badc0de"), Online: true,
		Compress: func(payload []byte) ([]byte, error) { return enc.EncodeAll(payload, nil), nil },
	}

	if err := Write(&buf, entries, opts); err != nil {
		t.Fatalf("Write() with Compress error = %v, want nil", err)
	}

	compressed, longest := requireServerForms(t, buf.Bytes(), dec)
	if compressed == 0 {
		t.Error("Write() with Compress stored no compressed data object, want every payload of 512 bytes and more")
	}
	if longest < 20_000 {
		t.Errorf("the longest decoded payload has %d bytes, want the 20,000 byte message of live.export", longest)
	}
}

// requireServerForms checks that every compressed data object of the journal file is a frame of one of the server's forms that decodes to a
// NAME=value payload, and returns the number of compressed objects and the length of the longest decoded payload.
func requireServerForms(t *testing.T, file []byte, dec *zstd.Decoder) (compressed, longest int) {
	t.Helper()
	flags, payloads := dataObjects(t, file)
	forms := serverFrameForms(t)
	for i, flag := range flags {
		if flag == 0 {
			continue
		}
		compressed++
		if flag != 4 {
			t.Errorf("data object %d has flags %d, want 4 (ZSTD)", i, flag)
		}
		if form := frameForm(t, payloads[i]); !forms[form] {
			t.Errorf("data object %d is a frame of the form %q, want one of the forms of zstd-frames.txt %v", i, form, forms)
		}
		plain, err := dec.DecodeAll(payloads[i], nil)
		if err != nil || !bytes.Contains(plain, []byte("=")) {
			t.Errorf("data object %d does not decode to a NAME=value payload: %d bytes, error %v", i, len(plain), err)
		}
		longest = max(longest, len(plain))
	}
	return compressed, longest
}

func TestWrite_CompressIsCalledForPayloadsOf512BytesAndMoreOnly(t *testing.T) {
	short := strings.Repeat("s", 400)
	long := strings.Repeat("l", 600)
	es := []entry{{
		"__REALTIME_TIMESTAMP": []byte("1790000000000000"), "__MONOTONIC_TIMESTAMP": []byte("5000000000"),
		"__SEQNUM": []byte("1"), "__SEQNUM_ID": []byte(id1), "_BOOT_ID": []byte(bootA),
		"MESSAGE": []byte(long), "SHORT": []byte(short),
	}}
	var seen []int
	opts := Options{
		FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), SeqnumID: mustID(t, id1), MachineID: mustID(t, "0badc0de0badc0de0badc0de0badc0de"),
		Compress: func(payload []byte) ([]byte, error) {
			seen = append(seen, len(payload))
			return append([]byte("COMPRESSED:"), payload...), nil
		},
	}
	var buf bytes.Buffer

	if err := Write(&buf, es, opts); err != nil {
		t.Fatalf("Write() error = %v, want nil", err)
	}

	if len(seen) != 1 || seen[0] != len("MESSAGE=")+len(long) {
		t.Errorf("Compress was called for payloads of %v bytes, want only the %d byte MESSAGE payload", seen, len("MESSAGE=")+len(long))
	}
	flags, payloads := dataObjects(t, buf.Bytes())
	stored := 0
	for i, flag := range flags {
		if flag != 0 && bytes.HasPrefix(payloads[i], []byte("COMPRESSED:")) {
			stored++
		}
	}
	if stored != 1 {
		t.Errorf("%d data objects hold the bytes Compress returned, want 1", stored)
	}
}

func TestWrite_CompressFailureFailsTheWrite(t *testing.T) {
	es := []entry{{
		"__REALTIME_TIMESTAMP": []byte("1790000000000000"), "__MONOTONIC_TIMESTAMP": []byte("5000000000"),
		"__SEQNUM": []byte("1"), "__SEQNUM_ID": []byte(id1), "_BOOT_ID": []byte(bootA),
		"MESSAGE": []byte(strings.Repeat("l", 600)),
	}}
	boom := errors.New("compress: boom")
	opts := Options{
		FileID: mustID(t, "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"), SeqnumID: mustID(t, id1), MachineID: mustID(t, "0badc0de0badc0de0badc0de0badc0de"),
		Compress: func([]byte) ([]byte, error) { return nil, boom },
	}
	var buf bytes.Buffer

	err := Write(&buf, es, opts)

	if !errors.Is(err, boom) {
		t.Errorf("Write() error = %v, want it to wrap the error of Compress", err)
	}
}
