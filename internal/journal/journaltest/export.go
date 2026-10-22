package journaltest

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"strconv"
)

// maxBinaryField bounds one binary field of an export, so that damaged input cannot ask for a huge allocation.
const maxBinaryField = 1 << 26

// ReadExport parses journal export text (journalctl -o export) into entries. A field is stored under its name; a field that
// repeats within an entry is stored under NAME#2, NAME#3 and so on in the order of appearance. Both forms of a value are read: a
// NAME=value line and a line NAME followed by an 8-byte little-endian length, the data and a line feed.
func ReadExport(r io.Reader) ([]map[string][]byte, error) {
	br := bufio.NewReaderSize(r, 1<<20)
	var entries []map[string][]byte
	cur := map[string][]byte{}
	for {
		line, err := br.ReadBytes('\n')
		if len(line) == 0 && errors.Is(err, io.EOF) {
			break
		}
		if err != nil && !errors.Is(err, io.EOF) {
			return nil, err
		}
		line = bytes.TrimSuffix(line, []byte("\n"))
		if len(line) == 0 {
			if len(cur) > 0 {
				entries, cur = append(entries, cur), map[string][]byte{}
			}
			continue
		}
		name, value, perr := parseField(br, line)
		if perr != nil {
			return nil, perr
		}
		cur[uniqueName(cur, name)] = value
	}
	if len(cur) > 0 {
		entries = append(entries, cur)
	}
	return entries, nil
}

// parseField splits a NAME=value line, or reads the binary value that follows a line holding only the name.
func parseField(br *bufio.Reader, line []byte) (string, []byte, error) {
	if i := bytes.IndexByte(line, '='); i >= 0 {
		return string(line[:i]), append([]byte{}, line[i+1:]...), nil
	}
	var n [8]byte
	if _, err := io.ReadFull(br, n[:]); err != nil {
		return "", nil, fmt.Errorf("journaltest: binary field length: %w", err)
	}
	size := binary.LittleEndian.Uint64(n[:])
	if size > maxBinaryField {
		return "", nil, errors.New("journaltest: binary field too large")
	}
	value := make([]byte, size)
	if _, err := io.ReadFull(br, value); err != nil {
		return "", nil, fmt.Errorf("journaltest: binary field data: %w", err)
	}
	if c, err := br.ReadByte(); err != nil || c != '\n' {
		return "", nil, errors.New("journaltest: binary field not ended by a line feed")
	}
	return string(line), value, nil
}

// uniqueName returns name, or NAME#k for the k-th occurrence of name in the entry.
func uniqueName(entry map[string][]byte, name string) string {
	if _, taken := entry[name]; !taken {
		return name
	}
	for k := 2; ; k++ {
		candidate := name + "#" + strconv.Itoa(k)
		if _, taken := entry[candidate]; !taken {
			return candidate
		}
	}
}
