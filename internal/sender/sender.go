// Package sender sends the spooled records to the backend and acknowledges them once they are accepted.
package sender

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"math/rand/v2"
	"sort"
	"sync"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/spool"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// The fixed texts of the sender's log lines.
const (
	logNotAccepting = "backend not accepting batches"
	logAccepting    = "backend accepting batches again"
	logBfStarted    = "backfill started"
	logBfFinished   = "backfill finished"
	logBfPaused     = "backfill paused"
	logBfResumed    = "backfill resumed"
	logRefused      = "record refused"
	logAckNotKept   = "acknowledgement not kept"
	logNotReadable  = "spool not readable"

	// keyReason and keyStatus are the attribute keys the log lines share.
	keyReason = "reason"
	keyStatus = "status"
	// reasonRefused is the reason of the wait for a free give-up slot.
	reasonRefused = "refused"
	// giveUpWindow is the span the give-up bound counts in.
	giveUpWindow = time.Hour
	// gapSpan is the length of the gap that stands for a given-up record.
	gapSpan = time.Second
)

// Limits and timings of the sender.
const (
	// IngestPath is the path of the ingest endpoint.
	IngestPath = "/v1/batches"
	// SendInterval is the interval between two sends.
	SendInterval = 4 * time.Second
	// RequestTimeout is the time one request may take.
	RequestTimeout = 30 * time.Second
	// FirstWait is the first wait after a failure.
	FirstWait = time.Second
	// MaxWait is the longest wait after failures.
	MaxWait = 60 * time.Second
	// MaxBatchBytes is the largest size of a batch.
	MaxBatchBytes = 1 << 20
	// MaxBatchRecords is the largest number of records of a batch.
	MaxBatchRecords = 20000
	// MaxHoles is the largest number of holes the sender keeps.
	MaxHoles = 32
	// MaxGivenUpPerHour is the largest number of records given up in an hour.
	MaxGivenUpPerHour = 10
	// MaxAnswerBody is the largest number of bytes read from an answer's body.
	MaxAnswerBody = 4096
	// MaxAnswerHeader is the largest size of an answer's header.
	MaxAnswerHeader = 16 << 10
	// RefusedHeader with the value RefusedValue marks a refusal of the batch itself.
	RefusedHeader = "Vandox-Refused"
	// RefusedValue is the value of RefusedHeader that marks a refusal of the batch itself.
	RefusedValue = "batch"
)

// Spool is what the sender needs of the spool.
type Spool interface {
	// Read returns the records after the sequence number after.
	Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (spool.Batch, error)
	// Ack acknowledges the sequence numbers first to last.
	Ack(ctx context.Context, first, last uint64) error
	// Put appends records.
	Put(ctx context.Context, records []model.Record) error
	// LastSeq returns the sequence number of the newest record.
	LastSeq() uint64
	// AckRanges returns the number of acknowledged ranges above the oldest kept number.
	AckRanges() int
}

// Options configures a Sender.
type Options struct {
	// Spool is the spool to send from.
	Spool Spool
	// Transport sends the batches.
	Transport Transport
	// Bandwidth is the bytes per second of backfill request bodies.
	Bandwidth int64
	// Rand returns a number in [0, 1); nil: math/rand/v2.Float64.
	Rand func() float64
}

// outcome is how the delivery of records ended.
type outcome int

const (
	// outDone: every record was acknowledged or given up.
	outDone outcome = iota
	// outFailed: the backend did not accept the batch; the episode is running.
	outFailed
	// outRetry: the spool did not keep an acknowledgement or a gap; the stream waits and sends again.
	outRetry
	// outParked: the give-up bound is exhausted; the stream waits for a free slot.
	outParked
	// outGate: the backfill may not send now (episode, memory pressure).
	outGate
	// outStop: the context ended.
	outStop
)

