package journal

import (
	"bytes"
	"errors"
	"maps"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/zstdfield"
)

var errDecode = errors.New("decoder: refused")

// rawFrameDecoder is a Decompressor for the frames the test builder writes: zstd frames of one raw block. It records its calls.
type rawFrameDecoder struct {
	calls  int
	limits []int
	fail   bool
}

func (d *rawFrameDecoder) Decompress(dst, src []byte, limit int) ([]byte, error) {
	d.calls++
	d.limits = append(d.limits, limit)
	if d.fail {
		return nil, errDecode
	}
	header := 0
	switch {
	case len(src) > 10 && bytes.Equal(src[:5], []byte{0x28, 0xb5, 0x2f, 0xfd, 0x20}):
		header = 9
	case len(src) > 10 && bytes.Equal(src[:5], []byte{0x28, 0xb5, 0x2f, 0xfd, 0x60}):
		header = 10
	default:
		return nil, errDecode
	}
	payload := src[header:]
	return append(dst[:0], payload[:min(len(payload), limit)]...), nil
}

func plain(s string) bItem { return bItem{payload: []byte(s)} }

func packed(s string) bItem { return bItem{payload: []byte(s), compressed: true} }

// seqEntry returns an entry with the sequence number seq, a real time of seq seconds after testBase and the given items.
func seqEntry(seq uint64, items ...bItem) bEntry {
	return bEntry{seqnum: seq, realtime: testBase + seq*1_000_000, mono: testMono + seq*1_000_000, boot: hexID(testBoot), items: items}
}

func longText(prefix string, n int) string {
	return prefix + strings.Repeat("abcdefghij", n/10+1)[:n]
}

// readOptions returns the options a collector would use.
func readOptions(d Decompressor) *ReadOptions {
	return &ReadOptions{MaxPayload: 16384, Decompress: d, MaxCompressed: 4, MaxCompressedBytes: 1 << 20}
}

// nextWith reads the next entry with opt and fails the test on an error or the end of the list.
func nextWith(t *testing.T, f *File, c *Cursor, want []string, e *Entry, opt *ReadOptions) {
	t.Helper()
	ok, err := f.NextWith(c, want, e, opt)
	if err != nil || !ok {
		t.Fatalf("NextWith() = %v, %v, want true, nil", ok, err)
	}
}

func value(t *testing.T, e *Entry, name string) Field {
	t.Helper()
	fields, ok := e.Field(name)
	if !ok || len(fields) != 1 {
		t.Fatalf("entry %d holds %d fields %s, want 1", e.Seqnum, len(fields), name)
	}
	return fields[0]
}

func TestEntries_SeekAllFindsTheFirstEntryAfter(t *testing.T) {
	var entries []bEntry
	for i := 1; i <= 300; i++ {
		entries = append(entries, seqEntry(uint64(i)*10, plain("MESSAGE=m"+strconv.Itoa(i)), plain("_TRANSPORT=t"+strconv.Itoa(i%3))))
	}
	b, _ := buildFile(entries, testOptions())
	f := openBytes(t, b, false)
	for _, after := range []uint64{0, 5, 9, 10, 11, 1505, 1500, 2990, 2999, 3000, 3001, 1 << 62} {
		var want []uint64
		for _, e := range entries {
			if e.seqnum > after {
				want = append(want, e.seqnum)
			}
		}

		got := seqsAfter(t, f, after)

		if !slices.Equal(got, want) {
			t.Errorf("SeekAll(%d) then NextWith until the end read %d entries %v, want %d entries %v", after, len(got), head(got), len(want), head(want))
		}
	}
}

// seqsAfter seeks the entry after the sequence number and returns the sequence numbers of the entries up to the end of the file.
func seqsAfter(t *testing.T, f *File, after uint64) []uint64 {
	t.Helper()
	c, err := f.SeekAll(after)
	if err != nil {
		t.Fatalf("SeekAll(%d) error = %v, want nil", after, err)
	}
	var got []uint64
	var e Entry
	for {
		ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, readOptions(nil))
		if err != nil {
			t.Fatalf("SeekAll(%d): NextWith() error = %v, want nil", after, err)
		}
		if !ok {
			return got
		}
		got = append(got, e.Seqnum)
	}
}

func TestEntries_SeekAllOfAnEmptyFileReadsNothing(t *testing.T) {
	b, _ := buildFile(nil, testOptions())
	f := openBytes(t, b, false)

	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry
	ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, readOptions(nil))

	if ok || err != nil {
		t.Errorf("NextWith() on an empty file = %v, %v, want false, nil", ok, err)
	}
}

