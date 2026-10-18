package procstats

import (
	"bufio"
	"bytes"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"strconv"
	"time"
)

// Bounds of the reads of /proc/stat.
const (
	maxProcStatBytes = 1 << 20  // the most of the file that is read
	maxProcStatLine  = 64 << 10 // the longest line that is held; a longer one (intr) is skipped to its end
)

// maxUnixSeconds is the last second of the year 9999: Go's JSON encoding of a time fails beyond it.
const maxUnixSeconds = 253402300799

// Reasons a read is refused; none of them names content.
var (
	errTooLarge   = errors.New("larger than the limit")
	errNoBootTime = errors.New("btime missing, repeated or not a number")
)

// readBootTime returns the boot time from the btime line of proc/stat: "btime", one space and 1 to 20 digits, exactly once.
func readBootTime(proc fs.FS) (time.Time, error) {
	f, err := proc.Open("stat")
	if err != nil {
		return time.Time{}, fmt.Errorf("procstats: stat: %w", err)
	}
	defer func() { _ = f.Close() }()

	value, err := findBootTime(bufio.NewReaderSize(io.LimitReader(f, maxProcStatBytes+1), maxProcStatLine))
	if err != nil {
		return time.Time{}, fmt.Errorf("procstats: stat: %w", err)
	}
	return bootTimeOf(value)
}

// findBootTime returns the text after "btime " of the one line that starts with it. The whole input may hold at most
// maxProcStatBytes; a line longer than the reader's buffer is skipped to its end without being held.
func findBootTime(r *bufio.Reader) (string, error) {
	var scan bootScan
	for {
		line, err := r.ReadSlice('\n')
		partial := errors.Is(err, bufio.ErrBufferFull)
		if scanErr := scan.add(line, partial); scanErr != nil {
			return "", scanErr
		}
		switch {
		case err == nil || partial:
		case errors.Is(err, io.EOF):
			return scan.value, nil
		default:
			return "", err
		}
	}
}

// bootScan is the state of the search for the btime line.
type bootScan struct {
	value    string // the text after "btime " of the line found
	total    int    // bytes seen
	skipping bool   // whether the current line is longer than the buffer
}

// add takes the next piece of the input; partial tells that the line goes on in the next piece.
func (b *bootScan) add(piece []byte, partial bool) error {
	b.total += len(piece)
	if b.total > maxProcStatBytes {
		return errTooLarge
	}
	if partial {
		b.skipping = true
		return nil
	}
	text, found := bytes.CutPrefix(piece, []byte("btime "))
	if found && !b.skipping {
		if b.value != "" {
			return errNoBootTime
		}
		// An empty value is kept as "-", which is no number, so the line still counts as the one btime line.
		if b.value = string(bytes.TrimSuffix(text, []byte("\n"))); b.value == "" {
			b.value = "-"
		}
	}
	b.skipping = false
	return nil
}

// bootTimeOf converts the text of the btime value into the time.
func bootTimeOf(value string) (time.Time, error) {
	if !isDigits(value) {
		return time.Time{}, fmt.Errorf("procstats: stat: %w", errNoBootTime)
	}
	secs, err := strconv.ParseUint(value, 10, 63)
	if err != nil || secs > maxUnixSeconds {
		return time.Time{}, fmt.Errorf("procstats: stat: %w", errNoBootTime)
	}
	return time.Unix(int64(secs), 0).UTC(), nil
}
