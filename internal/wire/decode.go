package wire

import (
	"bufio"
	"bytes"
	"compress/gzip"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"slices"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// kinds maps a record kind to a constructor of its payload.
var kinds = map[model.Kind]func() model.Payload{
	model.KindMetric:             func() model.Payload { return new(model.MetricPoint) },
	model.KindProcessSnapshot:    func() model.Payload { return new(model.ProcessSnapshot) },
	model.KindConnectionSnapshot: func() model.Payload { return new(model.ConnectionSnapshot) },
	model.KindServiceState:       func() model.Payload { return new(model.ServiceState) },
	model.KindMariaDBStatus:      func() model.Payload { return new(model.MariaDBStatus) },
	model.KindKernelEvent:        func() model.Payload { return new(model.KernelEvent) },
	model.KindLogLine:            func() model.Payload { return new(model.LogLine) },
	model.KindGap:                func() model.Payload { return new(model.Gap) },
	model.KindBackupRun:          func() model.Payload { return new(model.BackupRun) },
}

// envelope is the decoded form of a record line.
type envelope struct {
	Kind       string          `json:"kind"`
	Source     string          `json:"source"`
	Seq        uint64          `json:"seq"`
	CapturedAt time.Time       `json:"captured_at"`
	Data       json.RawMessage `json:"data"`
}

// guardReader counts the decompressed bytes against a limit and remembers the first read error, so a
// partial last line cut by an error is never decoded as a record.
type guardReader struct {
	r     io.Reader
	n     int64
	limit int64
	err   error
}

func (g *guardReader) Read(p []byte) (int, error) {
	if g.err != nil {
		return 0, g.err
	}
	n, err := g.r.Read(p)
	g.n += int64(n)
	if g.n > g.limit {
		g.err = fmt.Errorf("%w: more than %d decompressed bytes", ErrLimitExceeded, g.limit)
		return n - int(g.n-g.limit), g.err
	}
	if err != nil && err != io.EOF {
		g.err = err
	}
	return n, err
}

// Fixed texts of the line check; none names a key, because a key is input from outside.
var (
	errDuplicateKey = fmt.Errorf("%w: duplicate key", ErrMalformed)
	errKeyNotASCII  = fmt.Errorf("%w: key not ASCII", ErrMalformed)
	errTooDeep      = fmt.Errorf("%w: nesting deeper than %d", ErrMalformed, MaxDepth)

	errNotUTF8           = fmt.Errorf("%w: line not valid UTF-8", ErrMalformed)
	errUnpairedSurrogate = fmt.Errorf("%w: unpaired surrogate escape", ErrMalformed)
)

const (
	// smallKeys is the number of keys an object compares by scanning its key list, before a set is used.
	smallKeys = 8
	// keptKeys is the largest object whose set and key list are kept for the next object at the same depth.
	keptKeys = 64
)

// keyLevel holds the keys of the object that is open at one depth. keys lists the ASCII-lower-cased keys the
// object added; set holds the same keys once there are more than smallKeys of them, and is empty otherwise.
type keyLevel struct {
	set  map[string]struct{}
	keys []string
}

// add records key and reports whether the open object had it already.
func (l *keyLevel) add(key string) bool {
	n := len(l.keys)
	if n < smallKeys {
		if slices.Contains(l.keys, key) {
			return true
		}
		l.keys = append(l.keys, key)
		return false
	}
	if n == smallKeys {
		if slices.Contains(l.keys, key) {
			return true
		}
		if l.set == nil {
			l.set = make(map[string]struct{}, 2*smallKeys)
		}
		for _, k := range l.keys {
			l.set[k] = struct{}{}
		}
	} else if _, dup := l.set[key]; dup {
		return true
	}
	l.set[key] = struct{}{}
	l.keys = append(l.keys, key)
	return false
}

// release removes exactly the keys the object added, so the level is empty for the next object. An object of
// more than keptKeys keys drops its set and list instead. The set is never cleared as a whole: that would cost
// the capacity a larger object once reached.
func (l *keyLevel) release() {
	n := len(l.keys)
	if n > keptKeys {
		l.set, l.keys = nil, nil
		return
	}
	if n > smallKeys {
		for _, k := range l.keys {
			delete(l.set, k)
		}
	}
	clear(l.keys)
	l.keys = l.keys[:0]
}

// frame is one open object or array of the walk; it holds no keys.
type frame struct {
	object    bool
	expectKey bool
}

// keyCheck holds the state of the line check, reused for every line of one Decoder: the frame stack and, per
// depth, the keys seen in the open object. Every set is empty between two calls of checkLine.
type keyCheck struct {
	frames []frame
	levels [MaxDepth]keyLevel
}

// checkLine checks the JSON-level rules of one line before it is bound: no key outside ASCII after unescaping,
// no two keys of one object equal ignoring ASCII case, no nesting deeper than MaxDepth. A line that is one
// complete JSON value is then checked for the text rule (checkText); any other line is left to the binding, which
// reports its syntax error. It returns nil or an error wrapping ErrMalformed whose text names the broken rule but
// never the key or the text.
func (c *keyCheck) checkLine(line []byte) error {
	if c.frames == nil {
		c.frames = make([]frame, 0, MaxDepth)
	}
	complete, err := c.walk(line)
	c.unwind()
	if err != nil || !complete {
		return err
	}
	return checkText(line)
}

// unwind closes every object still open, so the next line starts with empty levels.
func (c *keyCheck) unwind() {
	for len(c.frames) > 0 {
		c.pop()
	}
}

// pop closes the innermost frame.
func (c *keyCheck) pop() {
	top := len(c.frames) - 1
	if c.frames[top].object {
		c.levels[top].release()
	}
	c.frames = c.frames[:top]
}

// checkText checks the text rule of one line: valid UTF-8, and every \u escape of a surrogate a high half directly
// followed by an escaped low half. It returns nil, errNotUTF8 or errUnpairedSurrogate.
func checkText(line []byte) error {
	if !utf8.Valid(line) {
		return errNotUTF8
	}
	for i := 0; i < len(line); {
		step, err := scanEscape(line[i:])
		if err != nil {
			return err
		}
		i += step
	}
	return nil
}

// scanEscape judges the byte at the start of b and returns how many bytes the scan skips: one for an ordinary byte, two
// for a backslash that does not start a complete \uXXXX escape, six for an escape and twelve for a surrogate pair.
func scanEscape(b []byte) (int, error) {
	if b[0] != '\\' {
		return 1, nil
	}
	unit, ok := escapedUnit(b)
	if !ok {
		return 2, nil
	}
	switch {
	case unit >= 0xDC00 && unit <= 0xDFFF:
		return 0, errUnpairedSurrogate
	case unit >= 0xD800 && unit <= 0xDBFF:
		low, ok := escapedUnit(b[6:])
		if !ok || low < 0xDC00 || low > 0xDFFF {
			return 0, errUnpairedSurrogate
		}
		return 12, nil
	}
	return 6, nil
}

// escapedUnit returns the UTF-16 code unit of the \uXXXX escape at the start of b, and false when b does not start
// with a complete one.
func escapedUnit(b []byte) (rune, bool) {
	if len(b) < 6 || b[0] != '\\' || b[1] != 'u' {
		return 0, false
	}
	var unit rune
	for _, h := range b[2:6] {
		var d byte
		switch {
		case h >= '0' && h <= '9':
			d = h - '0'
		case h >= 'a' && h <= 'f':
			d = h - 'a' + 10
		case h >= 'A' && h <= 'F':
			d = h - 'A' + 10
		default:
			return 0, false
		}
		unit = unit<<4 | rune(d)
	}
	return unit, true
}

// walk reads the first value of line token by token. A syntax error is reported as malformed; a line that ends
// inside a value is left to the binding that follows, which reports it. complete is true when the first value ended
// and only JSON whitespace follows it.
func (c *keyCheck) walk(line []byte) (complete bool, err error) {
	dec := json.NewDecoder(bytes.NewReader(line))
	dec.UseNumber()
	for {
		tok, err := dec.Token()
		if err != nil {
			if errors.Is(err, io.EOF) {
				return false, nil
			}
			return false, fmt.Errorf("%w: %w", ErrMalformed, err)
		}
		if err := c.step(tok); err != nil {
			return false, err
		}
		if len(c.frames) == 0 {
			return onlyWhitespace(line[dec.InputOffset():]), nil
		}
	}
}

// onlyWhitespace reports whether b holds nothing but JSON whitespace.
func onlyWhitespace(b []byte) bool {
	for _, c := range b {
		if c != ' ' && c != '\t' && c != '\r' && c != '\n' {
			return false
		}
	}
	return true
}

// step applies one token to the frame stack.
func (c *keyCheck) step(tok json.Token) error {
	if d, ok := tok.(json.Delim); ok {
		if d == '{' || d == '[' {
			return c.open(d == '{')
		}
		c.pop()
		c.valueDone()
		return nil
	}
	if key, ok := tok.(string); ok && c.expectsKey() {
		return c.key(key)
	}
	c.valueDone()
	return nil
}

// open pushes an object or an array and enforces the depth.
func (c *keyCheck) open(object bool) error {
	if len(c.frames) >= MaxDepth {
		return errTooDeep
	}
	c.frames = append(c.frames, frame{object: object, expectKey: object})
	return nil
}

// expectsKey reports whether the next string token is a key.
func (c *keyCheck) expectsKey() bool {
	return len(c.frames) > 0 && c.frames[len(c.frames)-1].expectKey
}

// valueDone notes that a value ended, so an enclosing object expects its next key.
func (c *keyCheck) valueDone() {
	if n := len(c.frames); n > 0 && c.frames[n-1].object {
		c.frames[n-1].expectKey = true
	}
}

// key checks one key of the innermost object.
func (c *keyCheck) key(key string) error {
	top := len(c.frames) - 1
	c.frames[top].expectKey = false
	for i := 0; i < len(key); i++ {
		if key[i] >= utf8.RuneSelf {
			return errKeyNotASCII
		}
	}
	if c.levels[top].add(asciiLower(key)) {
		return errDuplicateKey
	}
	return nil
}

// asciiLower lower-cases the ASCII letters of s; it returns s itself when there is nothing to change.
func asciiLower(s string) string {
	for i := 0; i < len(s); i++ {
		if s[i] >= 'A' && s[i] <= 'Z' {
			b := []byte(s)
			for j := i; j < len(b); j++ {
				if b[j] >= 'A' && b[j] <= 'Z' {
					b[j] += 'a' - 'A'
				}
			}
			return string(b)
		}
	}
	return s
}

// Decoder reads a batch record by record. It is not safe for concurrent use.
type Decoder struct {
	zr     *gzip.Reader
	guard  *guardReader
	sc     *bufio.Scanner
	lim    Limits
	header Header
	line   int
	count  int
	last   uint64
	err    error
	keys   keyCheck
}

// NewDecoder reads and validates the header of the batch in r.
func NewDecoder(r io.Reader, lim Limits) (*Decoder, error) {
	def := DefaultLimits()
	if lim.MaxLineBytes <= 0 {
		lim.MaxLineBytes = def.MaxLineBytes
	}
	lim.MaxLineBytes = min(lim.MaxLineBytes, math.MaxInt-1)
	if lim.MaxBatchBytes <= 0 {
		lim.MaxBatchBytes = def.MaxBatchBytes
	}
	if lim.MaxRecords <= 0 {
		lim.MaxRecords = def.MaxRecords
	}
	zr, err := gzip.NewReader(r)
	if err != nil {
		return nil, &DecodeError{Line: 1, Err: fmt.Errorf("%w: %w", ErrMalformed, err)}
	}
	d := &Decoder{zr: zr, guard: &guardReader{r: zr, limit: lim.MaxBatchBytes}, lim: lim}
	d.sc = bufio.NewScanner(d.guard)
	d.sc.Buffer(make([]byte, 0, min(64<<10, lim.MaxLineBytes+1)), lim.MaxLineBytes+1)
	d.sc.Split(d.splitLines)
	if err := d.readHeader(); err != nil {
		_ = zr.Close()
		return nil, err
	}
	return d, nil
}

// splitLines is bufio.ScanLines, except that an unterminated last line is dropped when the stream
// failed, because it may have been cut by the failure, and is rejected as too long when it exceeds
// MaxLineBytes (a trailing \r counts), because the scanner buffer would otherwise accept it.
func (d *Decoder) splitLines(data []byte, atEOF bool) (int, []byte, error) {
	advance, token, err := bufio.ScanLines(data, atEOF)
	if token != nil && atEOF && data[advance-1] != '\n' {
		if d.guard.err != nil {
			return 0, nil, nil
		}
		if len(data) > d.lim.MaxLineBytes {
			return 0, nil, bufio.ErrTooLong
		}
	}
	return advance, token, err
}

func (d *Decoder) readHeader() error {
	if !d.sc.Scan() {
		d.line = 1
		err := d.streamEnd()
		if errors.Is(err, io.EOF) {
			err = fmt.Errorf("%w: no header line", ErrMalformed)
		}
		return d.fail(err)
	}
	d.line = 1
	raw := d.sc.Bytes()
	if err := d.keys.checkLine(raw); err != nil {
		return d.fail(err)
	}
	var ver struct {
		FormatMajor *int64 `json:"format_major"`
	}
	if err := json.Unmarshal(raw, &ver); err != nil {
		return d.fail(fmt.Errorf("%w: %w", ErrMalformed, err))
	}
	if ver.FormatMajor == nil {
		return d.fail(fmt.Errorf("%w: format_major missing", ErrUnsupportedVersion))
	}
	if *ver.FormatMajor != MajorVersion {
		return d.fail(fmt.Errorf("%w: major version %d", ErrUnsupportedVersion, *ver.FormatMajor))
	}
	var h Header
	if err := json.Unmarshal(raw, &h); err != nil {
		return d.fail(fmt.Errorf("%w: %w", ErrMalformed, err))
	}
	if err := h.Validate(); err != nil {
		return d.fail(err)
	}
	d.header = h
	d.err = nil
	return nil
}

// fail wraps err with the current line and makes it sticky.
func (d *Decoder) fail(err error) error {
	d.err = &DecodeError{Line: d.line, Err: err}
	return d.err
}

// streamEnd classifies the end of the scan: io.EOF for a clean end, otherwise the matching error.
func (d *Decoder) streamEnd() error {
	err := d.sc.Err()
	switch {
	case err == nil:
		return io.EOF
	case errors.Is(err, ErrLimitExceeded):
		return err
	case errors.Is(err, bufio.ErrTooLong):
		return fmt.Errorf("%w: line longer than %d bytes", ErrLimitExceeded, d.lim.MaxLineBytes)
	default:
		return fmt.Errorf("%w: %w", ErrMalformed, err)
	}
}

// Header returns the validated header.
func (d *Decoder) Header() Header { return d.header }

// Next returns the next record, io.EOF after the last one.
func (d *Decoder) Next() (model.Record, error) {
	if d.err != nil {
		return model.Record{}, d.err
	}
	if !d.sc.Scan() {
		d.line++
		err := d.streamEnd()
		if errors.Is(err, io.EOF) {
			if d.count == 0 {
				return model.Record{}, d.fail(ErrEmptyBatch)
			}
			d.err = io.EOF
			return model.Record{}, io.EOF
		}
		return model.Record{}, d.fail(err)
	}
	d.line++
	if d.count >= d.lim.MaxRecords {
		return model.Record{}, d.fail(fmt.Errorf("%w: more than %d records", ErrLimitExceeded, d.lim.MaxRecords))
	}
	rec, err := d.decodeRecord(d.sc.Bytes())
	if err != nil {
		return model.Record{}, d.fail(err)
	}
	if rec.Seq <= d.last {
		return model.Record{}, d.fail(fmt.Errorf("%w: seq %d after %d", ErrSequence, rec.Seq, d.last))
	}
	d.last = rec.Seq
	d.count++
	return rec, nil
}

func (d *Decoder) decodeRecord(line []byte) (model.Record, error) {
	if err := d.keys.checkLine(line); err != nil {
		return model.Record{}, err
	}
	var env envelope
	if err := json.Unmarshal(line, &env); err != nil {
		return model.Record{}, fmt.Errorf("%w: %w", ErrMalformed, err)
	}
	newPayload, ok := kinds[model.Kind(env.Kind)]
	if !ok {
		return model.Record{}, fmt.Errorf("%w: %s", ErrUnknownKind, model.QuoteName(env.Kind))
	}
	if len(env.Data) == 0 || bytes.Equal(env.Data, []byte("null")) {
		return model.Record{}, &model.FieldError{Field: "data", Reason: "required"}
	}
	payload := newPayload()
	if err := json.Unmarshal(env.Data, payload); err != nil {
		return model.Record{}, fmt.Errorf("%w: %w", ErrMalformed, err)
	}
	rec := model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: env.Source, Seq: env.Seq, CapturedAt: env.CapturedAt},
		Data: payload,
	}
	if err := rec.Validate(); err != nil {
		return model.Record{}, err
	}
	return rec, nil
}

// Close closes the gzip reader; it does not close the underlying reader.
func (d *Decoder) Close() error { return d.zr.Close() }