// stream is the state of one sending goroutine.
type stream struct {
	mode wire.Mode
	// pos is the number after which the stream reads: every record up to it is acknowledged or given up.
	pos uint64
	// mark is the newest number at the start of the live stream's previous attempt.
	mark uint64
	buf  bytes.Buffer
	// wait is the next value of the stream's own backoff schedule; 0 means FirstWait.
	wait        time.Duration
	readFailing bool
	// parkUntil is when a parked stream tries again.
	parkUntil time.Time
	// sent is the number of records the backfill pass sent.
	sent int
}

// episode is the period in which the backend does not accept batches, shared by both streams.
type episode struct {
	active bool
	reason string
	status int
	start  time.Time
	// wait is the next value of the episode's backoff schedule.
	wait time.Duration
}

// batchCtx is the capture context of the records of one batch.
type batchCtx struct {
	agentID string
	bootID  string
	offset  *time.Duration
	// first is the first sequence number of the whole batch.
	first uint64
}

// Sender sends the spooled records to the backend.
type Sender struct {
	opts Options
	log  *slog.Logger
	rand func() float64
	thr  *throttle

	live stream
	back stream

	// giveMu serializes the give-ups, so the hourly bound holds for both streams.
	giveMu  sync.Mutex
	givenUp []time.Time
	// gapped holds the numbers whose gap the spool already has but whose acknowledgement is still to be kept.
	gapped map[uint64]bool

	mu sync.Mutex
	// changed is closed and replaced whenever something a stream waits for changes.
	changed       chan struct{}
	ep            episode
	pressure      bool
	started       bool
	liveStart     uint64
	releaseGen    uint64
	refusedWarned bool
	ackWarned     bool
	runStart      time.Time
	lagFrom       time.Time
	haveLag       bool
}

// New returns a Sender.
func New(opts Options, log *slog.Logger) *Sender {
	if log == nil {
		log = slog.New(slog.DiscardHandler)
	}
	draw := opts.Rand
	if draw == nil {
		draw = rand.Float64
	}

	return &Sender{
		opts:    opts,
		log:     log,
		rand:    draw,
		thr:     newThrottle(opts.Bandwidth),
		live:    stream{mode: wire.ModeLive},
		back:    stream{mode: wire.ModeBackfill},
		changed: make(chan struct{}),
		gapped:  map[uint64]bool{},
	}
}

// Run sends until ctx is done.
func (s *Sender) Run(ctx context.Context) {
	s.mu.Lock()
	s.runStart = time.Now()
	s.mu.Unlock()
	s.live.pos = s.opts.Spool.LastSeq()
	if s.opts.Spool.AckRanges() >= MaxHoles {
		s.live.pos = 0
	}
	var wg sync.WaitGroup
	wg.Go(func() { s.runLive(ctx) })
	wg.Go(func() { s.runBackfill(ctx) })
	wg.Wait()
}

// SetPressure tells the sender whether the system is under memory pressure.
func (s *Sender) SetPressure(high bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.pressure != high {
		s.pressure = high
		s.broadcastLocked()
	}
}

// SendLag returns the time since the capture of the newest record acknowledged since Run, and before the first
// acknowledgement the time since Run began; it is never negative.
func (s *Sender) SendLag(_ context.Context) (time.Duration, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	from := s.runStart
	if s.haveLag {
		from = s.lagFrom
	}
	if from.IsZero() {
		return 0, nil
	}

	return max(time.Since(from), 0), nil
}

// broadcastLocked wakes everything that waits for a change; s.mu is held.
func (s *Sender) broadcastLocked() {
	close(s.changed)
	s.changed = make(chan struct{})
}

// pause sleeps for d and reports false when ctx ended first.
func pause(ctx context.Context, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-timer.C:
		return true
	case <-ctx.Done():
		return false
	}
}

// jitter draws a wait uniformly from [d/2, d].
func (s *Sender) jitter(d time.Duration) time.Duration {
	r := min(max(s.rand(), 0), 1)

	return time.Duration(float64(d) * (0.5 + 0.5*r))
}

