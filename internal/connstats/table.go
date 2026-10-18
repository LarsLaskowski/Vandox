package connstats

import (
	"errors"
	"io/fs"
	"net/netip"

	"github.com/LarsLaskowski/Vandox/internal/model"
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

// parseLine parses one line of table t.
func parseLine(line []byte, t table) (socket, error) {
	return socket{}, errors.New("not implemented")
}

// readTable reads table t and calls visit per socket and bad per unreadable line; missing reports a table that does not exist.
func readTable(proc fs.FS, t table, visit func(socket), bad func(line int, err error)) (missing bool, err error) {
	return false, errors.New("not implemented")
}