func TestEntries_NextWithDecodesCompressedItems(t *testing.T) {
	message := longText("MESSAGE=", 700)
	cmdline := longText("_CMDLINE=", 650)
	entries := []bEntry{
		seqEntry(10, plain("_HOSTNAME=web-1"), packed(message), packed(cmdline), plain("PRIORITY=6")),
		seqEntry(11, plain("MESSAGE=short"), plain("PRIORITY=3")),
	}
	b, _ := buildFile(entries, testOptions())
	f := openBytes(t, b, false)
	d := &rawFrameDecoder{}
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME", "PRIORITY"}, &e, readOptions(d))

	requireDecodedEntry(t, &e, d, message)
	nextWith(t, f, &c, []string{"MESSAGE", "PRIORITY"}, &e, readOptions(d))
	if got := value(t, &e, "MESSAGE"); string(got.Value) != "short" || e.Undecoded != 0 {
		t.Errorf("next entry MESSAGE = %q undecoded %d, want short and 0 (Undecoded counts per entry)", got.Value, e.Undecoded)
	}
}

// requireDecodedEntry checks the entry 10 of TestEntries_NextWithDecodesCompressedItems after NextWith.
func requireDecodedEntry(t *testing.T, e *Entry, d *rawFrameDecoder, message string) {
	t.Helper()
	if got := value(t, e, "MESSAGE"); string(got.Value) != message[len("MESSAGE="):] || got.Cut || got.Compressed {
		t.Errorf("MESSAGE = %d bytes cut %v compressed %v, want the %d decoded bytes uncut", len(got.Value), got.Cut, got.Compressed, len(message)-len("MESSAGE="))
	}
	if got := value(t, e, "_HOSTNAME"); string(got.Value) != "web-1" {
		t.Errorf("_HOSTNAME = %q, want web-1", got.Value)
	}
	if _, ok := e.Field("_CMDLINE"); ok {
		t.Error("the entry holds _CMDLINE, want a decoded item whose name is not wanted left out")
	}
	for _, field := range e.Fields {
		if field.Compressed {
			t.Errorf("field %q is still marked compressed, want every item of a decoded entry resolved", field.Name)
		}
	}
	if e.Undecoded != 0 || e.Seqnum != 10 || e.Items != 4 {
		t.Errorf("entry = seqnum %d items %d undecoded %d, want seqnum 10, 4 items, 0 undecoded", e.Seqnum, e.Items, e.Undecoded)
	}
	if d.calls != 2 {
		t.Errorf("the decoder was called %d times, want 2 (the name of a compressed item is inside the frame, so both are decoded)", d.calls)
	}
	for _, limit := range d.limits {
		if limit != 16384+66 {
			t.Errorf("the decoder was given the limit %d, want MaxPayload + 66 = %d", limit, 16384+66)
		}
	}
}

// requireNoCompressedField checks that NextWith put no field marked compressed into the entry.
func requireNoCompressedField(t *testing.T, e *Entry) {
	t.Helper()
	for _, field := range e.Fields {
		if field.Compressed {
			t.Errorf("entry %d holds a field marked compressed, want NextWith to put none into Fields", e.Seqnum)
		}
	}
}

func TestEntries_NextWithCountsTheValueOnly(t *testing.T) {
	const limit = 100
	exact := strings.Repeat("e", limit)
	over := strings.Repeat("o", limit+1)
	tests := []struct {
		name    string
		item    bItem
		wantCut bool
		want    string
		decodes int
	}{
		{"a plain value of exactly MaxPayload", plain("MESSAGE=" + exact), false, exact, 0},
		{"a plain value of MaxPayload + 1", plain("MESSAGE=" + over), true, over[:limit], 0},
		{"a compressed value of exactly MaxPayload", packed("MESSAGE=" + exact), false, exact, 1},
		{"a compressed value of MaxPayload + 1", packed("MESSAGE=" + over), true, over[:limit], 1},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			b, _ := buildFile([]bEntry{seqEntry(1, tt.item)}, testOptions())
			f := openBytes(t, b, false)
			d := &rawFrameDecoder{}
			opt := readOptions(d)
			opt.MaxPayload = limit
			c, err := f.SeekAll(0)
			if err != nil {
				t.Fatalf("SeekAll(0) error = %v, want nil", err)
			}
			var e Entry

			nextWith(t, f, &c, []string{"MESSAGE"}, &e, opt)

			got := value(t, &e, "MESSAGE")
			if string(got.Value) != tt.want || got.Cut != tt.wantCut || got.Compressed {
				t.Errorf("MESSAGE = %d bytes cut %v compressed %v, want %d bytes cut %v", len(got.Value), got.Cut, got.Compressed, len(tt.want), tt.wantCut)
			}
			requireDecoderCalls(t, d, tt.decodes, limit+66)
			requireNoCompressedField(t, &e)
		})
	}
}