// backoff waits one value of the stream's own schedule (1 s doubling to 60 s) and reports false when ctx ended.
func (s *Sender) backoff(ctx context.Context, st *stream) bool {
	d := st.wait
	if d <= 0 {
		d = FirstWait
	}
	st.wait = min(d*2, MaxWait)

	return pause(ctx, s.jitter(d))
}

// ---------------------------------------------------------------------------------------------------------------
// Episode

// episodeActive reports whether the backend is not accepting batches.
func (s *Sender) episodeActive() bool {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.ep.active
}

// open reports whether the backfill may send: no episode and no memory pressure.
func (s *Sender) open() bool {
	s.mu.Lock()
	defer s.mu.Unlock()

	return !s.ep.active && !s.pressure
}

// fail records a failed attempt: it starts the episode or, when the reason or the status changed, logs again.
func (s *Sender) fail(res Result) {
	s.mu.Lock()
	defer s.mu.Unlock()
	switch {
	case !s.ep.active:
		s.ep = episode{active: true, reason: res.Reason, status: res.Status, start: time.Now(), wait: FirstWait}
		s.warnNotAccepting(res.Reason, res.Status)
		s.broadcastLocked()
	case s.ep.reason != res.Reason || s.ep.status != res.Status:
		s.ep.reason, s.ep.status = res.Reason, res.Status
		s.warnNotAccepting(res.Reason, res.Status)
	}
}

// warnNotAccepting logs the warning of an episode.
func (s *Sender) warnNotAccepting(reason string, status int) {
	if status > 0 {
		s.log.Warn(logNotAccepting, keyReason, reason, keyStatus, status)

		return
	}
	s.log.Warn(logNotAccepting, keyReason, reason)
}

// episodeWait returns the next value of the episode's schedule.
func (s *Sender) episodeWait() time.Duration {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.ep.wait
}

// episodeDouble doubles the episode's schedule after a failed attempt.
func (s *Sender) episodeDouble() {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.ep.active {
		s.ep.wait = min(s.ep.wait*2, MaxWait)
	}
}

// ---------------------------------------------------------------------------------------------------------------
// Live stream

// runLive sends the records handed to the spool every SendInterval, and makes the attempts of an episode.
func (s *Sender) runLive(ctx context.Context) {
	tick := time.NewTicker(SendInterval)
	defer tick.Stop()
	for ctx.Err() == nil {
		if s.episodeActive() {
			s.liveAttempt(ctx)

			continue
		}
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
		s.liveRound(ctx)
	}
}

// liveRound sends batch after batch until a read returns nothing.
func (s *Sender) liveRound(ctx context.Context) {
	st := &s.live
	for ctx.Err() == nil && !s.episodeActive() {
		st.mark = s.opts.Spool.LastSeq()
		recs, bc, ok := s.read(ctx, st, st.pos)
		if !ok {
			if !s.backoff(ctx, st) {
				return
			}

			continue
		}
		if len(recs) == 0 {
			return
		}
		if !s.finish(ctx, st, s.deliver(ctx, st, bc, recs)) {
			return
		}
	}
}

// liveAttempt waits one wait of the episode and then makes one attempt: a live batch of the records handed over since
// the previous attempt started, unless the spool already holds the most holes the sender keeps.
func (s *Sender) liveAttempt(ctx context.Context) {
	st := &s.live
	if !pause(ctx, s.jitter(s.episodeWait())) || !s.episodeActive() {
		return
	}
	after := st.pos
	if s.opts.Spool.AckRanges() < MaxHoles {
		after = max(after, st.mark)
	}
	mark := s.opts.Spool.LastSeq()
	recs, bc, ok := s.read(ctx, st, after)
	if !ok || len(recs) == 0 {
		return
	}
	st.mark = mark
	switch out := s.deliver(ctx, st, bc, recs); out {
	case outFailed:
		s.episodeDouble()
	case outParked:
		s.park(ctx, st)
	case outDone, outRetry, outGate, outStop:
	}
}

