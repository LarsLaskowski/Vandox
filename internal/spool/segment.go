package spool

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"errors"
	"hash/crc32"
	"io"
	"math"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// segmentExt is the file name extension of a segment file.
const segmentExt = ".spool"

// The data file format, version 1, every integer little-endian:
//
//	header:  "VXSPOOL1" | version u16 | agent ID length u8 | agent ID | boot ID (36 bytes) | clock offset flag u8 |
//	         clock offset i64 | CRC-32C u32 of every header byte before it
//	frame:   payload length u32 | CRC-32C u32 of the payload | payload = sequence number u64 | wire line
//	trailer: 0xFFFFFFFF u32 | last number u64 | record count u64 | earliest capture time i64 | latest capture time i64 |
//	         CRC-32C u32 of the 32 bytes after the marker
const (
	magicBytes    = "VXSPOOL1"
	formatVersion = 1
	maxAgentLen   = 64
	bootIDLen     = 36
	headerMax     = 128
	headerBase    = len(magicBytes) + 2 + 1
	frameHeadLen  = 8
	seqLen        = 8
	minPayload    = seqLen + 1
	trailerLen    = 40
	trailerMark   = uint32(0xFFFFFFFF)
	namePrefix    = "segment-"
	nameDigits    = 20
	// maxSeq is the first number that is never issued: the backend stores numbers below 2^63 - 1.
	maxSeq = uint64(math.MaxInt64)
	// scanBuffer is the size of the buffer frames are read through.
	scanBuffer = 64 << 10
	// maxPrealloc bounds the capacity reserved up front for a buffer; a buffer that needs more grows by appending.
	maxPrealloc = 64 << 20
)

var (
	castagnoli = crc32.MakeTable(crc32.Castagnoli)
	// maxPayload is the largest frame payload: a wire line of the largest size and the sequence number.
	maxPayload = int64(wire.DefaultLimits().MaxLineBytes) + seqLen
	minTime    = time.Unix(0, math.MinInt64).UTC()
	maxTime    = time.Unix(0, math.MaxInt64).UTC()
	// errMarker reports the trailer marker where a frame was expected.
	errMarker = errors.New("spool: trailer marker")
)

// inRange reports whether t can be held as nanoseconds since the epoch.
func inRange(t time.Time) bool {
	return !t.Before(minTime) && !t.After(maxTime)
}

// nanoTime converts nanoseconds since the epoch to a UTC time.
func nanoTime(ns int64) time.Time {
	return time.Unix(0, ns).UTC()
}

// identity is the capture context of a data file.
type identity struct {
	agent  string
	boot   string
	offset *int64
}

// equal reports whether two contexts are the same.
func (id *identity) equal(other *identity) bool {
	if id.agent != other.agent || id.boot != other.boot {
		return false
	}
	if id.offset == nil || other.offset == nil {
		return id.offset == other.offset
	}

	return *id.offset == *other.offset
}

// validBootID reports whether s has the shape of a lower-case UUID.
func validBootID(s string) bool {
	if len(s) != bootIDLen {
		return false
	}
	for i := range len(s) {
		c := s[i]
		switch i {
		case 8, 13, 18, 23:
			if c != '-' {
				return false
			}
		default:
			if (c < '0' || c > '9') && (c < 'a' || c > 'f') {
				return false
			}
		}
	}

	return true
}

// encodeHeader returns the header of a data file of this context.
func (id *identity) encodeHeader() []byte {
	h := make([]byte, 0, headerMax)
	h = append(h, magicBytes...)
	h = binary.LittleEndian.AppendUint16(h, formatVersion)
	h = append(h, agentLenByte(id.agent))
	h = append(h, id.agent...)
	h = append(h, id.boot...)
	var offset int64
	flag := byte(0)
	if id.offset != nil {
		flag, offset = 1, *id.offset
	}
	h = append(h, flag)
	h = appendInt64(h, offset)

	return binary.LittleEndian.AppendUint32(h, crc32.Checksum(h, castagnoli))
}