// requireDecoderCalls checks the number of calls of the decoder and that each was given the limit.
func requireDecoderCalls(t *testing.T, d *rawFrameDecoder, calls, limit int) {
	t.Helper()
	if d.calls != calls {
		t.Errorf("the decoder was called %d times, want %d", d.calls, calls)
	}
	for _, got := range d.limits {
		if got != limit {
			t.Errorf("the decoder was given the limit %d, want MaxPayload + 66 = %d", got, limit)
		}
	}
}

// damageByte sets the byte at off of the file bytes b.
func damageByte(b []byte, off uint64, value byte) { b[off] = value }

func TestEntries_NextWithDamagedEntryKeepsTheCursor(t *testing.T) {
	b, lay := buildFile([]bEntry{seqEntry(1, plain("MESSAGE=one")), seqEntry(2, plain("MESSAGE=two")), seqEntry(3, plain("MESSAGE=three"))}, testOptions())
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry
	nextWith(t, f, &c, []string{"MESSAGE"}, &e, readOptions(nil))
	damageByte(b, lay.entries[1], 0) // the type of the second entry object

	for i := range 2 {
		ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, readOptions(nil))
		if ok || !errors.Is(err, ErrMalformed) {
			t.Fatalf("NextWith() %d on the damaged entry = %v, %v, want false and ErrMalformed", i, ok, err)
		}
	}
	damageByte(b, lay.entries[1], 3) // the type of an entry object restored

	for _, want := range []string{"two", "three"} {
		nextWith(t, f, &c, []string{"MESSAGE"}, &e, readOptions(nil))
		if got := value(t, &e, "MESSAGE"); string(got.Value) != want {
			t.Errorf("MESSAGE after the repair = %q, want %q: the cursor stayed in front of the damaged entry", got.Value, want)
		}
	}
}

func TestEntries_NextWithDamagedItemObjectIsUndecoded(t *testing.T) {
	const flagsAndType, sizeField = 0, 8
	tests := []struct {
		name   string
		offset uint64
		value  byte
	}{
		{"a wrong type", flagsAndType, 0},
		{"a size that leaves the file", sizeField + 6, 0x7f},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			bad := "_HOSTNAME=bad-host"
			b, lay := buildFile([]bEntry{
				seqEntry(1, plain("MESSAGE=one"), plain(bad)),
				seqEntry(2, plain("MESSAGE=two"), plain("_HOSTNAME=good-host")),
			}, testOptions())
			b[lay.data[bad]+tt.offset] = tt.value
			f := openBytes(t, b, false)
			c, err := f.SeekAll(0)
			if err != nil {
				t.Fatalf("SeekAll(0) error = %v, want nil", err)
			}
			var e Entry

			nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, readOptions(nil))

			if e.Undecoded != 1 || e.Seqnum != 1 {
				t.Errorf("entry %d Undecoded = %d, want entry 1 with 1 undecoded item (the damaged object)", e.Seqnum, e.Undecoded)
			}
			if got := value(t, &e, "MESSAGE"); string(got.Value) != "one" {
				t.Errorf("MESSAGE = %q, want one: the other items of the entry are read", got.Value)
			}
			requireNoCompressedField(t, &e)
			nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, readOptions(nil))
			if got := value(t, &e, "_HOSTNAME"); string(got.Value) != "good-host" || e.Undecoded != 0 || e.Seqnum != 2 {
				t.Errorf("next entry %d _HOSTNAME = %q undecoded %d, want entry 2, good-host and 0", e.Seqnum, got.Value, e.Undecoded)
			}
		})
	}
}

func TestEntries_NextWithMarksCutFields(t *testing.T) {
	plainLong := longText("MESSAGE=", 3000)
	packedLong := longText("MESSAGE=", 1500)
	entries := []bEntry{
		seqEntry(1, plain(plainLong), plain("_HOSTNAME=web-1")),
		seqEntry(2, packed(packedLong), plain("_HOSTNAME=web-1")),
	}
	b, _ := buildFile(entries, testOptions())
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	opt := readOptions(&rawFrameDecoder{})
	opt.MaxPayload = 100
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry
	for i, full := range []string{plainLong, packedLong} {
		nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, opt)

		got := value(t, &e, "MESSAGE")
		whole := full[len("MESSAGE="):]
		if !got.Cut || len(got.Value) != 100 || !strings.HasPrefix(whole, string(got.Value)) {
			t.Errorf("entry %d MESSAGE = %d bytes cut %v, want the first 100 bytes of the %d byte value with Cut set", i, len(got.Value), got.Cut, len(whole))
		}
		if host := value(t, &e, "_HOSTNAME"); string(host.Value) != "web-1" || host.Cut {
			t.Errorf("entry %d _HOSTNAME = %q cut %v, want web-1 uncut", i, host.Value, host.Cut)
		}
		if i == 0 && r.maxRead > 1024 {
			t.Errorf("the reader asked for a read of %d bytes, want the excess of a 3,000 byte payload not read (at most 1,024)", r.maxRead)
		}
	}
}

