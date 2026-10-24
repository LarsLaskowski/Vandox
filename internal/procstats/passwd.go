package procstats

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"strconv"
	"strings"
)

// Bounds of the read of /etc/passwd.
const (
	maxPasswdBytes    = 1 << 20 // the most of the file that is read
	maxPasswdLine     = 4096    // the longest line, without its line feed
	maxUserNameBytes  = 256     // the longest user name that is kept
	passwdMinFields   = 3
	maxPasswdUIDDigit = 10
)

// errPasswdLine is the reason a passwd line is refused; it names no content.
var errPasswdLine = errors.New("line longer than the limit")

// ReadUsers returns the user names of etc/passwd by user ID: a line is "name:password:uid:..."; a line with fewer than 3
// fields, an empty name or a UID that is not 1 to 10 digits within 32 bits is skipped, the first line of a UID wins, and a
// name is made valid UTF-8 and cut to 256 bytes.
func ReadUsers(etc fs.FS) (map[uint32]string, error) {
	f, err := etc.Open("passwd")
	if err != nil {
		return nil, fmt.Errorf("procstats: passwd: %w", err)
	}
	defer func() { _ = f.Close() }()

	r := bufio.NewReaderSize(io.LimitReader(f, maxPasswdBytes+1), maxPasswdLine+1)
	users := map[uint32]string{}
	total := 0
	for {
		line, err := r.ReadSlice('\n')
		total += len(line)
		if total > maxPasswdBytes {
			return nil, fmt.Errorf("procstats: passwd: %w", errTooLarge)
		}
		if errors.Is(err, bufio.ErrBufferFull) {
			return nil, fmt.Errorf("procstats: passwd: %w", errPasswdLine)
		}
		addUser(users, strings.TrimSuffix(string(line), "\n"))
		if errors.Is(err, io.EOF) {
			return users, nil
		}
		if err != nil {
			return nil, fmt.Errorf("procstats: passwd: %w", err)
		}
	}
}

// addUser adds the user of one passwd line to users unless the line is skipped or its UID is already there.
func addUser(users map[uint32]string, line string) {
	fields := strings.SplitN(line, ":", passwdMinFields+1)
	if len(fields) < passwdMinFields || fields[0] == "" || len(fields[2]) > maxPasswdUIDDigit || !isDigits(fields[2]) {
		return
	}
	uid, err := strconv.ParseUint(fields[2], 10, 32)
	if err != nil {
		return
	}
	if _, taken := users[uint32(uid)]; taken {
		return
	}
	name, _ := cleanText(fields[0], maxUserNameBytes)
	users[uint32(uid)] = name
}