// agentLenByte returns the length of the agent ID as the header holds it; the ID was checked at Open.
func agentLenByte(agent string) byte {
	n := len(agent)
	if n < 1 || n > maxAgentLen {
		return 0
	}

	return byte(n)
}

// appendInt64 appends v little-endian.
func appendInt64(dst []byte, v int64) []byte {
	out, err := binary.Append(dst, binary.LittleEndian, v)
	if err != nil {
		return dst
	}

	return out
}

// readInt64 reads a little-endian int64 from the start of b.
func readInt64(b []byte) int64 {
	var v int64
	if _, err := binary.Decode(b, binary.LittleEndian, &v); err != nil {
		return 0
	}

	return v
}

// parseHeader decodes the header at the start of b, which holds at least the first headerMax bytes of the file or all of
// it, and returns the context and the header's length.
func parseHeader(b []byte) (identity, int, error) {
	if len(b) < len(magicBytes) {
		if bytes.HasPrefix([]byte(magicBytes), b) {
			return identity{}, 0, errDamageCutShort
		}

		return identity{}, 0, errDamageNotFormat
	}
	if string(b[:len(magicBytes)]) != magicBytes {
		return identity{}, 0, errDamageNotFormat
	}
	if len(b) < headerBase {
		return identity{}, 0, errDamageCutShort
	}
	if binary.LittleEndian.Uint16(b[len(magicBytes):]) != formatVersion {
		return identity{}, 0, errDamageVersion
	}
	agentLen := int(b[headerBase-1])
	if agentLen < 1 || agentLen > maxAgentLen {
		return identity{}, 0, errDamageInconsistent
	}
	total := headerBase + agentLen + bootIDLen + 1 + 8 + 4
	if len(b) < total {
		return identity{}, 0, errDamageCutShort
	}
	if crc32.Checksum(b[:total-4], castagnoli) != binary.LittleEndian.Uint32(b[total-4:total]) {
		return identity{}, 0, errDamageChecksum
	}

	return headerContext(b, agentLen, total)
}

// headerContext reads the context out of a header whose checksum matched.
func headerContext(b []byte, agentLen, total int) (identity, int, error) {
	at := headerBase
	id := identity{agent: string(b[at : at+agentLen])}
	at += agentLen
	id.boot = string(b[at : at+bootIDLen])
	at += bootIDLen
	flag, offset := b[at], readInt64(b[at+1:])
	if !validBootID(id.boot) || flag > 1 || (flag == 0 && offset != 0) {
		return identity{}, 0, errDamageInconsistent
	}
	if flag == 1 {
		id.offset = &offset
	}

	return id, total, nil
}

// trailer is the summary that seals a data file.
type trailer struct {
	last, count      uint64
	earliest, latest int64
}

// appendTrailer appends the trailer frame.
func appendTrailer(dst []byte, t trailer) []byte {
	start := len(dst)
	dst = binary.LittleEndian.AppendUint32(dst, trailerMark)
	dst = binary.LittleEndian.AppendUint64(dst, t.last)
	dst = binary.LittleEndian.AppendUint64(dst, t.count)
	dst = appendInt64(dst, t.earliest)
	dst = appendInt64(dst, t.latest)

	return binary.LittleEndian.AppendUint32(dst, crc32.Checksum(dst[start+4:], castagnoli))
}

// parseTrailer decodes b, which is trailerLen bytes starting with the marker.
func parseTrailer(b []byte) (trailer, error) {
	if len(b) != trailerLen || binary.LittleEndian.Uint32(b) != trailerMark {
		return trailer{}, errDamageNotFormat
	}
	if crc32.Checksum(b[4:trailerLen-4], castagnoli) != binary.LittleEndian.Uint32(b[trailerLen-4:]) {
		return trailer{}, errDamageChecksum
	}

	return trailer{
		last:     binary.LittleEndian.Uint64(b[4:]),
		count:    binary.LittleEndian.Uint64(b[12:]),
		earliest: readInt64(b[20:]),
		latest:   readInt64(b[28:]),
	}, nil
}