func TestEntries_MoreThanFourCompressedItemsLeaveTheEntryUndecoded(t *testing.T) {
	five := seqEntry(1, packed(longText("F1=", 600)), packed(longText("F2=", 600)), packed(longText("F3=", 600)), packed(longText("F4=", 600)), packed(longText("F5=", 600)), plain("MESSAGE=m"))
	four := seqEntry(2, packed(longText("F1=", 601)), packed(longText("F2=", 601)), packed(longText("F3=", 601)), packed(longText("F4=", 601)), plain("MESSAGE=m"))
	b, _ := buildFile([]bEntry{five, four}, testOptions())
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry
	d := &rawFrameDecoder{}

	nextWith(t, f, &c, []string{"MESSAGE", "F1", "F2", "F3", "F4", "F5"}, &e, readOptions(d))

	if e.Undecoded < 1 {
		t.Errorf("entry with five compressed items: Undecoded = %d, want at least 1 (one more than MaxCompressed 4 leaves it undecoded)", e.Undecoded)
	}
	if d.calls > 4 {
		t.Errorf("the decoder was called %d times for five compressed items, want at most 4", d.calls)
	}

	d = &rawFrameDecoder{}
	nextWith(t, f, &c, []string{"MESSAGE", "F1", "F2", "F3", "F4", "F5"}, &e, readOptions(d))

	if e.Undecoded != 0 || d.calls != 4 {
		t.Errorf("entry with four compressed items: Undecoded = %d, decoder calls = %d, want 0 and 4", e.Undecoded, d.calls)
	}
	for _, name := range []string{"F1", "F2", "F3", "F4"} {
		if _, ok := e.Field(name); !ok {
			t.Errorf("entry with four compressed items lacks the decoded field %s", name)
		}
	}
}

func TestEntries_CompressedObjectBeyondTheBoundIsNotRead(t *testing.T) {
	huge := longText("MESSAGE=", 20_000)
	entries := []bEntry{
		seqEntry(1, packed(huge), plain("_HOSTNAME=web-1")),
		seqEntry(2, packed(longText("MESSAGE=", 900)), plain("_HOSTNAME=web-1")),
	}
	b, _ := buildFile(entries, testOptions())
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	d := &rawFrameDecoder{}
	opt := readOptions(d)
	opt.MaxCompressedBytes = 4096
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, opt)

	if e.Undecoded != 1 || d.calls != 0 {
		t.Errorf("entry with a 20,000 byte object and MaxCompressedBytes 4096: Undecoded = %d, decoder calls = %d, want 1 and 0", e.Undecoded, d.calls)
	}
	if r.maxRead > 4096 {
		t.Errorf("the reader asked for a read of %d bytes, want the object over the bound not read (at most 4,096)", r.maxRead)
	}
	if host := value(t, &e, "_HOSTNAME"); string(host.Value) != "web-1" {
		t.Errorf("_HOSTNAME = %q, want the other fields of the entry read", host.Value)
	}

	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, opt)

	if e.Undecoded != 0 || d.calls != 1 {
		t.Errorf("next entry with a 900 byte object: Undecoded = %d, decoder calls = %d, want 0 and 1 (the bound is per object)", e.Undecoded, d.calls)
	}
}

func TestEntries_ItemsThatCannotBeDecodedCountAsUndecoded(t *testing.T) {
	const flagXZ, flagLZ4 = 1, 2
	tests := []struct {
		name    string
		patch   byte
		decoder func() Decompressor
	}{
		{"an XZ item", flagXZ, func() Decompressor { return &rawFrameDecoder{} }},
		{"an LZ4 item", flagLZ4, func() Decompressor { return &rawFrameDecoder{} }},
		{"no decompressor", 0, func() Decompressor { return nil }},
		{"a failed decoding", 0, func() Decompressor { return &rawFrameDecoder{fail: true} }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) { requireUndecoded(t, tt.patch, tt.decoder()) })
	}
}

// requireUndecoded reads an entry whose message is stored compressed (with the object flags changed to patch unless it is 0) and the
// entry after it, with a decoder that cannot decode the message.
func requireUndecoded(t *testing.T, patch byte, d Decompressor) {
	t.Helper()
	message := longText("MESSAGE=", 700)
	b, lay := buildFile([]bEntry{seqEntry(1, packed(message), plain("_HOSTNAME=web-1")), seqEntry(2, plain("MESSAGE=after"))}, testOptions())
	if patch != 0 {
		b[lay.data[message]+1] = patch
	}
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, readOptions(d))

	if e.Undecoded != 1 {
		t.Errorf("Undecoded = %d, want 1", e.Undecoded)
	}
	if _, ok := e.Field("MESSAGE"); ok {
		t.Error("the entry holds a MESSAGE, want the undecodable item left out")
	}
	requireNoCompressedField(t, &e)
	if host := value(t, &e, "_HOSTNAME"); string(host.Value) != "web-1" {
		t.Errorf("_HOSTNAME = %q, want the other fields read", host.Value)
	}
	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, readOptions(d))
	if got := value(t, &e, "MESSAGE"); string(got.Value) != "after" || e.Undecoded != 0 {
		t.Errorf("next entry MESSAGE = %q undecoded %d, want after and 0", got.Value, e.Undecoded)
	}
}

