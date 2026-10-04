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
	"time"

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

// Decoder reads a batch record by record.
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
	var ver struct {
		FormatMajor *int `json:"format_major"`
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