// finish handles the outcome of a delivery in a stream's loop; it reports false when the loop is to end.
func (s *Sender) finish(ctx context.Context, st *stream, out outcome) bool {
	switch out {
	case outRetry:
		return s.backoff(ctx, st)
	case outParked:
		return s.park(ctx, st)
	case outStop:
		return false
	case outDone, outFailed, outGate:
	}

	return ctx.Err() == nil
}

// read reads the next batch after the number after. It reports false when the read failed, after logging it once
// per failure episode of the stream.
func (s *Sender) read(ctx context.Context, st *stream, after uint64) ([]model.Record, *batchCtx, bool) {
	b, err := s.opts.Spool.Read(ctx, after, MaxBatchRecords, MaxBatchBytes)
	if err != nil {
		if ctx.Err() == nil && !errors.Is(err, spool.ErrClosed) && !st.readFailing {
			s.log.Warn(logNotReadable, "err", err.Error())
		}
		st.readFailing = true

		return nil, nil, false
	}
	st.readFailing = false
	bc := &batchCtx{agentID: b.AgentID, bootID: b.BootID}
	if b.ClockOffsetNS != nil {
		d := time.Duration(*b.ClockOffsetNS)
		bc.offset = &d
	}
	if len(b.Records) > 0 {
		bc.first = b.Records[0].Seq
	}

	return b.Records, bc, true
}

// ---------------------------------------------------------------------------------------------------------------
// Backfill stream

// runBackfill sends the backlog in passes, one for each release by the live stream.
func (s *Sender) runBackfill(ctx context.Context) {
	var gen uint64
	for {
		if !s.awaitRelease(ctx, gen) {
			return
		}
		var ok bool
		if gen, ok = s.pass(ctx); !ok {
			return
		}
	}
}

// awaitRelease blocks until a live acknowledgement released the backfill after generation gen and no episode runs.
func (s *Sender) awaitRelease(ctx context.Context, gen uint64) bool {
	for {
		s.mu.Lock()
		ready := s.releaseGen > gen && !s.ep.active
		changed := s.changed
		s.mu.Unlock()
		if ready {
			return true
		}
		select {
		case <-ctx.Done():
			return false
		case <-changed:
		}
	}
}

// waitOpen blocks while an episode runs or the memory pressure mode is on, and logs the pause and the resume of a
// pass whose cut holds records. It reports false when ctx ended.
func (s *Sender) waitOpen(ctx context.Context, paused *bool) bool {
	for {
		s.mu.Lock()
		blocked, pressure, changed := s.ep.active, s.pressure, s.changed
		s.mu.Unlock()
		if !blocked && !pressure {
			if *paused {
				s.log.Info(logBfResumed)
				*paused = false
			}

			return true
		}
		if pressure && !*paused {
			s.log.Info(logBfPaused, keyReason, "memory_pressure")
			*paused = true
		}
		select {
		case <-ctx.Done():
			return false
		case <-changed:
		}
	}
}

// liveBound returns the number from which the live stream sends and the generation of the release.
func (s *Sender) liveBound() (uint64, uint64) {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.liveStart, s.releaseGen
}

// cutBelow returns the records below limit.
func cutBelow(recs []model.Record, limit uint64) []model.Record {
	return recs[:sort.Search(len(recs), func(i int) bool { return recs[i].Seq >= limit })]
}

// passState is what a backfill pass remembers between its steps.
type passState struct {
	started bool
	paused  bool
}

// pass sends every unacknowledged record below the live stream's start, oldest first. It returns the generation of
// the release it covered, and false when ctx ended.
func (s *Sender) pass(ctx context.Context) (uint64, bool) {
	s.back.sent = 0
	var ps passState
	for {
		gen, finished, ok := s.passStep(ctx, &ps)
		if !ok {
			return 0, false
		}
		if finished {
			return gen, true
		}
	}
}