// hostEntries returns an entry whose items cover the paths of NextWith: a plain item longer than the scratch buffer, a compressed one
// and a short one.
func hostEntries() []bEntry {
	return []bEntry{
		seqEntry(1, plain(longText("MESSAGE=", 3000)), packed(longText("F1=", 700)), plain("_HOSTNAME=web-1")),
		seqEntry(2, plain("MESSAGE=after")),
	}
}

func TestEntries_NextWithReaderFailureIsErrReadAndKeepsTheCursor(t *testing.T) {
	b, _ := buildFile(hostEntries(), testOptions())
	failures := 0
	for ok := 0; ok < 40; ok++ {
		if readWithFailureAfter(t, b, ok) {
			failures++
		}
	}
	if failures < 5 {
		t.Errorf("only %d of 40 read counts made a read fail, want the failure met in every kind of read (the entry, the table, the items, the payload and the compressed object)", failures)
	}
}

// readWithFailureAfter reads the two entries of hostEntries through a reader that fails after okReads reads once the cursor is set, and
// reports whether a read failed. A failure must be ErrRead, never a damage, and leave the cursor in front of the entry.
func readWithFailureAfter(t *testing.T, b []byte, okReads int) bool {
	t.Helper()
	want := []string{"MESSAGE", "F1", "_HOSTNAME"}
	fr := &failingReader{r: newRefReader(b), okReads: 1 << 30}
	f, err := Open(fr, int64(len(b)), false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	fr.okReads = fr.calls + okReads
	var e Entry

	got, err := f.NextWith(&c, want, &e, readOptions(&rawFrameDecoder{}))

	if err != nil {
		if got || !errors.Is(err, ErrRead) || !errors.Is(err, errBoom) || err.Error() != "journal: read failed" {
			t.Fatalf("after %d good reads: NextWith() = %v, %v, want false and %q wrapping the reader's error", okReads, got, err, "journal: read failed")
		}
		fr.okReads = 1 << 30
		e = Entry{}
		nextWith(t, f, &c, want, &e, readOptions(&rawFrameDecoder{})) // the cursor stayed: the same entry is read again
	}
	fr.okReads = 1 << 30
	var second Entry
	nextWith(t, f, &c, want, &second, readOptions(&rawFrameDecoder{}))
	if e.Seqnum != 1 || e.Undecoded != 0 || len(e.Fields) != 3 || second.Seqnum != 2 {
		t.Errorf("after %d good reads: entries %d and %d with %d fields and %d undecoded, want entries 1 and 2 and 3 fields and none undecoded: a failing reader is no damage", okReads, e.Seqnum, second.Seqnum, len(e.Fields), e.Undecoded)
	}
	return err != nil
}

func TestEntries_NextWithCompressedObjectBeyondTheFileEnd(t *testing.T) {
	message := longText("MESSAGE=", 4000)
	for _, online := range []bool{false, true} {
		name := "an archived file ends its list"
		if online {
			name = "an online file is malformed until it is refreshed"
		}
		t.Run(name, func(t *testing.T) {
			o := testOptions()
			o.online = online
			b, lay := buildFile([]bEntry{seqEntry(1, plain("MESSAGE=one")), seqEntry(2, packed(message))}, o)
			r := newRefReader(b)
			r.limit = int(lay.data[message]) + 100 // #nosec G115 -- a small fixture offset: the object starts and does not end inside the file
			f, err := r.openRef(false)
			if err != nil {
				t.Fatalf("Open() error = %v, want nil", err)
			}
			c, err := f.SeekAll(0)
			if err != nil {
				t.Fatalf("SeekAll(0) error = %v, want nil", err)
			}
			var e Entry
			nextWith(t, f, &c, []string{"MESSAGE"}, &e, readOptions(&rawFrameDecoder{}))

			for i := range 2 { // the cursor stays: the second read meets the same cut
				ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, readOptions(&rawFrameDecoder{}))

				requireCutEnd(t, i, online, ok, err, &c)
			}
		})
	}
}

