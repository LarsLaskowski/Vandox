package wire

import (
	"bytes"
	"compress/gzip"
	"encoding/json"
	"fmt"
	"io"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// recordLine is the encoded form of a record: origin and receive time are not part of the wire format.
type recordLine struct {
	Kind       model.Kind    `json:"kind"`
	Source     string        `json:"source"`
	Seq        uint64        `json:"seq"`
	CapturedAt time.Time     `json:"captured_at"`
	Data       model.Payload `json:"data"`
}

// marshalLine encodes r as one JSON line without the trailing '\n'. r must have been validated.
func marshalLine(r *model.Record) ([]byte, error) {
	line, err := json.Marshal(recordLine{
		Kind:       r.Kind(),
		Source:     r.Source,
		Seq:        r.Seq,
		CapturedAt: r.CapturedAt.UTC(),
		Data:       r.Data,
	})
	if err != nil {
		return nil, fmt.Errorf("wire: encode record: %w", err)
	}
	return line, nil
}

// EncodeBatch validates b and writes it gzip-compressed to w; nothing is written when b is invalid.
// If writing to w fails, the output is incomplete and must be discarded. w is not closed.
func EncodeBatch(w io.Writer, b *Batch) error {
	if err := b.Validate(); err != nil {
		return err
	}
	lim := DefaultLimits()
	head, err := json.Marshal(b.Header)
	if err != nil {
		return fmt.Errorf("wire: encode header: %w", err)
	}
	var plain bytes.Buffer
	plain.Write(head)
	plain.WriteByte('\n')
	for i := range b.Records {
		line, err := marshalLine(&b.Records[i])
		if err != nil {
			return err
		}
		if len(line) > lim.MaxLineBytes {
			return &RecordSizeError{Index: i, Size: len(line), Limit: lim.MaxLineBytes}
		}
		if int64(plain.Len())+int64(len(line))+1 > lim.MaxBatchBytes {
			return fmt.Errorf("%w: batch exceeds %d bytes at records[%d]", ErrLimitExceeded, lim.MaxBatchBytes, i)
		}
		plain.Write(line)
		plain.WriteByte('\n')
	}
	zw := gzip.NewWriter(w)
	if _, err := zw.Write(plain.Bytes()); err != nil {
		return fmt.Errorf("wire: write batch: %w", err)
	}
	if err := zw.Close(); err != nil {
		return fmt.Errorf("wire: write batch: %w", err)
	}
	return nil
}

// CheckRecord validates one agent record and returns the number of bytes its line adds to the
// decompressed batch (including '\n'); *RecordSizeError if the line exceeds the line limit.
func CheckRecord(r *model.Record) (int, error) {
	if err := r.Validate(); err != nil {
		return 0, err
	}
	if r.Origin != model.OriginAgent {
		return 0, &model.FieldError{Field: "origin", Reason: "must be agent"}
	}
	line, err := marshalLine(r)
	if err != nil {
		return 0, err
	}
	if limit := DefaultLimits().MaxLineBytes; len(line) > limit {
		return 0, &RecordSizeError{Index: -1, Size: len(line), Limit: limit}
	}
	return len(line) + 1, nil
}