// sane reports whether the trailer agrees with a file whose first record has the number first.
func (t trailer) sane(first uint64) bool {
	return t.count >= 1 && t.last >= first && t.last < maxSeq && t.last-first+1 >= t.count && t.earliest <= t.latest
}

// appendFrame appends the frame of one record.
func appendFrame(dst []byte, seq uint64, line []byte) []byte {
	start := len(dst)
	dst = binary.LittleEndian.AppendUint32(dst, lengthU32(seqLen+len(line)))
	dst = binary.LittleEndian.AppendUint32(dst, 0)
	dst = binary.LittleEndian.AppendUint64(dst, seq)
	dst = append(dst, line...)
	binary.LittleEndian.PutUint32(dst[start+4:], crc32.Checksum(dst[start+frameHeadLen:], castagnoli))

	return dst
}

// lengthU32 converts a payload length, which the wire limit keeps far below 4 GiB.
func lengthU32(n int) uint32 {
	if n < 0 || n > math.MaxUint32 {
		return math.MaxUint32
	}

	return uint32(n)
}

// withSeq returns line with the number of its envelope replaced by seq.
func withSeq(line []byte, seq uint64) []byte {
	key := []byte(`"seq":`)
	i := bytes.Index(line, key)
	if i < 0 {
		return line
	}
	start := i + len(key)
	end := start
	for end < len(line) && line[end] >= '0' && line[end] <= '9' {
		end++
	}
	out := make([]byte, 0, min(len(line), maxPrealloc))
	out = append(out, line[:start]...)
	out = strconv.AppendUint(out, seq, 10)

	return append(out, line[end:]...)
}

// frame is one frame read from a data file; line is only valid until the next read.
type frame struct {
	seq  uint64
	line []byte
	off  int64
	next int64
}

// frameReader reads and checks the frames of a data file.
type frameReader struct {
	br  *bufio.Reader
	buf []byte
	off int64
	end int64
}

// reset starts reading the frames between off and end of f.
func (r *frameReader) reset(f *os.File, off, end int64) {
	if r.br == nil {
		r.br = bufio.NewReaderSize(nil, scanBuffer)
	}
	r.br.Reset(io.NewSectionReader(f, off, end-off))
	r.off, r.end = off, end
}

// release gives back a payload buffer that grew beyond the usual size.
func (r *frameReader) release() {
	if cap(r.buf) > scanBuffer {
		r.buf = nil
	}
}

// next returns the next frame, io.EOF where the frames end, errMarker at a trailer, or the damage found.
func (r *frameReader) next() (frame, error) {
	left := r.end - r.off
	if left == 0 {
		return frame{}, io.EOF
	}
	if left < frameHeadLen {
		return frame{}, errDamageCutShort
	}
	var head [frameHeadLen]byte
	if _, err := io.ReadFull(r.br, head[:]); err != nil {
		return frame{}, readFailure(err)
	}
	size := binary.LittleEndian.Uint32(head[:4])
	if size == trailerMark {
		return frame{off: r.off}, errMarker
	}
	n := int64(size)
	if n < minPayload || n > maxPayload {
		return frame{}, errDamageInconsistent
	}
	if n > left-frameHeadLen {
		return frame{}, errDamageCutShort
	}
	if int64(cap(r.buf)) < n {
		r.buf = make([]byte, n)
	}
	payload := r.buf[:n]
	if _, err := io.ReadFull(r.br, payload); err != nil {
		return frame{}, readFailure(err)
	}
	if crc32.Checksum(payload, castagnoli) != binary.LittleEndian.Uint32(head[4:]) {
		return frame{}, errDamageChecksum
	}
	fr := frame{seq: binary.LittleEndian.Uint64(payload), line: payload[seqLen:], off: r.off, next: r.off + frameHeadLen + n}
	r.off = fr.next

	return fr, nil
}

