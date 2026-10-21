package mariadb

import (
	"errors"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// sample is the result of one run.
type sample struct {
	status    map[string]uint64
	variables map[string]string
	threads   []model.MariaDBThread
	ping      time.Duration
	ownID     uint32
	run       time.Time
}

// selectStatus collects the numeric status counters from the rows.
func selectStatus(rows func(func(name, value []byte, cut bool) error) error) (map[string]uint64, error) {
	_ = rows
	return nil, errors.New("not implemented")
}

// keepVariable returns the text of a variable value and whether it is kept.
func keepVariable(value []byte, cut bool) (string, bool) {
	_, _ = value, cut
	return "", false
}

// threadList collects the process list within its bounds.
type threadList struct{ own uint64 }

// newThreadList returns a threadList that leaves out the thread ownID.
func newThreadList(ownID uint64) *threadList { return &threadList{own: ownID} }

// visit reads one row cell by cell into the pools.
func (l *threadList) visit(p *packetReader, step string) error {
	_, _ = p, step
	return errors.New("not implemented")
}

// threads returns the collected threads.
func (l *threadList) threads() []model.MariaDBThread { return nil }

// estimate returns an upper bound of the encoded line of s.
func estimate(s *model.MariaDBStatus) int {
	_ = s
	return 0
}

// fitThreads reduces the threads of s until estimate is at most limit.
func fitThreads(s *model.MariaDBStatus, limit int) {
	_, _ = s, limit
}