// requireCutEnd checks the result of a read that met the cut-off end of the file: an online file reports a malformed one (the file may
// have grown), any other ends its list with a torn cursor.
func requireCutEnd(t *testing.T, i int, online, ok bool, err error, c *Cursor) {
	t.Helper()
	if online {
		if ok || !errors.Is(err, ErrMalformed) {
			t.Fatalf("NextWith() %d = %v, %v, want false and %v", i, ok, err, ErrMalformed)
		}
		return
	}
	if ok || err != nil || !c.Torn() {
		t.Fatalf("NextWith() %d = %v, %v, torn %v, want false, nil and a torn end", i, ok, err, c.Torn())
	}
}

func TestEntries_NextWithKeepsAtMostSixteenFields(t *testing.T) {
	const keep = 16
	var plains, packs []bItem
	var want []string
	for i := range keep + 1 {
		name := "F" + strconv.Itoa(i)
		want = append(want, name)
		plains = append(plains, plain(name+"=v"))
		packs = append(packs, packed(longText(name+"=", 600)))
	}
	b, _ := buildFile([]bEntry{seqEntry(1, plains...), seqEntry(2, packs...)}, testOptions())
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	opt := readOptions(&rawFrameDecoder{})
	opt.MaxCompressed = keep + 1
	for _, kind := range []string{"plain", "compressed"} {
		var e Entry

		nextWith(t, f, &c, want, &e, opt)

		if len(e.Fields) != keep || omittedOf(t, &e) != 1 || e.Undecoded != 0 {
			t.Errorf("entry with %d %s items: %d fields, Omitted %d, Undecoded %d, want %d, 1 and 0", keep+1, kind, len(e.Fields), omittedOf(t, &e), e.Undecoded, keep)
		}
	}
}

func TestEntries_NextWithItemsWithoutAName(t *testing.T) {
	b, _ := buildFile([]bEntry{seqEntry(1, plain("NOVALUE"), plain("=nameless"), packed(strings.Repeat("a", 700)), packed("="+strings.Repeat("b", 700)), plain("MESSAGE=kept"))}, testOptions())
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE", "NOVALUE"}, &e, readOptions(&rawFrameDecoder{}))

	if got := value(t, &e, "MESSAGE"); string(got.Value) != "kept" || len(e.Fields) != 1 {
		t.Errorf("entry fields = %d, MESSAGE = %q, want only MESSAGE=kept: an item without a name is no field", len(e.Fields), got.Value)
	}
	if e.Undecoded != 2 {
		t.Errorf("Undecoded = %d, want 2 (the two compressed items whose decoded form has no name)", e.Undecoded)
	}
}

// ownBufferDecoder answers with a buffer of its own and not the one it was given.
type ownBufferDecoder struct{ out []byte }

func (d *ownBufferDecoder) Decompress(_, _ []byte, _ int) ([]byte, error) { return d.out, nil }

func TestEntries_NextWithCopiesTheValueOfADecoderThatKeepsItsBuffer(t *testing.T) {
	b, _ := buildFile([]bEntry{seqEntry(1, packed(longText("MESSAGE=", 700)))}, testOptions())
	f := openBytes(t, b, false)
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	d := &ownBufferDecoder{out: []byte("MESSAGE=decoded text")}
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE"}, &e, readOptions(d))
	copy(d.out, "MESSAGE=XXXXXXX text")

	if got := value(t, &e, "MESSAGE"); string(got.Value) != "decoded text" {
		t.Errorf("MESSAGE = %q, want %q: the value must not alias the decoder's buffer", got.Value, "decoded text")
	}
}

func TestEntries_NextWithGrowsTheInputBufferForALargeCompressedObject(t *testing.T) {
	big := longText("MESSAGE=", 60_000)
	small := longText("MESSAGE=", 900)
	b, _ := buildFile([]bEntry{seqEntry(1, packed(small)), seqEntry(2, packed(big)), seqEntry(3, packed(small+"x"))}, testOptions())
	r := newRefReader(b)
	f, err := r.openRef(false)
	if err != nil {
		t.Fatalf("Open() error = %v, want nil", err)
	}
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	opt := readOptions(&rawFrameDecoder{})
	var e Entry

	nextWith(t, f, &c, []string{"MESSAGE"}, &e, opt)
	start := len(e.zin)
	nextWith(t, f, &c, []string{"MESSAGE"}, &e, opt)
	grown := len(e.zin)
	cut := value(t, &e, "MESSAGE")
	nextWith(t, f, &c, []string{"MESSAGE"}, &e, opt)

	if start != minInputBuffer {
		t.Errorf("the input buffer holds %d bytes after a small object, want its start size %d", start, minInputBuffer)
	}
	if grown < len(big) || len(cut.Value) != 16384 || !cut.Cut {
		t.Errorf("after the 60,000 byte object the input buffer holds %d bytes and MESSAGE %d bytes (cut %v), want at least %d, 16384 and cut", grown, len(cut.Value), cut.Cut, len(big))
	}
	if got := value(t, &e, "MESSAGE"); string(got.Value) != small[len("MESSAGE="):]+"x" || len(e.zin) != grown {
		t.Errorf("third entry MESSAGE = %d bytes with an input buffer of %d, want the 893 byte value and the buffer kept at %d (grown once)", len(got.Value), len(e.zin), grown)
	}
	if r.violated != "" {
		t.Errorf("the reader was asked for %s, want reads of at most 64 KiB", r.violated)
	}
}

