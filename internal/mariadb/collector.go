package mariadb

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"math"
	"os"
	"strconv"
	"sync"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// Name is the name of the collector.
const Name = "mariadb"

// Budgets and bounds of the collector.
const (
	GreetingBudget   = 2 * time.Second
	RunBudget        = 4 * time.Second
	ThreadsThreshold = 10
	MaxThreads       = 4096
	MaxInfoThreads   = 64
	MaxInfoBytes     = 16384
	RestartSlack     = 5 * time.Second
)

// Lines the collector logs and the text of the fix.
const (
	logDown          = "mariadb down"
	logNotAnswering  = "mariadb not answering"
	logUserMissing   = "mariadb user missing or not allowed"
	logLoginRefused  = "mariadb login refused"
	logAgain         = "mariadb answering again"
	logNotAsExpected = "mariadb user not as installed"
	logRestarted     = "mariadb restarted"
	logAboveRAM      = "mariadb memory maximum above RAM"
	logExposed       = "mariadb listens on a non-loopback address"
	logLoopbackOnly  = "mariadb listens on loopback only"
	loginFix         = "run deploy/agent/mariadb-user.sql as the database administrator"

	accountName   = User + "@localhost"
	privilegeName = "PROCESS"
	maxUptime     = uint64(math.MaxInt64 / int64(time.Second))
)

// Sources are the sources of the collector; zero fields take their defaults.
type Sources struct {
	Dial Dialer // default UnixDialer(socket)
	Proc fs.FS  // default os.DirFS("/proc")
}

// Collector collects the state of the database.
type Collector struct {
	src Sources
	log *slog.Logger

	mu         sync.Mutex
	avail      model.MariaDBAvailability // what the last run found; the agent starts out assuming up
	code       uint16
	prev       *sample // the previous run's sample when that run was up
	start      time.Time
	haveStart  bool // the start time of the server at the last up sample with an Uptime
	accountBad bool
	aboveRAM   bool
	exposed    int
}

// New returns a Collector for the database at socket.
func New(src Sources, socket string, log *slog.Logger) *Collector {
	if src.Dial == nil {
		src.Dial = UnixDialer(socket)
	}
	if src.Proc == nil {
		src.Proc = os.DirFS("/proc")
	}
	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}
	return &Collector{src: src, log: log.With("collector", Name), avail: model.MariaDBUp}
}

// Name returns Name.
func (c *Collector) Name() string { return Name }

// checkResult is what the account check of a run found.
type checkResult int

// Results of the account check.
const (
	checkUnknown checkResult = iota
	checkPassed
	checkFailed
)

// reading is what a run read from the server.
type reading struct {
	sample *sample
	check  checkResult
}

// Collect takes one sample.
func (c *Collector) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	rctx, cancel := context.WithDeadline(ctx, run.Start.Add(RunBudget))
	defer cancel()
	rd, err := c.read(rctx, run)
	if err != nil {
		if ctx.Err() != nil {
			return nil, err
		}
		return c.unanswered(run, err)
	}
	return c.answered(run, rd), nil
}

// read asks the server: ping, status, variables, the account check and, when due, the process list.
func (c *Collector) read(ctx context.Context, run scheduler.Run) (*reading, error) {
	cn, err := open(ctx, c.src.Dial, run.Start.Add(GreetingBudget))
	if err != nil {
		return nil, err
	}
	defer cn.close()
	rd := &reading{sample: &sample{run: run.Start, ownID: cn.id()}}
	s := rd.sample
	began := time.Now()
	if err = cn.ping(ctx); err != nil {
		return nil, err
	}
	s.ping = time.Since(began)
	if s.status, err = selectStatus(cn.rowsOf(ctx, globalStatus)); err != nil {
		return nil, err
	}
	if s.variables, err = selectVariables(cn.rowsOf(ctx, globalVariables)); err != nil {
		return nil, err
	}
	var usable bool
	if rd.check, usable, err = checkAccount(ctx, cn); err != nil {
		return nil, err
	}
	if usable && (s.status["Threads_running"] > ThreadsThreshold || run.Pressure) {
		if s.threads, err = readThreads(ctx, cn, wire.DefaultLimits().MaxLineBytes-estimate(&model.MariaDBStatus{Status: s.status, Variables: s.variables})); err != nil {
			return nil, err
		}
	}
	return rd, nil
}

// readThreads reads the process list; budget is what the threads may add to the estimate of the record.
func readThreads(ctx context.Context, cn *conn, budget int) ([]model.MariaDBThread, error) {
	l := newThreadList(uint64(cn.id()))
	if err := cn.query(ctx, fullProcesslist, func(p *packetReader) error { return l.visit(p, fullProcesslist.step()) }); err != nil {
		return nil, err
	}
	return l.threadsWithin(budget), nil
}