// readFailure maps the end of the data to a cut file and anything else to an I/O error.
func readFailure(err error) error {
	if errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) {
		return errDamageCutShort
	}

	return errors.New(readErrPrefix + errText(err))
}

// recordSpan returns the earliest and latest time a record covers, in nanoseconds: its capture time, or for a gap its
// span. The bool is false when a time lies outside the range the wire format stores.
func recordSpan(rec *model.Record) (lo, hi int64, ok bool) {
	if !inRange(rec.CapturedAt) {
		return 0, 0, false
	}
	if g, isGap := rec.Data.(*model.Gap); isGap {
		if !inRange(g.From) || !inRange(g.To) {
			return 0, 0, false
		}

		return g.From.UnixNano(), g.To.UnixNano(), true
	}
	ns := rec.CapturedAt.UnixNano()

	return ns, ns, true
}

// decodeFrame decodes the record of a frame and applies the consistency rules to it.
func decodeFrame(fr *frame) (model.Record, int64, int64, error) {
	rec, err := wire.UnmarshalRecord(fr.line)
	if err != nil || rec.Seq != fr.seq || fr.seq >= maxSeq {
		return model.Record{}, 0, 0, errDamageInconsistent
	}
	lo, hi, ok := recordSpan(&rec)
	if !ok {
		return model.Record{}, 0, 0, errDamageInconsistent
	}

	return rec, lo, hi, nil
}

// segFile is what the spool keeps in memory of one data file.
type segFile struct {
	num   uint64
	id    fileID
	ident *identity
	hdr   int64
	size  int64
	// first, last and count describe the records that can be read; they are zero for a file without any.
	first, last, count uint64
	earliest, latest   int64
	sealed             bool
	// sumLast and sumCount are the trailer's own figures of a sealed file.
	sumLast, sumCount uint64
	// bad marks damage found while reading.
	bad    bool
	warned bool
}

// name returns the file name.
func (f *segFile) name() string {
	return dataName(f.num)
}

// dataEnd returns the offset where the frames of the file end.
func (f *segFile) dataEnd() int64 {
	if f.sealed {
		return f.size - trailerLen
	}

	return f.size
}

// summary returns the trailer that seals the file as it is.
func (f *segFile) summary() trailer {
	return trailer{last: f.last, count: f.count, earliest: f.earliest, latest: f.latest}
}

// add folds a record that was written into the file's figures.
func (f *segFile) add(seq uint64, lo, hi int64) {
	if f.count == 0 {
		f.first, f.earliest, f.latest = seq, lo, hi
	}
	f.last = seq
	f.count++
	f.earliest = min(f.earliest, lo)
	f.latest = max(f.latest, hi)
}

// dataName returns the name of the data file whose first record has the number first.
func dataName(first uint64) string {
	digits := strconv.FormatUint(first, 10)

	return namePrefix + strings.Repeat("0", nameDigits-len(digits)) + digits + segmentExt
}

// dataNumber parses the name of a data file.
func dataNumber(name string) (uint64, bool) {
	rest, ok := strings.CutPrefix(name, namePrefix)
	if !ok {
		return 0, false
	}
	digits, ok := strings.CutSuffix(rest, segmentExt)
	if !ok || len(digits) != nameDigits {
		return 0, false
	}
	for i := range len(digits) {
		if digits[i] < '0' || digits[i] > '9' {
			return 0, false
		}
	}
	n, err := strconv.ParseUint(digits, 10, 64)
	if err != nil {
		return 0, false
	}

	return n, true
}

// scanResult is what a pass over the frames of a data file found.
type scanResult struct {
	first, last, count uint64
	earliest, latest   int64
	// goodEnd is the offset after the last good frame.
	goodEnd int64
	// tr is the trailer that seals the file, when it is intact and agrees with the frames.
	tr *trailer
	// tail is a trailer found at the end of the file whatever the frames say.
	tail *trailer
	// damage is the reason the pass stopped before the end of the file, nil at a clean end.
	damage error
	// torn reports an incomplete last frame.
	torn bool
	// badName reports that the first record's number is not the file's name number.
	badName bool
}