func TestEntries_NextWithKeepsTheFirstItemOfEachName(t *testing.T) {
	want := []string{"MESSAGE", "PRIORITY", "_HOSTNAME", "SYSLOG_IDENTIFIER", "_COMM", "_PID", "SYSLOG_PID"}
	repeated := func(name string, n int) []bItem {
		var items []bItem
		for i := range n {
			items = append(items, plain(name+"="+strconv.Itoa(i)+"-value"))
		}
		return items
	}
	message := longText("MESSAGE=", 600)
	tests := []struct {
		name  string
		items []bItem
		want  map[string]string
	}{
		{
			"twenty identifiers, two process IDs and a message",
			append(append(repeated("SYSLOG_IDENTIFIER", 20), plain("_PID=7"), plain("_PID=8")), plain("MESSAGE=kept")),
			map[string]string{"SYSLOG_IDENTIFIER": "0-value", "_PID": "7", "MESSAGE": "kept"},
		},
		{
			"twenty process IDs and a compressed message",
			append(repeated("_PID", 20), packed(message)),
			map[string]string{"_PID": "0-value", "MESSAGE": message[len("MESSAGE="):]},
		},
		{
			"a plain message and a compressed one",
			[]bItem{plain("MESSAGE=first"), packed(message)},
			map[string]string{"MESSAGE": "first"},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			b, _ := buildFile([]bEntry{seqEntry(1, tt.items...)}, testOptions())
			f := openBytes(t, b, false)
			c, err := f.SeekAll(0)
			if err != nil {
				t.Fatalf("SeekAll(0) error = %v, want nil", err)
			}
			var e Entry

			nextWith(t, f, &c, want, &e, readOptions(&rawFrameDecoder{}))

			requireFirstItems(t, &e, tt.want)
		})
	}
}

// requireFirstItems fails the test when the fields of e are not exactly want, each name once, or when an item was counted as omitted
// or undecoded.
func requireFirstItems(t *testing.T, e *Entry, want map[string]string) {
	t.Helper()
	got := map[string]string{}
	for _, fld := range e.Fields {
		if _, twice := got[fld.Name]; twice {
			t.Errorf("the entry holds the field %s twice, want the first item of each name only", fld.Name)
		}
		got[fld.Name] = string(fld.Value)
	}
	if !maps.Equal(got, want) {
		t.Errorf("fields = %v, want %v", got, want)
	}
	if omittedOf(t, e) != 0 || e.Undecoded != 0 {
		t.Errorf("Omitted = %d, Undecoded = %d, want 0 and 0: a repeated name is passed over, not counted", omittedOf(t, e), e.Undecoded)
	}
}

// realSampleMaxPayload is the value cap of the log collector (logship.maxMessageBytes, model.MaxTextBytes); the journal package cannot
// import logship, so the number is repeated here.
const realSampleMaxPayload = 16384

func TestEntries_NextWithReadsEveryEntryOfTheRealCompactSamples(t *testing.T) {
	dec, err := zstdfield.New()
	if err != nil {
		t.Fatalf("zstdfield.New() error = %v, want nil", err)
	}
	opt := &ReadOptions{MaxPayload: realSampleMaxPayload, Decompress: dec, MaxCompressed: 4, MaxCompressedBytes: zstdfield.MaxFrameBytes}
	for _, c := range []struct {
		journal, export string
		entries         uint64
	}{{"kernel.journal", "kernel.export", 100}, {"live.journal", "live.export", 40}} {
		t.Run(c.journal, func(t *testing.T) {
			requireRealSample(t, c.journal, c.export, c.entries, opt)
		})
	}
}