// passStep reads and sends one batch of a pass. It reports whether the pass is finished, and false when ctx ended.
func (s *Sender) passStep(ctx context.Context, ps *passState) (uint64, bool, bool) {
	st := &s.back
	limit, gen := s.liveBound()
	recs, bc, ok := s.read(ctx, st, st.pos)
	if !ok {
		return 0, false, s.backoff(ctx, st)
	}
	recs = cutBelow(recs, limit)
	if len(recs) == 0 {
		if ps.started {
			s.log.Info(logBfFinished, "records", st.sent)
		}

		return gen, true, true
	}
	if !s.open() {
		// The pause is logged for a pass whose cut holds records; they are read again once the backfill may go on.
		return 0, false, s.waitOpen(ctx, &ps.paused)
	}
	if !ps.started {
		ps.started = true
		s.log.Info(logBfStarted)
	}

	return 0, false, s.finish(ctx, st, s.deliver(ctx, st, bc, recs))
}

// ---------------------------------------------------------------------------------------------------------------
// Delivery

// deliver sends the records of one capture context and acknowledges them; a refused batch is split in halves.
func (s *Sender) deliver(ctx context.Context, st *stream, bc *batchCtx, recs []model.Record) outcome {
	body, err := s.encode(st, bc, recs)
	if err != nil {
		return s.split(ctx, st, bc, recs, 0)
	}
	if st.mode == wire.ModeBackfill {
		if s.thr.wait(ctx, len(body)) != nil {
			return outStop
		}
		if !s.open() {
			return outGate
		}
	}
	res := s.send(ctx, body)
	switch res.Outcome() {
	case Acknowledged:
		return s.acknowledge(ctx, st, bc, recs)
	case Refused:
		return s.split(ctx, st, bc, recs, res.Status)
	case Stopped:
		return outStop
	case NotAccepting:
	}
	s.fail(res)

	return outFailed
}

// encode writes the batch of recs into the stream's buffer.
func (s *Sender) encode(st *stream, bc *batchCtx, recs []model.Record) ([]byte, error) {
	st.buf.Reset()
	h := wire.NewHeader(bc.agentID, bc.bootID, st.mode)
	h.ClockOffset = bc.offset
	if err := wire.EncodeBatch(&st.buf, &wire.Batch{Header: h, Records: recs}); err != nil {
		return nil, err
	}

	return st.buf.Bytes(), nil
}

// send makes one request that ends after RequestTimeout.
func (s *Sender) send(ctx context.Context, body []byte) Result {
	reqCtx, cancel := context.WithTimeout(ctx, RequestTimeout)
	defer cancel()

	return s.opts.Transport.Send(reqCtx, body)
}

// split sends the halves of a refused batch in order; a refused single record is given up.
func (s *Sender) split(ctx context.Context, st *stream, bc *batchCtx, recs []model.Record, status int) outcome {
	if len(recs) == 1 {
		return s.giveUp(ctx, st, &recs[0], status)
	}
	mid := len(recs) / 2
	if out := s.deliver(ctx, st, bc, recs[:mid]); out != outDone {
		return out
	}

	return s.deliver(ctx, st, bc, recs[mid:])
}