// take checks one frame against the file's name and the frames before it and adds it to the result.
func (res *scanResult) take(fr *frame, name uint64) error {
	_, lo, hi, err := decodeFrame(fr)
	if err != nil {
		return err
	}
	if res.count == 0 {
		if fr.seq != name {
			res.badName = true

			return errDamageInconsistent
		}
		res.first, res.earliest, res.latest = fr.seq, lo, hi
	} else if fr.seq <= res.last {
		return errDamageInconsistent
	}
	res.last = fr.seq
	res.count++
	res.earliest = min(res.earliest, lo)
	res.latest = max(res.latest, hi)
	res.goodEnd = fr.next

	return nil
}

// readTail returns the trailer at the end of a file of the given size, when there is an intact one.
func readTail(f *os.File, hdr, size int64) *trailer {
	if size-hdr < trailerLen {
		return nil
	}
	var b [trailerLen]byte
	if n, err := f.ReadAt(b[:], size-trailerLen); n != trailerLen && err != nil {
		return nil
	}
	t, err := parseTrailer(b[:])
	if err != nil {
		return nil
	}

	return &t
}

// finishTrailer judges the trailer found at off during a pass.
func (res *scanResult) finishTrailer(f *os.File, off, size int64) {
	res.goodEnd = off
	switch rest := size - off; {
	case rest < trailerLen:
		res.damage, res.torn = errDamageCutShort, true
	case rest > trailerLen:
		res.damage = errDamageInconsistent
	default:
		var b [trailerLen]byte
		if n, err := f.ReadAt(b[:], off); n != trailerLen && err != nil {
			res.damage = errDamageCutShort

			return
		}
		t, err := parseTrailer(b[:])
		switch {
		case err != nil:
			res.damage = err
		case res.count == 0 || t.last != res.last || t.count != res.count || t.earliest > t.latest:
			res.damage = errDamageInconsistent
		default:
			res.tr = &t
		}
	}
}

// scan reads every frame of the data file f, whose header is hdr bytes long, and checks them.
func (r *frameReader) scan(f *os.File, name uint64, hdr, size int64) (scanResult, error) {
	res := scanResult{goodEnd: hdr, tail: readTail(f, hdr, size)}
	r.reset(f, hdr, size)
	defer r.release()
	for {
		fr, err := r.next()
		switch {
		case errors.Is(err, io.EOF):
			return res, nil
		case errors.Is(err, errMarker):
			res.finishTrailer(f, fr.off, size)

			return res, nil
		case err != nil:
			if !isDamage(err) {
				return res, err
			}
			res.damage, res.torn = err, errors.Is(err, errDamageCutShort)

			return res, nil
		}
		if err := res.take(&fr, name); err != nil {
			res.damage = err

			return res, nil
		}
	}
}

// quick checks a sealed file without reading its frames: header, trailer and first record. The bool is false when the
// file has to be scanned instead.
func (r *frameReader) quick(f *os.File, name uint64, hdr, size int64, nextName uint64) (scanResult, bool, error) {
	res := scanResult{tail: readTail(f, hdr, size)}
	t := res.tail
	if t == nil || !t.sane(name) || (nextName != 0 && t.last >= nextName) || size-hdr < frameHeadLen+minPayload+trailerLen {
		return res, false, nil
	}
	r.reset(f, hdr, size-trailerLen)
	defer r.release()
	fr, err := r.next()
	if err != nil {
		if isDamage(err) || errors.Is(err, errMarker) || errors.Is(err, io.EOF) {
			return res, false, nil
		}

		return res, false, err
	}
	_, _, _, err = decodeFrame(&fr)
	if err != nil || fr.seq != name {
		return res, false, nil
	}
	res.first, res.last, res.count = name, t.last, t.count
	res.earliest, res.latest, res.goodEnd, res.tr = t.earliest, t.latest, size-trailerLen, t

	return res, true, nil
}
