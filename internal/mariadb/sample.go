package mariadb

import (
	"bytes"
	"cmp"
	"container/heap"
	"context"
	"slices"
	"strconv"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
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

// Names of the status values and variables the collector keeps (S1); every other name the server sends is dropped.
var (
	statusNames = [...]string{
		"Uptime", "Threads_connected", "Threads_running", "Max_used_connections", "Aborted_connects", "Aborted_clients", "Questions",
		"Slow_queries", "Created_tmp_disk_tables", "Innodb_buffer_pool_pages_total", "Innodb_buffer_pool_pages_free",
		"Innodb_buffer_pool_read_requests", "Innodb_buffer_pool_reads", "Memory_used",
	}
	variableNames = [...]string{
		"version", "max_connections", "innodb_buffer_pool_size", "innodb_log_buffer_size", "key_buffer_size", "aria_pagecache_buffer_size",
		"query_cache_size", "tmp_table_size", "max_heap_table_size", "sort_buffer_size", "read_buffer_size", "read_rnd_buffer_size",
		"join_buffer_size", "thread_stack", "binlog_cache_size", "log_bin", "bind_address", "skip_networking", "port", "extra_port",
	}
	statusSlots   = slotsOf(statusNames[:])
	variableSlots = slotsOf(variableNames[:])
)

// Bounds of a kept value and reasons that belong to the selection and the process list.
const (
	maxVariableBytes = 1024
	maxShortBytes    = 1024
	maxDigits        = 20
	reasonNameTwice  = "name sent twice"

	// Estimate of the encoded line (record 0118): the longest JSON escape per byte of text, 20 digits and a sign for a number.
	escapeBytes    = 6
	numberBytes    = 21
	entryBytes     = 8
	threadBytes    = 256
	recordOverhead = 1024
)

// Capacities of the short texts of a thread (spec E8) and the positions of the cells of a process list row.
const (
	userCap    = 512
	hostCap    = 512
	dbCap      = 256
	commandCap = 32
	stateCap   = 256
	slotBytes  = userCap + hostCap + dbCap + commandCap + stateCap
	infoBuffer = MaxInfoBytes

	cellID       = 0
	cellUser     = 1
	cellHost     = 2
	cellDB       = 3
	cellCommand  = 4
	cellTime     = 5
	cellState    = 6
	cellInfo     = 7
	cellsPerRow  = 9
	sleepCommand = "Sleep"
)

// slotsOf returns the position of every name.
func slotsOf(names []string) map[string]int {
	out := make(map[string]int, len(names))
	for i, n := range names {
		out[n] = i
	}
	return out
}

// parseDigits reads a number of 1 to 20 ASCII digits within uint64.
func parseDigits(b []byte) (uint64, bool) {
	if len(b) == 0 || len(b) > maxDigits || bytes.ContainsFunc(b, func(r rune) bool { return r < '0' || r > '9' }) {
		return 0, false
	}
	n, err := strconv.ParseUint(string(b), 10, 64)
	return n, err == nil
}

// pick calls keep for the value of every name of slots once; a selected name met twice is an error. Other names are neither
// kept nor remembered.
func pick(step string, slots map[string]int, rows func(func(name, value []byte, cut bool) error) error, keep func(slot int, value []byte, cut bool)) error {
	seen := make([]bool, len(slots))
	return rows(func(name, value []byte, cut bool) error {
		slot, ok := slots[string(name)]
		switch {
		case !ok:
			return nil
		case seen[slot]:
			return &protocolError{step: step, reason: reasonNameTwice}
		}
		seen[slot] = true
		keep(slot, value, cut)
		return nil
	})
}

// selectStatus collects the numeric status counters from the rows.
func selectStatus(rows func(func(name, value []byte, cut bool) error) error) (map[string]uint64, error) {
	out := make(map[string]uint64, len(statusNames))
	err := pick(globalStatus.step(), statusSlots, rows, func(slot int, value []byte, cut bool) {
		if n, ok := parseDigits(value); ok && !cut {
			out[statusNames[slot]] = n
		}
	})
	if err != nil {
		return nil, err
	}
	return out, nil
}

// selectVariables collects the variables of the selection from the rows.
func selectVariables(rows func(func(name, value []byte, cut bool) error) error) (map[string]string, error) {
	out := make(map[string]string, len(variableNames))
	err := pick(globalVariables.step(), variableSlots, rows, func(slot int, value []byte, cut bool) {
		if text, ok := keepVariable(value, cut); ok {
			out[variableNames[slot]] = text
		}
	})
	if err != nil {
		return nil, err
	}
	return out, nil
}

// keepVariable returns the text of a variable value and whether it is kept: not cut and at most 1,024 bytes once cleaned.
func keepVariable(value []byte, cut bool) (string, bool) {
	if cut {
		return "", false
	}
	text, over := procstats.CleanText(string(value), maxVariableBytes)
	if over {
		return "", false
	}
	return text, true
}

// rowsOf returns the rows of a two-column statement as the selection reads them.
func (c *conn) rowsOf(ctx context.Context, s statement) func(func(name, value []byte, cut bool) error) error {
	cols, step := s.columns(), s.step()
	return func(yield func(name, value []byte, cut bool) error) error {
		return c.query(ctx, s, func(p *packetReader) error {
			name, err := p.readCell(step, cols[0])
			if err != nil {
				return err
			}
			value, err := p.readCell(step, cols[1])
			if err != nil {
				return err
			}
			return yield(name.value, value.value, name.cut || value.cut)
		})
	}
}

// threadKey is the place of a thread in the order of the snapshot: working threads first, then the longest running, then the
// lowest id.
type threadKey struct {
	id, seconds uint64
	sleeping    bool
}

// before reports whether k comes before o.
func (k threadKey) before(o threadKey) bool {
	switch {
	case k.sleeping != o.sleeping:
		return !k.sleeping
	case k.seconds != o.seconds:
		return k.seconds > o.seconds
	}
	return k.id < o.id
}

// compareKeys orders keys by before.
func compareKeys(a, b threadKey) int {
	switch {
	case a.before(b):
		return -1
	case b.before(a):
		return 1
	}
	return cmp.Compare(a.id, b.id)
}

// threadSlot is the place for one kept thread: its short texts in one array and, for the first threads, a buffer for the statement.
type threadSlot struct {
	key          threadKey
	lens         [5]int
	text         [slotBytes]byte
	info         []byte
	hasStatement bool
	infoCut      bool
}

// shortFields are the capacities of the short texts in the order of threadSlot.text.
var shortFields = [5]int{userCap, hostCap, dbCap, commandCap, stateCap}

// shortCells are the cells of the short texts in the same order.
var shortCells = [5]int{cellUser, cellHost, cellDB, cellCommand, cellState}

// fill copies the short texts of a row into the slot.
func (s *threadSlot) fill(key threadKey, cells *[cellsPerRow]cell) {
	*s = threadSlot{key: key}
	off := 0
	for i, size := range shortFields {
		s.lens[i] = copy(s.text[off:off+size], cells[shortCells[i]].value)
		off += size
	}
}

// field returns the short text i, cleaned.
func (s *threadSlot) field(i int) string {
	off := 0
	for _, size := range shortFields[:i] {
		off += size
	}
	text, _ := cleanBytes(s.text[off:off+s.lens[i]], maxShortBytes)
	return text
}

// cleanBytes makes b valid UTF-8 and cuts it to limit bytes.
func cleanBytes(b []byte, limit int) (string, bool) {
	if len(b) == 0 {
		return "", false
	}
	return procstats.CleanText(string(b), limit)
}

// thread returns the thread of the slot with its texts cleaned.
func (s *threadSlot) thread() model.MariaDBThread {
	info, cut := cleanBytes(s.info, MaxInfoBytes)
	return model.MariaDBThread{
		ID: s.key.id, User: s.field(0), Host: s.field(1), DB: s.field(2), Command: s.field(3), TimeSeconds: s.key.seconds,
		State: s.field(4), Info: info, Truncated: s.infoCut || cut || (s.hasStatement && s.info == nil),
	}
}

// slotHeap is a heap of slots with the one that comes last at the root.
type slotHeap []*threadSlot

// Len returns the number of slots.
func (h slotHeap) Len() int { return len(h) }

// Less reports whether slot i comes after slot j.
func (h slotHeap) Less(i, j int) bool { return h[j].key.before(h[i].key) }

// Swap swaps two slots.
func (h slotHeap) Swap(i, j int) { h[i], h[j] = h[j], h[i] }

// Push adds a slot.
func (h *slotHeap) Push(x any) {
	if s, ok := x.(*threadSlot); ok {
		*h = append(*h, s)
	}
}

// Pop removes the last slot.
func (h *slotHeap) Pop() any {
	old := *h
	last := old[len(old)-1]
	*h = old[:len(old)-1]
	return last
}

// threadList collects the process list within its bounds: at most MaxThreads threads, the statement of only the first
// MaxInfoThreads, and no allocation per row once the slots exist.
type threadList struct {
	own    uint64
	cols   []column
	kept   slotHeap
	top    slotHeap
	spares [][]byte
}

// newThreadList returns a threadList that leaves out the thread ownID.
func newThreadList(ownID uint64) *threadList {
	return &threadList{own: ownID, cols: fullProcesslist.columns()}
}

// visit reads one row cell by cell into the pools.
func (l *threadList) visit(p *packetReader, step string) error {
	var cells [cellsPerRow]cell
	for i, col := range l.cols {
		c, err := p.readCell(step, col)
		if err != nil {
			return err
		}
		cells[i] = c
	}
	id, ok := parseDigits(cells[cellID].value)
	if cells[cellID].null || !ok {
		return &protocolError{step: step, reason: reasonUnexpected}
	}
	if id == l.own {
		return nil
	}
	seconds, _ := parseDigits(cells[cellTime].value)
	key := threadKey{id: id, seconds: seconds, sleeping: string(cells[cellCommand].value) == sleepCommand}
	slot := l.admit(key)
	if slot == nil {
		return nil
	}
	slot.fill(key, &cells)
	l.giveStatement(slot, cells[cellInfo])
	return nil
}

// admit returns the slot for a thread with key, or nil when the thread ranks after the first MaxThreads.
func (l *threadList) admit(key threadKey) *threadSlot {
	if len(l.kept) < MaxThreads {
		s := &threadSlot{key: key}
		heap.Push(&l.kept, s)
		return s
	}
	worst := l.kept[0]
	if !key.before(worst.key) {
		return nil
	}
	worst.key = key
	heap.Fix(&l.kept, 0)
	return worst
}

// giveStatement keeps the statement of the slot when it ranks among the first MaxInfoThreads; the thread that falls out of them
// loses its statement.
func (l *threadList) giveStatement(slot *threadSlot, c cell) {
	slot.hasStatement = !c.null && len(c.value) > 0
	slot.infoCut = c.cut
	var buf []byte
	switch {
	case len(l.top) < MaxInfoThreads:
		heap.Push(&l.top, slot)
	case slot.key.before(l.top[0].key):
		buf, l.top[0].info = l.top[0].info, nil
		l.top[0] = slot
		heap.Fix(&l.top, 0)
	default:
		return
	}
	switch {
	case !slot.hasStatement:
		if buf != nil {
			l.spares = append(l.spares, buf)
		}
		return
	case buf == nil && len(l.spares) > 0:
		buf, l.spares = l.spares[len(l.spares)-1], l.spares[:len(l.spares)-1]
	case buf == nil:
		buf = make([]byte, 0, infoBuffer)
	}
	buf = append(buf[:0], c.value...)
	slot.info = buf
}

// ordered returns the kept slots in the order of the snapshot.
func (l *threadList) ordered() []*threadSlot {
	order := slices.Clone(l.kept)
	slices.SortFunc(order, func(a, b *threadSlot) int { return compareKeys(a.key, b.key) })
	return order
}

// minimumBytes is the least the thread adds to the estimate of the record once its statement is dropped: the cleaned texts are
// never shorter than the bytes read.
func (s *threadSlot) minimumBytes() int {
	n := 0
	for _, size := range s.lens {
		n += size
	}
	return threadBytes + 2*numberBytes + escapeBytes*n
}

// build returns the threads of the slots with their texts cleaned.
func build(order []*threadSlot) []model.MariaDBThread {
	if len(order) == 0 {
		return nil
	}
	out := make([]model.MariaDBThread, len(order))
	for i, s := range order {
		out[i] = s.thread()
	}
	return out
}

// threads returns the collected threads in the order of the snapshot, with their texts cleaned.
func (l *threadList) threads() []model.MariaDBThread {
	return build(l.ordered())
}

// threadsWithin returns the threads in the order of the snapshot that can still be part of a record whose threads may add budget
// bytes to the estimate: the first threads whose least sizes, without any statement, fit together. The others would be left
// out by fitThreads anyway, so their texts are not cleaned at all.
func (l *threadList) threadsWithin(budget int) []model.MariaDBThread {
	order := l.ordered()
	used, n := 0, 0
	for _, s := range order {
		used += s.minimumBytes()
		if used > budget {
			break
		}
		n++
	}
	return build(order[:n])
}

// estimate returns an upper bound of the encoded line of s.
func estimate(s *model.MariaDBStatus) int {
	n := recordOverhead
	for name := range s.Status {
		n += escapeBytes*len(name) + numberBytes + entryBytes
	}
	for name, value := range s.Variables {
		n += escapeBytes*(len(name)+len(value)) + entryBytes
	}
	for i := range s.Threads {
		t := &s.Threads[i]
		n += threadBytes + 2*numberBytes + escapeBytes*(len(t.User)+len(t.Host)+len(t.DB)+len(t.Command)+len(t.State)+len(t.Info))
	}
	return n
}

// fitThreads reduces the threads of s until estimate is at most limit: the statements to 4,096, 1,024 and 0 bytes, then the
// thread list halved from its end.
func fitThreads(s *model.MariaDBStatus, limit int) {
	if estimate(s) <= limit {
		return
	}
	for _, size := range [...]int{4096, 1024, 0} {
		cutStatements(s.Threads, size)
		if estimate(s) <= limit {
			return
		}
	}
	for len(s.Threads) > 0 && estimate(s) > limit {
		s.Threads = s.Threads[:len(s.Threads)/2]
	}
	if len(s.Threads) == 0 {
		s.Threads = nil
	}
}

// cutStatements cuts every statement of threads to at most size bytes at a character boundary and flags the thread.
func cutStatements(threads []model.MariaDBThread, size int) {
	for i := range threads {
		t := &threads[i]
		if len(t.Info) <= size {
			continue
		}
		end := size
		for end > 0 && !utf8.RuneStart(t.Info[end]) {
			end--
		}
		t.Info, t.Truncated = t.Info[:end], true
	}
}