// acknowledge marks the records acknowledged in the spool and moves the stream on.
func (s *Sender) acknowledge(ctx context.Context, st *stream, bc *batchCtx, recs []model.Record) outcome {
	if err := s.opts.Spool.Ack(ctx, recs[0].Seq, recs[len(recs)-1].Seq); err != nil {
		return s.ackFailed(ctx, err)
	}
	st.pos = recs[len(recs)-1].Seq
	st.wait = 0
	var newest time.Time
	for i := range recs {
		if recs[i].CapturedAt.After(newest) {
			newest = recs[i].CapturedAt
		}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ackWarned = false
	if !s.haveLag || newest.After(s.lagFrom) {
		s.lagFrom, s.haveLag = newest, true
	}
	if st.mode == wire.ModeBackfill {
		st.sent += len(recs)

		return outDone
	}
	if s.ep.active || !s.started {
		s.release(bc.first)
	}

	return outDone
}

// release ends the episode and the wait for the first acknowledgement; s.mu is held.
func (s *Sender) release(first uint64) {
	if s.ep.active {
		s.log.Info(logAccepting, "seconds", int(time.Since(s.ep.start)/time.Second))
		s.ep = episode{}
	}
	s.started = true
	s.liveStart = first
	s.releaseGen++
	s.broadcastLocked()
}

// ackFailed handles a spool that did not keep an acknowledgement: the stream sends again after a wait.
func (s *Sender) ackFailed(ctx context.Context, err error) outcome {
	if ctx.Err() != nil {
		return outStop
	}
	if errors.Is(err, spool.ErrClosed) {
		return outRetry
	}
	s.mu.Lock()
	warn := !s.ackWarned
	s.ackWarned = true
	s.mu.Unlock()
	if warn {
		s.log.Warn(logAckNotKept, "err", err.Error())
	}

	return outRetry
}

// ---------------------------------------------------------------------------------------------------------------
// Refused records

// giveUp acknowledges a refused record and hands the spool a gap that stands for it, unless the hourly bound is used
// up: then the stream parks.
func (s *Sender) giveUp(ctx context.Context, st *stream, rec *model.Record, status int) outcome {
	s.giveMu.Lock()
	defer s.giveMu.Unlock()
	now := time.Now()
	kept := s.givenUp[:0]
	for _, at := range s.givenUp {
		if now.Sub(at) < giveUpWindow {
			kept = append(kept, at)
		}
	}
	s.givenUp = kept
	if len(kept) >= MaxGivenUpPerHour {
		st.parkUntil = kept[len(kept)-MaxGivenUpPerHour].Add(giveUpWindow)
		s.warnRefusedWait(status)

		return outParked
	}
	if !s.gapped[rec.Seq] {
		gap := model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: "agent", CapturedAt: now.UTC()},
			Data: &model.Gap{
				From: rec.CapturedAt, To: rec.CapturedAt.Add(gapSpan), Cause: model.GapUnknown,
				FirstSeq: rec.Seq, LastSeq: rec.Seq,
			},
		}
		if err := s.opts.Spool.Put(ctx, []model.Record{gap}); err != nil {
			if ctx.Err() != nil {
				return outStop
			}

			return outRetry
		}
		s.gapped[rec.Seq] = true
	}
	if err := s.opts.Spool.Ack(ctx, rec.Seq, rec.Seq); err != nil {
		return s.ackFailed(ctx, err)
	}
	delete(s.gapped, rec.Seq)
	s.givenUp = append(s.givenUp, now)
	st.pos = rec.Seq
	st.wait = 0
	s.log.Warn(logRefused, keyStatus, status, "seq", rec.Seq, "kind", string(rec.Kind()))

	return outDone
}

// warnRefusedWait logs the warning of a wait for a give-up slot once for the wait, whichever stream met it.
func (s *Sender) warnRefusedWait(status int) {
	s.mu.Lock()
	warn := !s.refusedWarned
	s.refusedWarned = true
	s.mu.Unlock()
	if warn {
		s.log.Warn(logNotAccepting, keyReason, reasonRefused, keyStatus, status)
	}
}

// park waits until a give-up slot is free and reports false when ctx ended. The refused records are sent again then.
func (s *Sender) park(ctx context.Context, st *stream) bool {
	if !pause(ctx, time.Until(st.parkUntil)) {
		return false
	}
	s.mu.Lock()
	s.refusedWarned = false
	s.mu.Unlock()

	return true
}
