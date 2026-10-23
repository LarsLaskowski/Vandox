package journal

import (
	"bytes"
	"errors"
	"slices"
	"strconv"
	"strings"
	"testing"
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
		if limit < 16384 {
			t.Errorf("the decoder was given the limit %d, want at least MaxPayload 16384", limit)
		}
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
		if !got.Cut || len(got.Value) == 0 || len(got.Value) > 100 || !strings.HasPrefix(whole, string(got.Value)) {
			t.Errorf("entry %d MESSAGE = %d bytes cut %v, want a non-empty prefix of at most 100 bytes of the %d byte value with Cut set", i, len(got.Value), got.Cut, len(whole))
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
	if host := value(t, &e, "_HOSTNAME"); string(host.Value) != "web-1" {
		t.Errorf("_HOSTNAME = %q, want the other fields read", host.Value)
	}
	nextWith(t, f, &c, []string{"MESSAGE", "_HOSTNAME"}, &e, readOptions(d))
	if got := value(t, &e, "MESSAGE"); string(got.Value) != "after" || e.Undecoded != 0 {
		t.Errorf("next entry MESSAGE = %q undecoded %d, want after and 0", got.Value, e.Undecoded)
	}
}
