package spool

import (
	"encoding/binary"
	"fmt"
	"hash/crc32"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The tests forge spool files, so they pin the layout of data file format version 1 (all integers little-endian):
//
//	header:  "VXSPOOL1" | version u16 | agent ID length u8 | agent ID | boot ID (36 bytes) | clock offset flag u8 |
//	         clock offset i64 | CRC-32C u32 of every header byte before it
//	frame:   payload length u32 | CRC-32C u32 of the payload | payload = sequence number u64 | wire line
//	trailer: 0xFFFFFFFF u32 | last sequence number u64 | record count u64 | earliest capture time i64 (unix ns) |
//	         latest capture time i64 (unix ns) | CRC-32C u32 of the 32 bytes after the marker
//
// A sealed file ends with its trailer. The meta file is the JSON object written by metaJSON.
const (
	segMagic       = "VXSPOOL1"
	trailerMarker  = uint32(0xFFFFFFFF)
	recordSpacing  = 10 * time.Second
	forgedFirst    = uint64(1_000_000_000_000)
	forgedFileSize = 10
)

var crcTable = crc32.MakeTable(crc32.Castagnoli)

// frameSpec describes one frame of a forged file.
type frameSpec struct {
	// rec is the record; its Seq is the number in the wire line.
	rec model.Record
	// frameSeq is the number in the frame; zero means rec.Seq.
	frameSeq uint64
	// line replaces the wire line when it is not nil.
	line []byte
	// badCRC writes a wrong checksum.
	badCRC bool
}

// trailerSpec describes the trailer of a forged file.
type trailerSpec struct {
	last, count      uint64
	earliest, latest int64
	badCRC           bool
}

// segSpec describes a forged data file.
type segSpec struct {
	agent, boot string
	offset      *int64
	version     uint16
	frames      []frameSpec
	trailer     *trailerSpec
}

// segBytes is an encoded forged file with the offsets of its parts.
type segBytes struct {
	data         []byte
	frameStart   []int
	trailerStart int
	headerLen    int
}

func u32(t testing.TB, n int) uint32 {
	t.Helper()

	if n < 0 || n > math.MaxUint32 {
		t.Fatalf("u32(%d): out of range in a test", n)

		return 0
	}

	return uint32(n)
}

func encodeSegment(t testing.TB, s segSpec) segBytes {
	t.Helper()

	if s.version == 0 {
		s.version = 1
	}
	var out segBytes
	h := []byte(segMagic)
	h = binary.LittleEndian.AppendUint16(h, s.version)
	h = append(h, toByte(len(s.agent)))
	h = append(h, s.agent...)
	h = append(h, s.boot...)
	if s.offset == nil {
		h = append(h, 0)
		h = binary.LittleEndian.AppendUint64(h, 0)
	} else {
		h = append(h, 1)
		h = binary.LittleEndian.AppendUint64(h, u64(t, *s.offset))
	}
	h = binary.LittleEndian.AppendUint32(h, crc32.Checksum(h, crcTable))
	out.headerLen = len(h)
	out.data = h
	for _, f := range s.frames {
		out.frameStart = append(out.frameStart, len(out.data))
		out.data = append(out.data, encodeFrame(t, f)...)
	}
	out.trailerStart = len(out.data)
	if s.trailer != nil {
		out.data = append(out.data, encodeTrailer(t, s.trailer)...)
	}

	return out
}

func encodeFrame(t testing.TB, f frameSpec) []byte {
	t.Helper()

	line := f.line
	if line == nil {
		var err error
		if line, err = wire.MarshalRecord(&f.rec); err != nil {
			t.Fatalf("forging a frame: MarshalRecord(seq %d) = %v", f.rec.Seq, err)
		}
	}
	seq := f.frameSeq
	if seq == 0 {
		seq = f.rec.Seq
	}
	payload := binary.LittleEndian.AppendUint64(nil, seq)
	payload = append(payload, line...)
	sum := crc32.Checksum(payload, crcTable)
	if f.badCRC {
		sum ^= 0xDEADBEEF
	}
	out := binary.LittleEndian.AppendUint32(nil, u32(t, len(payload)))
	out = binary.LittleEndian.AppendUint32(out, sum)

	return append(out, payload...)
}

func encodeTrailer(t testing.TB, tr *trailerSpec) []byte {
	t.Helper()

	body := binary.LittleEndian.AppendUint64(nil, tr.last)
	body = binary.LittleEndian.AppendUint64(body, tr.count)
	body = binary.LittleEndian.AppendUint64(body, u64(t, tr.earliest))
	body = binary.LittleEndian.AppendUint64(body, u64(t, tr.latest))
	sum := crc32.Checksum(body, crcTable)
	if tr.badCRC {
		sum ^= 0xDEADBEEF
	}
	out := binary.LittleEndian.AppendUint32(nil, trailerMarker)
	out = append(out, body...)

	return binary.LittleEndian.AppendUint32(out, sum)
}

// fileSpec describes a forged data file of consecutive records.
type fileSpec struct {
	// first is the number of the first record and the file's name number.
	first uint64
	// n is the number of records.
	n int
	// sealed adds a trailer that agrees with the records.
	sealed bool
	// boot is the boot ID of the capture context.
	boot string
	// agent is the agent ID; empty means testAgent.
	agent string
	// offset is the clock offset estimate; nil means unknown.
	offset *int64
	// capture is the capture time of the first record; each next record is recordSpacing later.
	capture time.Time
	// tag is part of every message.
	tag string
}

func (f fileSpec) agentID() string {
	if f.agent == "" {
		return testAgent
	}

	return f.agent
}

// last returns the number of the last record.
func (f fileSpec) last() uint64 { return f.first + ofLenInt(f.n) - 1 }

func ofLenInt(n int) uint64 {
	if n < 0 {
		return 0
	}

	return uint64(n)
}

// at returns the capture time of record i.
func (f fileSpec) at(i int) time.Time { return f.capture.Add(time.Duration(i) * recordSpacing) }

// records returns the numbered records of the file.
func (f fileSpec) records() []model.Record {
	out := make([]model.Record, 0, f.n)
	for i := range f.n {
		r := logRec(f.at(i), fmt.Sprintf("%s-%d", f.tag, i), 60)
		r.Seq = f.first + ofLenInt(i)
		out = append(out, r)
	}

	return out
}

// spec returns the file's description; a test changes it before it encodes the file.
func (f fileSpec) spec() segSpec {
	s := segSpec{agent: f.agentID(), boot: f.boot, offset: f.offset}
	for _, r := range f.records() {
		s.frames = append(s.frames, frameSpec{rec: r})
	}
	if f.sealed {
		s.trailer = &trailerSpec{last: f.last(), count: ofLenInt(f.n), earliest: f.at(0).UnixNano(), latest: f.at(f.n - 1).UnixNano()}
	}

	return s
}

// bytes returns the encoded file.
func (f fileSpec) bytes(t testing.TB) []byte { return encodeSegment(t, f.spec()).data }

// name returns the file name.
func (f fileSpec) name() string { return segName(f.first) }

// forgedMtime is the modification time the tests give a forged file.
var forgedMtime = epoch.Add(-5 * time.Hour)

// writeSegment writes data as the file name in dir with mode 0600 and the fixed modification time.
func writeSegment(t testing.TB, dir, name string, data []byte) {
	t.Helper()

	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatalf("WriteFile(%s) = %v", name, err)
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatalf("Chmod(%s) = %v", name, err)
	}
	if err := os.Chtimes(path, forgedMtime, forgedMtime); err != nil {
		t.Fatalf("Chtimes(%s) = %v", name, err)
	}
}

