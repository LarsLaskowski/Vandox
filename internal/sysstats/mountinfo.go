package sysstats

import (
	"bufio"
	"bytes"
	"errors"
	"fmt"
	"io"
	"io/fs"
)

// Limits and name of the mount table reader.
const (
	mountinfoFile         = "self/mountinfo"
	maxMountinfoLineBytes = 65536
	mountinfoBufferBytes  = 4096
	mountPointField       = 5
)

// readMounted reads self/mountinfo line by line and returns which of the paths are the mount point of a line. Every line is
// checked, so a bad line fails the sample wherever it is.
func readMounted(proc fs.FS, paths []string) (map[string]bool, error) {
	f, err := proc.Open(mountinfoFile)
	if err != nil {
		return nil, fmt.Errorf("sysstats: %s: %w", mountinfoFile, err)
	}
	defer func() { _ = f.Close() }()

	want := make(map[string]bool, len(paths))
	for _, p := range paths {
		want[p] = true
	}
	mounted := make(map[string]bool, len(paths))
	r := bufio.NewReaderSize(f, mountinfoBufferBytes)
	for n := 1; ; n++ {
		line, err := readLine(r)
		if errors.Is(err, io.EOF) {
			return mounted, nil
		}
		if err != nil {
			return nil, fmt.Errorf("sysstats: %s: line %d: %w", mountinfoFile, n, err)
		}
		point, ok := mountPointOf(line)
		if !ok {
			return nil, fmt.Errorf("sysstats: %s: line %d: not a mountinfo line", mountinfoFile, n)
		}
		if want[point] {
			mounted[point] = true
		}
	}
}

// errLineTooLong is the error of a mount table line above maxMountinfoLineBytes.
var errLineTooLong = fmt.Errorf("longer than %d bytes", maxMountinfoLineBytes)

// readLine returns the next line without its line feed, at most maxMountinfoLineBytes long; io.EOF when there is none.
func readLine(r *bufio.Reader) ([]byte, error) {
	var line []byte
	for {
		chunk, err := r.ReadSlice('\n')
		line = append(line, chunk...)
		switch {
		case err == nil:
			line = line[:len(line)-1]
		case errors.Is(err, bufio.ErrBufferFull):
			if len(line) > maxMountinfoLineBytes {
				return nil, errLineTooLong
			}
			continue
		case errors.Is(err, io.EOF) && len(line) > 0:
		default:
			return nil, err
		}
		if len(line) > maxMountinfoLineBytes {
			return nil, errLineTooLong
		}
		return line, nil
	}
}

// mountPointOf returns the mount point (field 5) of a mountinfo line with the kernel's escapes decoded.
func mountPointOf(line []byte) (string, bool) {
	fields := bytes.SplitN(line, []byte{' '}, mountPointField+1)
	if len(fields) < mountPointField {
		return "", false
	}
	return unescape(fields[mountPointField-1])
}

// unescape decodes the escapes of a mount table field: a backslash and exactly three octal digits is one byte.
func unescape(field []byte) (string, bool) {
	out := make([]byte, 0, len(field))
	for i := 0; i < len(field); i++ {
		if field[i] != '\\' {
			out = append(out, field[i])
			continue
		}
		b, ok := octalByte(field[i+1:])
		if !ok {
			return "", false
		}
		out = append(out, b)
		i += 3
	}
	return string(out), true
}

// octalByte decodes the first three bytes of s as an octal number of at most 255.
func octalByte(s []byte) (byte, bool) {
	if len(s) < 3 {
		return 0, false
	}
	v := 0
	for _, c := range s[:3] {
		if c < '0' || c > '7' {
			return 0, false
		}
		v = v*8 + int(c-'0')
	}
	return byte(v), v <= 255
}
