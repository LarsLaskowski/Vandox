package connstats

import (
	"bufio"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net/netip"
	"strconv"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Bounds of the table reader.
const (
	maxLine       = 512 // longest table line read, without its newline
	readerSize    = 4096
	minFields     = 10 // sl, local, remote, state, queues, timer, retransmits, uid, timeout, inode
	inodeField    = 9
	maxInodeDigit = 20 // digits of a 64-bit inode
	stateDigits   = 2
	portDigits    = 4
	wordDigits    = 8
	hexBase       = 16
	decimalBase   = 10
)

// listenState is the name of the state of a listening socket.
const listenState = "LISTEN"

// Fixed reasons of an unreadable line.
var (
	errFormat    = errors.New("not in the expected format")
	errState     = errors.New("unknown state")
	errLineLong  = errors.New("line too long")
	tcpStateName = [...]string{
		1: "ESTABLISHED", 2: "SYN_SENT", 3: "SYN_RECV", 4: "FIN_WAIT1", 5: "FIN_WAIT2", 6: "TIME_WAIT",
		7: "CLOSE", 8: "CLOSE_WAIT", 9: "LAST_ACK", 10: "LISTEN", 11: "CLOSING", 12: "NEW_SYN_RECV",
	}
)

// table is one of the four socket tables; words is 1 (IPv4) or 4 (IPv6).
type table struct {
	name  string
	proto model.Proto
	words int
}

// socket is one parsed line of a table.
type socket struct {
	proto         model.Proto
	local, remote netip.AddrPort
	state         string
	inode         uint64
}

// parseLine parses one line of table t, given without its newline.
func parseLine(line []byte, t table) (socket, error) {
	var fields [minFields][]byte
	if splitFields(line, &fields) < minFields {
		return socket{}, errFormat
	}
	if !isSlot(fields[0]) {
		return socket{}, errFormat
	}
	local, err := parseEndpoint(fields[1], t.words)
	if err != nil {
		return socket{}, err
	}
	remote, err := parseEndpoint(fields[2], t.words)
	if err != nil {
		return socket{}, err
	}
	state, err := parseState(fields[3], t.proto)
	if err != nil {
		return socket{}, err
	}
	inode, err := parseInode(fields[inodeField])
	if err != nil {
		return socket{}, err
	}
	return socket{proto: t.proto, local: local, remote: remote, state: state, inode: inode}, nil
}

// splitFields stores the first len(out) fields of line, separated by runs of spaces, in out and returns how many it found.
func splitFields(line []byte, out *[minFields][]byte) int {
	n := 0
	i := 0
	for n < len(out) {
		for i < len(line) && line[i] == ' ' {
			i++
		}
		if i == len(line) {
			break
		}
		start := i
		for i < len(line) && line[i] != ' ' {
			i++
		}
		out[n] = line[start:i]
		n++
	}
	return n
}

// isSlot reports whether f is the "sl" field: decimal digits and a colon.
func isSlot(f []byte) bool {
	if len(f) < 2 || f[len(f)-1] != ':' {
		return false
	}
	return allDigits(f[:len(f)-1])
}

func allDigits(b []byte) bool {
	if len(b) == 0 {
		return false
	}
	for _, c := range b {
		if c < '0' || c > '9' {
			return false
		}
	}
	return true
}

// hexValue returns the value of an upper-case hex digit.
func hexValue(c byte) (byte, bool) {
	switch {
	case c >= '0' && c <= '9':
		return c - '0', true
	case c >= 'A' && c <= 'F':
		return c - 'A' + 10, true
	}
	return 0, false
}

// parseEndpoint parses HEX:PORT with words 8-digit words in host byte order and a 4-digit port.
func parseEndpoint(f []byte, words int) (netip.AddrPort, error) {
	addrLen := words * wordDigits
	if len(f) != addrLen+1+portDigits || f[addrLen] != ':' {
		return netip.AddrPort{}, errFormat
	}
	var raw [16]byte
	for w := range words {
		var v uint32
		for _, c := range f[w*wordDigits : (w+1)*wordDigits] {
			d, ok := hexValue(c)
			if !ok {
				return netip.AddrPort{}, errFormat
			}
			v = v<<4 | uint32(d)
		}
		binary.NativeEndian.PutUint32(raw[w*4:], v)
	}
	var port uint16
	for _, c := range f[addrLen+1:] {
		d, ok := hexValue(c)
		if !ok {
			return netip.AddrPort{}, errFormat
		}
		port = port<<4 | uint16(d)
	}
	if words == 1 {
		return netip.AddrPortFrom(netip.AddrFrom4([4]byte(raw[:4])), port), nil
	}
	return netip.AddrPortFrom(netip.AddrFrom16(raw), port), nil
}

// parseState checks the two hex digits and names a TCP state; a UDP state is checked and dropped.
func parseState(f []byte, proto model.Proto) (string, error) {
	if len(f) != stateDigits {
		return "", errFormat
	}
	hi, okHi := hexValue(f[0])
	lo, okLo := hexValue(f[1])
	if !okHi || !okLo {
		return "", errFormat
	}
	if proto == model.ProtoUDP || proto == model.ProtoUDP6 {
		return "", nil
	}
	code := int(hi)*hexBase + int(lo)
	if code < 1 || code >= len(tcpStateName) {
		return "", errState
	}
	return tcpStateName[code], nil
}

// parseInode parses 1 to 20 decimal digits within 64 bits.
func parseInode(f []byte) (uint64, error) {
	if len(f) > maxInodeDigit || !allDigits(f) {
		return 0, errFormat
	}
	v, err := strconv.ParseUint(string(f), decimalBase, 64)
	if err != nil {
		return 0, errFormat
	}
	return v, nil
}

// readTable reads table t and calls visit per socket and bad per unreadable line (numbered from 1, the header being line 1);
// missing reports a table that does not exist.
func readTable(proc fs.FS, t table, visit func(socket), bad func(line int, err error)) (missing bool, err error) {
	f, err := proc.Open("net/" + t.name)
	if errors.Is(err, fs.ErrNotExist) {
		return true, nil
	}
	if err != nil {
		return false, tableError(t, err)
	}
	defer func() { _ = f.Close() }()

	r := bufio.NewReaderSize(f, readerSize)
	for n := 1; ; n++ {
		line, tooLong, rerr := readLine(r)
		if rerr != nil {
			if errors.Is(rerr, io.EOF) {
				return false, nil
			}
			return false, tableError(t, rerr)
		}
		if n == 1 {
			continue // the header
		}
		if tooLong || len(line) > maxLine {
			bad(n, errLineLong)
			continue
		}
		s, perr := parseLine(line, t)
		if perr != nil {
			bad(n, perr)
			continue
		}
		visit(s)
	}
}

// readLine returns the next line without its newline; a line longer than the reader's buffer is skipped to its end and reported
// as tooLong. The line is valid until the next call.
func readLine(r *bufio.Reader) (line []byte, tooLong bool, err error) {
	line, err = r.ReadSlice('\n')
	for errors.Is(err, bufio.ErrBufferFull) {
		tooLong = true
		_, err = r.ReadSlice('\n')
	}
	if errors.Is(err, io.EOF) && len(line) > 0 {
		err = nil // a last line without newline
	}
	if err != nil {
		return nil, false, err
	}
	if n := len(line); n > 0 && line[n-1] == '\n' {
		line = line[:n-1]
	}
	return line, tooLong, nil
}

// tableError names the table and the operating system's reason.
func tableError(t table, err error) error {
	return fmt.Errorf("connstats: net/%s: %w", t.name, cause(err))
}

// cause strips the path of a path error, which the message already names.
func cause(err error) error {
	var pe *fs.PathError
	if errors.As(err, &pe) {
		return pe.Err
	}
	return err
}

// TCPListeners calls visit for the local address of every LISTEN socket of net/tcp and net/tcp6; a missing tcp6 is skipped,
// a missing tcp or any unread line is an error.
func TCPListeners(proc fs.FS, visit func(local netip.AddrPort)) error {
	for _, t := range socketTables[:2] {
		var badErr error
		missing, err := readTable(proc, t, func(s socket) {
			if s.state == listenState {
				visit(s.local)
			}
		}, func(line int, lerr error) {
			if badErr == nil {
				badErr = fmt.Errorf("connstats: net/%s: line %d: %w", t.name, line, lerr)
			}
		})
		if err != nil {
			return err
		}
		if badErr != nil {
			return badErr
		}
		if missing && t.name == "tcp" {
			return fmt.Errorf("connstats: net/%s: %w", t.name, fs.ErrNotExist)
		}
	}
	return nil
}