// writeFiles forges the files in dir.
func writeFiles(t testing.TB, dir string, files ...fileSpec) {
	t.Helper()

	for _, f := range files {
		writeSegment(t, dir, f.name(), f.bytes(t))
	}
}

// metaSpec describes a forged meta file.
type metaSpec struct {
	version int
	agent   string
	boot    string
	clean   bool
	next    uint64
	acked   [][2]uint64
}

// metaJSON returns the meta file the tests forge: {"version":1,"agent_id":"..","boot_id":"..","clean":false,
// "next":N,"acked":[{"first":A,"last":B}]}. A zero version means 1.
func metaJSON(m metaSpec) []byte {
	v := m.version
	if v == 0 {
		v = 1
	}
	var ranges []string
	for _, r := range m.acked {
		ranges = append(ranges, fmt.Sprintf(`{"first":%d,"last":%d}`, r[0], r[1]))
	}

	return fmt.Appendf(nil, `{"version":%d,"agent_id":%q,"boot_id":%q,"clean":%t,"next":%d,"acked":[%s]}`,
		v, m.agent, m.boot, m.clean, m.next, strings.Join(ranges, ","))
}

// writeMeta writes the meta file m into dir.
func writeMeta(t testing.TB, dir string, m metaSpec) {
	t.Helper()

	writeSegment(t, dir, metaName, metaJSON(m))
}

// world is a forged directory of three files in one context: a sealed, b sealed and c open (unsealed) with the
// current boot, with numbers that follow each other and a meta file that fits them.
type world struct {
	dir     string
	a, b, c fileSpec
}

// newWorld forges the world in dir.
func newWorld(t testing.TB, dir string) world {
	t.Helper()

	w := world{dir: dir}
	w.a = fileSpec{first: forgedFirst, n: forgedFileSize, sealed: true, boot: otherBoot, capture: epoch, tag: "a"}
	w.b = fileSpec{first: w.a.last() + 1, n: forgedFileSize, sealed: true, boot: otherBoot, capture: epoch.Add(100 * time.Second), tag: "b"}
	w.c = fileSpec{first: w.b.last() + 1, n: forgedFileSize, boot: testBoot, capture: epoch.Add(200 * time.Second), tag: "c"}
	writeFiles(t, dir, w.a, w.b, w.c)
	writeMeta(t, dir, metaSpec{agent: testAgent, boot: testBoot, next: w.c.last() + 1})

	return w
}

// readNumbers returns the numbers of the data records the spool hands out from the oldest.
func readNumbers(t *testing.T, sp *Spool) []uint64 {
	t.Helper()

	return seqs(dataOf(readAll(t, sp)))
}

// rangeNumbers returns first, first+1, ..., last.
func rangeNumbers(first, last uint64) []uint64 {
	var out []uint64
	for n := first; n <= last; n++ {
		out = append(out, n)
	}

	return out
}

// concat joins number lists.
func concat(lists ...[]uint64) []uint64 {
	var out []uint64
	for _, l := range lists {
		out = append(out, l...)
	}

	return out
}
