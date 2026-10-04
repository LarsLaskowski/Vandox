package wire

import (
	"errors"
	"io"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// EncodeBatch validates b and writes it gzip-compressed to w; nothing is written when b is invalid.
func EncodeBatch(w io.Writer, b *Batch) error { return errors.New("not implemented") }

// CheckRecord validates one agent record and returns the number of bytes its line adds to the
// decompressed batch (including '\n'); *RecordSizeError if the line exceeds the line limit.
func CheckRecord(r *model.Record) (int, error) { return 0, errors.New("not implemented") }