// checkAccount asks which account the login got and whether it holds PROCESS. An error answer of the server is no failure
// of the run (the result is unknown) but ends the use of the connection, which usable tells.
func checkAccount(ctx context.Context, cn *conn) (result checkResult, usable bool, err error) {
	user, err := askAccount(ctx, cn)
	if err != nil {
		return unusable(err)
	}
	process, err := askPrivileges(ctx, cn)
	if err != nil {
		return unusable(err)
	}
	if user && process {
		return checkPassed, true, nil
	}
	return checkFailed, true, nil
}

// unusable turns the error of an account question into the result of the check: an error answer is tolerated.
func unusable(err error) (checkResult, bool, error) {
	var se *serverError
	if errors.As(err, &se) {
		return checkUnknown, false, nil
	}
	return checkUnknown, false, err
}

// askAccount reports whether CURRENT_USER() is exactly the installed account.
func askAccount(ctx context.Context, cn *conn) (bool, error) {
	col := currentUser.columns()[0]
	rows, match := 0, false
	err := cn.query(ctx, currentUser, func(p *packetReader) error {
		c, err := p.readCell(currentUser.step(), col)
		if err != nil {
			return err
		}
		rows++
		match = !c.null && !c.cut && string(c.value) == accountName
		return nil
	})
	return err == nil && rows == 1 && match, err
}

// askPrivileges reports whether the privileges of the account include PROCESS.
func askPrivileges(ctx context.Context, cn *conn) (bool, error) {
	col := userPrivileges.columns()[0]
	found := false
	err := cn.query(ctx, userPrivileges, func(p *packetReader) error {
		c, err := p.readCell(userPrivileges.step(), col)
		if err != nil {
			return err
		}
		if !c.null && !c.cut && string(c.value) == privilegeName {
			found = true
		}
		return nil
	})
	return err == nil && found, err
}

// classify maps err to an availability and an error code; ok is false for an error that is no answer of the database.
func classify(err error) (availability model.MariaDBAvailability, code uint16, ok bool) {
	var de *dialError
	if errors.As(err, &de) {
		switch de.errno {
		case syscall.ENOENT, syscall.ECONNREFUSED:
			return model.MariaDBDown, 0, true
		case syscall.EAGAIN:
			return model.MariaDBNotAnswering, 0, true
		}
		return "", 0, false
	}
	if errors.Is(err, errTimeout) || errors.Is(err, errClosed) {
		return model.MariaDBNotAnswering, 0, true
	}
	var se *serverError
	if errors.As(err, &se) && (se.step == "greeting" || se.step == "login") {
		return model.MariaDBNotAnswering, se.code, true
	}
	return "", 0, false
}

// record returns a record of the run's start.
func record(run scheduler.Run, data model.Payload) model.Record {
	return model.Record{Meta: model.Meta{Source: Name, CapturedAt: run.Start.UTC()}, Data: data}
}

// unanswered records a run the database did not serve; any other failure fails the run.
func (c *Collector) unanswered(run scheduler.Run, err error) ([]model.Record, error) {
	avail, code, ok := classify(err)
	c.mu.Lock()
	defer c.mu.Unlock()
	c.prev = nil
	if !ok {
		return nil, err
	}
	status := &model.MariaDBStatus{Availability: avail}
	if code != 0 {
		status.ErrorCode = &code
	}
	c.noteAvailability(avail, code)
	return []model.Record{record(run, status)}, nil
}

// noteAvailability logs the change of what the runs find; the caller holds the lock.
func (c *Collector) noteAvailability(avail model.MariaDBAvailability, code uint16) {
	was := c.avail
	same := was == avail && c.code == code
	c.avail, c.code = avail, code
	switch {
	case same:
	case avail == model.MariaDBUp:
		c.log.Info(logAgain)
	case avail == model.MariaDBDown:
		c.log.Warn(logDown)
	case code == 0:
		c.log.Warn(logNotAnswering)
	case code == 1045 || code == 1698:
		c.log.Warn(logUserMissing, "error_code", strconv.Itoa(int(code)), "fix", loginFix)
	default:
		c.log.Warn(logLoginRefused, "error_code", strconv.Itoa(int(code)))
	}
}

// facts are what a run reads outside the connection.
type facts struct {
	maxMemory   u128
	haveMemory  bool
	ram         uint64
	haveRAM     bool
	exposed     int
	haveExposed bool
}