// requireRealSample reads the real sample journal in full with NextWith and compares it with its export.
func requireRealSample(t *testing.T, journal, export string, entries uint64, opt *ReadOptions) {
	t.Helper()
	byStamps := exportByStamps(t, export)
	f, raw, r := openSample(t, journal)
	if n := le.Uint64(raw[hdrNEntries:]); n != entries {
		t.Fatalf("the sample counts %d entries in its header, want %d", n, entries)
	}

	seqs, long, cut := readSampleWith(t, f, byStamps, opt)

	if uint64(len(seqs)) != entries { // #nosec G115 -- len is never negative
		t.Fatalf("read %d entries, want the %d the header counts", len(seqs), entries)
	}
	if journal == "live.journal" {
		if want := []uint64{16, 36}; !slices.Equal(long, want) {
			t.Errorf("entries with the 700-byte MESSAGE decoded from zstd = %v, want %v", long, want)
		}
		if want := []uint64{15, 35}; !slices.Equal(cut, want) {
			t.Errorf("entries with a cut MESSAGE (the 20,000-byte one decoded from zstd) = %v, want %v", cut, want)
		}
		for i, seq := range seqs {
			if seq != uint64(i+1) { // #nosec G115 -- i is never negative
				t.Errorf("entry %d has the seqnum %d, want %d (SeekAll(s) stands on entry s+1)", i, seq, i+1)
				break
			}
		}
	}
	if journal == "kernel.journal" && len(cut) != 0 {
		t.Errorf("entries with a cut MESSAGE = %v, want none", cut)
	}
	requireSeekAllAfterEverySeqnum(t, f, seqs, opt)
	if r.violated != "" {
		t.Errorf("reader misbehaved: %s", r.violated)
	}
}

// readSampleWith reads every entry of a real sample with NextWith, compares MESSAGE with the export entry of the same stamps and
// returns the seqnums, those of the entries whose MESSAGE is 700 bytes long and those of the entries whose MESSAGE is cut.
func readSampleWith(t *testing.T, f *File, export map[stampKey][]exField, opt *ReadOptions) (seqs, long, cut []uint64) {
	t.Helper()
	c, err := f.SeekAll(0)
	if err != nil {
		t.Fatalf("SeekAll(0) error = %v, want nil", err)
	}
	var e Entry
	for {
		ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, opt)
		if err != nil {
			t.Fatalf("NextWith() after %d entries: error = %v, want nil", len(seqs), err)
		}
		if !ok {
			break
		}
		seqs = append(seqs, e.Seqnum)
		if e.Undecoded != 0 {
			t.Errorf("entry %d has %d undecoded items, want 0", e.Seqnum, e.Undecoded)
		}
		n, wasCut := requireSameMessage(t, &e, export, opt.MaxPayload)
		if n == 700 {
			long = append(long, e.Seqnum)
		}
		if wasCut {
			cut = append(cut, e.Seqnum)
		}
	}
	if c.Torn() {
		t.Error("Torn() = true after the end of the list, want false for a whole file")
	}
	return seqs, long, cut
}

// requireSameMessage wants e to hold exactly one MESSAGE, equal to that of the export entry of the same stamps when the export value
// has at most limit bytes (Cut false) and equal to its first limit bytes with Cut true when it is longer. It returns the length of the
// value read (0 when the check failed) and its Cut flag.
func requireSameMessage(t *testing.T, e *Entry, export map[stampKey][]exField, limit int) (length int, cut bool) {
	t.Helper()
	pair, found := export[stampKey{e.RealtimeUS, e.MonotonicUS}]
	if !found {
		t.Fatalf("entry %d (realtime %d, monotonic %d) has no entry of the same stamps in the export", e.Seqnum, e.RealtimeUS, e.MonotonicUS)
	}
	want := exValue(pair, "MESSAGE")
	msg, _ := e.Field("MESSAGE")
	if len(msg) != 1 {
		t.Errorf("entry %d holds %d MESSAGE fields, want 1", e.Seqnum, len(msg))
		return 0, false
	}
	wantCut := len(want) > limit
	if wantCut {
		want = want[:limit]
	}
	if msg[0].Cut != wantCut || !bytes.Equal(msg[0].Value, want) {
		t.Errorf("entry %d MESSAGE = %d bytes cut %v, want the %d bytes of the export cut %v", e.Seqnum, len(msg[0].Value), msg[0].Cut, len(want), wantCut)
		return 0, false
	}
	return len(msg[0].Value), msg[0].Cut
}

// requireSeekAllAfterEverySeqnum wants SeekAll(s) to stand on the first entry of seqs after s, for every s up to the last seqnum.
func requireSeekAllAfterEverySeqnum(t *testing.T, f *File, seqs []uint64, opt *ReadOptions) {
	t.Helper()
	var e Entry
	for s := uint64(0); s < seqs[len(seqs)-1]; s++ {
		c, err := f.SeekAll(s)
		if err != nil {
			t.Fatalf("SeekAll(%d) error = %v, want nil", s, err)
		}
		want, _ := slices.BinarySearch(seqs, s+1)
		ok, err := f.NextWith(&c, []string{"MESSAGE"}, &e, opt)
		if err != nil || !ok || e.Seqnum != seqs[want] {
			t.Fatalf("SeekAll(%d) then NextWith() = seqnum %d, %v, %v, want seqnum %d", s, e.Seqnum, ok, err, seqs[want])
		}
	}
}
