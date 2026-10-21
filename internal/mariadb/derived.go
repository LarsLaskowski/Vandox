package mariadb

import (
	"errors"
	"io/fs"
	"net/netip"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// memoryMax returns the memory the server may use as hi and lo parts.
func memoryMax(vars map[string]string) (hi, lo uint64, ok bool) {
	_ = vars
	return 0, 0, false
}

// rates returns the rate metrics between two samples.
func rates(prev, cur *sample) []model.MetricPoint {
	_, _ = prev, cur
	return nil
}

// bufferPoolUsed returns the used share of the buffer pool.
func bufferPoolUsed(status map[string]uint64) (float64, bool) {
	_ = status
	return 0, false
}

// exposed reports whether addr is reachable from outside the host.
func exposed(addr netip.Addr) bool {
	_ = addr
	return false
}

// countExposed counts the listening ports in ports that are exposed.
func countExposed(proc fs.FS, ports []uint16) (int, error) {
	_, _ = proc, ports
	return 0, errors.New("not implemented")
}