// gather reads /proc: MemTotal for the share of the maximum and the listening sockets.
func (c *Collector) gather(vars map[string]string) facts {
	var f facts
	if hi, lo, ok := memoryMax(vars); ok {
		f.maxMemory, f.haveMemory = u128{hi, lo}, true
		ram, err := sysstats.MemTotal(c.src.Proc)
		f.ram, f.haveRAM = ram, err == nil && ram > 0
	}
	if ports, ok := listenPorts(vars); ok {
		n, err := countExposed(c.src.Proc, ports)
		f.exposed, f.haveExposed = n, err == nil
	}
	return f
}

// answered records an up run.
func (c *Collector) answered(run scheduler.Run, rd *reading) []model.Record {
	s := rd.sample
	f := c.gather(s.variables)
	latency := s.ping
	status := &model.MariaDBStatus{Availability: model.MariaDBUp, PingLatency: &latency, Status: s.status, Variables: s.variables, Threads: s.threads}
	fitThreads(status, wire.DefaultLimits().MaxLineBytes)

	c.mu.Lock()
	defer c.mu.Unlock()
	c.noteAvailability(model.MariaDBUp, 0)
	c.noteAccount(rd.check)
	points := c.noteRestart(run, s)
	c.noteMemory(f)
	c.noteExposure(f)
	points = append(points, c.metrics(s, f)...)
	c.prev = s

	out := make([]model.Record, 0, 1+len(points))
	out = append(out, record(run, status))
	for i := range points {
		out = append(out, record(run, &points[i]))
	}
	return out
}

// metrics returns the metrics of an up sample that do not depend on the previous one.
func (c *Collector) metrics(s *sample, f facts) []model.MetricPoint {
	var out []model.MetricPoint
	if f.haveMemory {
		out = append(out, model.MetricPoint{Name: metricMemoryMaxBytes, Value: f.maxMemory.float(), Unit: unitBytes})
		if f.haveRAM {
			out = append(out, model.MetricPoint{Name: metricMemoryMaxPercent, Value: 100 * f.maxMemory.float() / float64(f.ram), Unit: unitPercent})
		}
	}
	if used, ok := bufferPoolUsed(s.status); ok {
		out = append(out, model.MetricPoint{Name: metricPoolUsedPercent, Value: used, Unit: unitPercent})
	}
	if f.haveExposed {
		out = append(out, model.MetricPoint{Name: metricExposedListeners, Value: float64(f.exposed)})
	}
	return out
}

// noteAccount logs the account check when it newly fails; an unknown result changes nothing.
func (c *Collector) noteAccount(result checkResult) {
	switch {
	case result == checkPassed:
		c.accountBad = false
	case result == checkFailed && !c.accountBad:
		c.accountBad = true
		c.log.Warn(logNotAsExpected, "fix", loginFix)
	case result == checkFailed:
	}
}

// serverStart returns when the server started: the run's start less its uptime.
func serverStart(s *sample) (time.Time, bool) {
	uptime, ok := s.status["Uptime"]
	if !ok || uptime > maxUptime {
		return time.Time{}, false
	}
	return s.run.Add(-time.Duration(uptime) * time.Second), true
}

// noteRestart compares the start of the server with the one of the last up sample, logs a restart and returns the rates between
// the samples when they are of one server lifetime.
func (c *Collector) noteRestart(run scheduler.Run, s *sample) []model.MetricPoint {
	start, ok := serverStart(s)
	prev := c.prev
	if run.PreviousLost {
		prev = nil
	}
	restarted := ok && c.haveStart && start.Sub(c.start) > RestartSlack
	if ok {
		c.start, c.haveStart = start, true
	}
	if restarted {
		c.log.Warn(logRestarted, "uptime_seconds", strconv.FormatUint(s.status["Uptime"], 10))
	}
	if prev == nil || !ok || restarted {
		return nil
	}
	return rates(prev, s)
}

// noteMemory logs the transitions of the maximum above the memory of the server.
func (c *Collector) noteMemory(f facts) {
	if !f.haveMemory || !f.haveRAM {
		return
	}
	above := f.maxMemory.hi != 0 || f.maxMemory.lo > f.ram
	if above && !c.aboveRAM {
		c.log.Warn(logAboveRAM, "max_bytes", f.maxMemory.String(), "ram_bytes", strconv.FormatUint(f.ram, 10))
	}
	c.aboveRAM = above
}

// noteExposure logs the transitions of the number of exposed listeners.
func (c *Collector) noteExposure(f facts) {
	if !f.haveExposed {
		return
	}
	switch {
	case f.exposed > 0 && c.exposed == 0:
		c.log.Warn(logExposed, "count", strconv.Itoa(f.exposed))
	case f.exposed == 0 && c.exposed > 0:
		c.log.Info(logLoopbackOnly)
	}
	c.exposed = f.exposed
}
