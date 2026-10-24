package sender

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"maps"
	"math/rand/v2"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/spool"
	"github.com/LarsLaskowski/Vandox/internal/wire"
)

const (
	testAgent = "agent-one"
	testBoot  = "0f0f0f0f-1111-4222-8333-444455556666"
	otherBoot = "9a9a9a9a-aaaa-4bbb-8ccc-ddddeeeeffff"
	// testToken is the agent token the transport tests send; it is long enough for the configuration loader.
	testToken = "tkn-0123456789abcdef0123456789abcdef"
	mebi      = 1 << 20

	// The texts of the sender's log lines (plan, Log lines).
	msgNotAccepting = "backend not accepting batches"
	msgAccepting    = "backend accepting batches again"
	msgBfStarted    = "backfill started"
	msgBfFinished   = "backfill finished"
	msgBfPaused     = "backfill paused"
	msgBfResumed    = "backfill resumed"
	msgRefused      = "record refused"
	msgAckNotKept   = "acknowledgement not kept"
	msgNotReadable  = "spool not readable"

	// poisonTag starts the message of a record the fake backend refuses.
	poisonTag = "poison"

	// issuedSeq is the first number a spool issues in a bubble (the fake clock starts at 2000-01-01 00:00 UTC).
	issuedSeq = 946684801000000
)

// connFail is the result of a refused connection.
var connFail = Result{Reason: "connection failed"}

// bubble runs f in a testing/synctest bubble: fake time, no network (record 0062).
func bubble(t *testing.T, f func(t *testing.T)) {
	t.Helper()

	synctest.Test(t, f)
}

// advance moves the fake clock on by d and lets every goroutine run until it is blocked again.
func advance(d time.Duration) {
	time.Sleep(d)
	synctest.Wait()
}

// advanceUntil moves the fake clock on in steps until cond holds; it fails the test after limit.
func advanceUntil(t *testing.T, limit, step time.Duration, what string, cond func() bool) bool {
	t.Helper()

	for waited := time.Duration(0); ; waited += step {
		synctest.Wait()
		if cond() {
			return true
		}
		if waited >= limit {
			t.Errorf("%s did not happen within %v of fake time", what, limit)

			return false
		}
		time.Sleep(step)
	}
}

// sleepCtx sleeps for d and reports false when ctx ended first.
func sleepCtx(ctx context.Context, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-timer.C:
		return true
	case <-ctx.Done():
		return false
	}
}

// zeroRand draws the lowest value, so a wait is exactly half of its schedule value.
func zeroRand() float64 { return 0 }

// cyclingRand returns a Rand that cycles through values spread over [0, 1).
func cyclingRand() func() float64 {
	var mu sync.Mutex
	values := []float64{0, 0.999999, 0.5, 0.25, 0.75, 0.1}
	next := 0

	return func() float64 {
		mu.Lock()
		defer mu.Unlock()
		v := values[next%len(values)]
		next++

		return v
	}
}

// testSecret returns the agent token as the configuration loader makes it, since a config.Secret has no public
// constructor.
func testSecret(t *testing.T, token string) config.Secret {
	t.Helper()

	path := filepath.Join(t.TempDir(), "agent.yaml")
	if err := os.WriteFile(path, []byte("agent_id: web-1\nbackend:\n  url: http://100.64.0.1:8081\n"), 0o600); err != nil {
		t.Fatalf("writing the configuration: %v", err)
	}
	cfg, err := config.LoadAgent(path, []string{"VANDOX_AGENT_TOKEN=" + token})
	if err != nil {
		t.Fatalf("LoadAgent() error = %v, want nil", err)
	}

	return cfg.Secrets.AgentToken
}

// openSpool opens a real spool in dir for the boot; the cleanup closes it inside the bubble.
func openSpool(t *testing.T, dir, boot string, maxBytes int64) *spool.Spool {
	t.Helper()

	sp, err := spool.Open(spool.Options{
		Dir:          dir,
		AgentID:      testAgent,
		BootID:       boot,
		MaxBytes:     maxBytes,
		SyncInterval: 10000 * time.Hour,
		Available:    func(string) (uint64, error) { return 1 << 50, nil },
	}, slog.New(slog.DiscardHandler))
	if err != nil {
		t.Fatalf("spool.Open() error = %v, want a spool", err)
	}
	t.Cleanup(func() { _ = sp.Close(context.Background()) })

	return sp
}

// ---------------------------------------------------------------------------------------------------------------
// Records

// textPool is a megabyte of letters and digits; they hardly compress, so a batch has a realistic size.
var textPool = sync.OnceValue(func() string {
	const alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	rng := rand.New(rand.NewPCG(5, 6))
	b := make([]byte, mebi)
	for i := range b {
		b[i] = alphabet[rng.IntN(len(alphabet))]
	}

	return string(b)
})

// textFill returns n letters and digits from a random place of the pool.
func textFill(rng *rand.Rand, n int) string {
	pool := textPool()
	n = min(n, len(pool)-1)

	return pool[rng.IntN(len(pool)-n):][:n]
}

// textRec returns a log line record whose message is the tag, a space and filler up to size bytes.
func textRec(at time.Time, tag string, size int, rng *rand.Rand) model.Record {
	msg := tag + " " + textFill(rng, max(size-len(tag)-1, 0))

	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "logs", CapturedAt: at},
		Data: &model.LogLine{Log: "app.log", Message: msg},
	}
}

// metricRec returns a metric record.
func metricRec(at time.Time, value float64) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "system", CapturedAt: at},
		Data: &model.MetricPoint{Name: "load_1m", Value: value, Unit: "ratio"},
	}
}

// serviceRec returns a service state record.
func serviceRec(at time.Time, i int, rng *rand.Rand) model.Record {
	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "systemd", CapturedAt: at},
		Data: &model.ServiceState{
			Unit: "svc" + strconv.Itoa(i) + ".service", LoadState: "loaded", ActiveState: "active", SubState: "running",
			Restarts: rng.Uint32N(5),
		},
	}
}

// snapshotRec returns a process snapshot record of n processes.
func snapshotRec(at time.Time, n int32, rng *rand.Rand) model.Record {
	procs := make([]model.ProcessSample, n)
	for i := range n {
		procs[i] = model.ProcessSample{
			PID: i + 1, Command: "cmd" + strconv.Itoa(int(i)), Cmdline: "/usr/bin/cmd " + textFill(rng, 40),
			CPUPercent: rng.Float64() * 50, RSSBytes: uint64(rng.Uint32()),
		}
	}

	return model.Record{
		Meta: model.Meta{Origin: model.OriginAgent, Source: "processes", CapturedAt: at},
		Data: &model.ProcessSnapshot{Complete: true, Processes: procs},
	}
}

// variedRec returns the i-th record of a varied stream of metrics, log lines, service states and snapshots.
func variedRec(i int, at time.Time, rng *rand.Rand) model.Record {
	switch i % 5 {
	case 0:
		return snapshotRec(at, 40, rng)
	case 1:
		return metricRec(at, rng.Float64()*100)
	case 2:
		return serviceRec(at, i, rng)
	default:
		return textRec(at, "line"+strconv.Itoa(i), 100+rng.IntN(2900), rng)
	}
}

// tagOf returns the first word of a log line's message, and the kind of any other record.
func tagOf(rec model.Record) string {
	if l, ok := rec.Data.(*model.LogLine); ok {
		tag, _, _ := strings.Cut(l.Message, " ")

		return tag
	}

	return string(rec.Kind())
}

// isPoison reports whether the record is one the fake backend refuses.
func isPoison(rec model.Record) bool {
	return strings.HasPrefix(tagOf(rec), poisonTag)
}

// lineLen returns the length of the record's wire line without the line feed.
func lineLen(t testing.TB, rec model.Record) int {
	t.Helper()

	if rec.Seq == 0 {
		rec.Seq = issuedSeq
	}
	line, err := wire.MarshalRecord(&rec)
	if err != nil {
		t.Fatalf("MarshalRecord(seq %d) error = %v, want nil", rec.Seq, err)
	}

	return len(line)
}

// giantRec returns a process snapshot whose wire line, with a sequence number as long as the spool issues them in
// the bubble, is exactly want bytes: the largest record the wire format allows.
func giantRec(t testing.TB, at time.Time, want int) model.Record {
	t.Helper()

	rng := rand.New(rand.NewPCG(7, 7))
	build := func(n, pad int) model.Record {
		procs := make([]model.ProcessSample, n)
		var pid int32
		for i := range procs {
			pid++
			procs[i] = model.ProcessSample{PID: pid, Command: "cmd", Cmdline: textFill(rng, 400)}
		}
		procs[n-1].Cmdline = textFill(rng, pad)

		return model.Record{
			Meta: model.Meta{Origin: model.OriginAgent, Source: "processes", CapturedAt: at, Seq: issuedSeq},
			Data: &model.ProcessSnapshot{Complete: true, Processes: procs},
		}
	}
	per := lineLen(t, build(100, 400)) / 100
	n := (want - 8000) / per
	for range 5 {
		base := lineLen(t, build(n, 1))
		if base <= want-1000 && base >= want-15000 {
			break
		}
		n += (want - 8000 - base) / per
	}
	if n < 1 || n > model.MaxItems {
		t.Fatalf("giantRec(%d): %d processes, want 1 to %d", want, n, model.MaxItems)
	}
	rec := build(n, want-lineLen(t, build(n, 1))+1)
	if got := lineLen(t, rec); got != want {
		t.Fatalf("giantRec(%d) line = %d bytes, want exactly %d", want, got, want)
	}
	rec.Seq = 0

	return rec
}

// ---------------------------------------------------------------------------------------------------------------
// Log recorder

// logEntry is one log line the sender wrote.
type logEntry struct {
	at    time.Time
	level slog.Level
	msg   string
	keys  []string
	attrs map[string]slog.Value
}

// text returns the line as one string for leak checks.
func (e logEntry) text() string {
	var b strings.Builder
	b.WriteString(e.msg)
	for _, k := range e.keys {
		b.WriteString(" " + k + "=" + e.attrs[k].String())
	}

	return b.String()
}

// str returns a string attribute, or "" when it is absent.
func (e logEntry) str(key string) string {
	v, ok := e.attrs[key]
	if !ok {
		return ""
	}

	return v.String()
}

// num returns a numeric attribute.
func (e logEntry) num(key string) (int64, bool) {
	v, ok := e.attrs[key]
	if !ok {
		return 0, false
	}
	switch v.Kind() {
	case slog.KindInt64, slog.KindUint64:
		n, err := strconv.ParseInt(v.String(), 10, 64)

		return n, err == nil
	case slog.KindDuration:
		return int64(v.Duration() / time.Second), true
	default:
		return 0, false
	}
}

// seq returns a sequence number attribute.
func (e logEntry) seq(key string) (uint64, bool) {
	v, ok := e.attrs[key]
	if !ok {
		return 0, false
	}
	n, err := strconv.ParseUint(v.String(), 10, 64)

	return n, err == nil
}

// logSink is a slog.Handler that keeps every record.
type logSink struct {
	mu      *sync.Mutex
	entries *[]logEntry
	pre     []slog.Attr
}

func newLogSink() *logSink {
	return &logSink{mu: &sync.Mutex{}, entries: &[]logEntry{}}
}

// Enabled accepts every level.
func (l *logSink) Enabled(context.Context, slog.Level) bool { return true }

// Handle keeps the record with its attributes in order.
func (l *logSink) Handle(_ context.Context, r slog.Record) error {
	e := logEntry{at: time.Now(), level: r.Level, msg: r.Message, attrs: map[string]slog.Value{}}
	for _, a := range l.pre {
		e.keys = append(e.keys, a.Key)
		e.attrs[a.Key] = a.Value.Resolve()
	}
	r.Attrs(func(a slog.Attr) bool {
		e.keys = append(e.keys, a.Key)
		e.attrs[a.Key] = a.Value.Resolve()

		return true
	})
	l.mu.Lock()
	defer l.mu.Unlock()
	*l.entries = append(*l.entries, e)

	return nil
}

// WithAttrs returns a handler that adds attrs to every record.
func (l *logSink) WithAttrs(attrs []slog.Attr) slog.Handler {
	return &logSink{mu: l.mu, entries: l.entries, pre: append(slices.Clone(l.pre), attrs...)}
}

// WithGroup ignores the group.
func (l *logSink) WithGroup(string) slog.Handler { return l }

// all returns a copy of the recorded entries.
func (l *logSink) all() []logEntry {
	l.mu.Lock()
	defer l.mu.Unlock()

	return slices.Clone(*l.entries)
}

// find returns the entries whose message is msg.
func (l *logSink) find(msg string) []logEntry {
	var out []logEntry
	for _, e := range l.all() {
		if e.msg == msg {
			out = append(out, e)
		}
	}

	return out
}

// count returns how many entries carry the message msg.
func (l *logSink) count(msg string) int { return len(l.find(msg)) }

// notAccepting returns the warnings of the episodes, in order.
func (l *logSink) notAccepting() []logEntry { return l.find(msgNotAccepting) }

// ---------------------------------------------------------------------------------------------------------------
// Spool wrapper

// recSpool is the real spool with injected failures and counters; the sender uses it through the Spool interface,
// the tests put records into the real spool directly.
type recSpool struct {
	*spool.Spool

	mu        sync.Mutex
	ackErr    func(first, last uint64) error
	readErr   func(after uint64) error
	putErr    func(records []model.Record) error
	readHook  func(b *spool.Batch)
	acks      int
	tooMany   int
	maxRanges int
	reads     int
}

// setAckErr sets the injected acknowledgement failure; nil removes it.
func (s *recSpool) setAckErr(f func(first, last uint64) error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ackErr = f
}

// setReadErr sets the injected read failure; nil removes it.
func (s *recSpool) setReadErr(f func(after uint64) error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.readErr = f
}

// setPutErr sets the injected failure of the sender's own hand-overs; nil removes it.
func (s *recSpool) setPutErr(f func(records []model.Record) error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.putErr = f
}

// Read returns what the real spool reads unless a failure is injected.
func (s *recSpool) Read(ctx context.Context, after uint64, maxRecords int, maxBytes int64) (spool.Batch, error) {
	s.mu.Lock()
	fail, hook := s.readErr, s.readHook
	s.reads++
	s.mu.Unlock()
	if fail != nil {
		if err := fail(after); err != nil {
			return spool.Batch{}, err
		}
	}
	b, err := s.Spool.Read(ctx, after, maxRecords, maxBytes)
	if err == nil && hook != nil {
		hook(&b)
	}

	return b, err
}

// Ack acknowledges in the real spool unless a failure is injected, and measures the ranges it holds afterwards.
func (s *recSpool) Ack(ctx context.Context, first, last uint64) error {
	s.mu.Lock()
	fail := s.ackErr
	s.mu.Unlock()
	if fail != nil {
		if err := fail(first, last); err != nil {
			return err
		}
	}
	err := s.Spool.Ack(ctx, first, last)
	s.mu.Lock()
	defer s.mu.Unlock()
	s.acks++
	if errors.Is(err, spool.ErrTooManyRanges) {
		s.tooMany++
	}
	s.maxRanges = max(s.maxRanges, s.AckRanges())

	return err
}

// Put hands records to the real spool unless a failure is injected.
func (s *recSpool) Put(ctx context.Context, records []model.Record) error {
	s.mu.Lock()
	fail := s.putErr
	s.mu.Unlock()
	if fail != nil {
		if err := fail(records); err != nil {
			return err
		}
	}

	return s.Spool.Put(ctx, records)
}

// stats returns the counters.
func (s *recSpool) stats() (acks, tooMany, maxRanges, reads int) {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.acks, s.tooMany, s.maxRanges, s.reads
}

// unacked returns the records the spool holds unacknowledged.
func (s *recSpool) unacked(t testing.TB) []model.Record {
	t.Helper()

	b, err := s.Spool.Read(context.Background(), 0, MaxBatchRecords*4, 1<<30)
	if err != nil {
		t.Fatalf("Read(0) error = %v, want nil", err)
	}

	return b.Records
}

// ---------------------------------------------------------------------------------------------------------------
// Fake transport

// call is one request the fake transport received.
type call struct {
	n         int
	at, end   time.Time
	header    wire.Header
	recs      []model.Record
	size      int
	res       Result
	committed bool
	inflight  int
	// lastSeq is the spool's newest number when the request arrived.
	lastSeq uint64
}

// mode returns the batch's mode.
func (c call) mode() wire.Mode { return c.header.Mode }

// seqs returns the sequence numbers of the batch.
func (c call) seqs() []uint64 {
	out := make([]uint64, len(c.recs))
	for i, r := range c.recs {
		out[i] = r.Seq
	}

	return out
}

// first returns the first sequence number, 0 for an empty batch.
func (c call) first() uint64 {
	if len(c.recs) == 0 {
		return 0
	}

	return c.recs[0].Seq
}

// last returns the last sequence number, 0 for an empty batch.
func (c call) last() uint64 {
	if len(c.recs) == 0 {
		return 0
	}

	return c.recs[len(c.recs)-1].Seq
}

// lineBytes returns the bytes of the batch's record lines without their line feeds.
func (c call) lineBytes(t testing.TB) int {
	t.Helper()

	n := 0
	for _, r := range c.recs {
		n += lineLen(t, r)
	}

	return n
}

// has reports whether the batch holds the sequence number.
func (c call) has(seq uint64) bool { return slices.Contains(c.seqs(), seq) }

// reply is what the fake backend does with one request.
type reply struct {
	res Result
	// commit stores the records, whatever the answer: with a failing answer it is a lost answer.
	commit bool
	delay  time.Duration
	// hang never answers: the request ends with its context.
	hang bool
}

func replyOK() reply { return reply{res: Result{Status: 200}, commit: true} }

func replyFail(res Result) reply { return reply{res: res} }

func replyLost() reply { return reply{res: connFail, commit: true} }

func replyHang() reply { return reply{hang: true} }

// replyRefuse is a refusal of the batch itself: 422 with the marker header.
func replyRefuse() reply { return reply{res: Result{Status: 422, Refused: true}} }

func (r reply) after(d time.Duration) reply {
	r.delay = d

	return r
}

// script decides the reply to one request.
type script func(c call) reply

func allOK(call) reply { return replyOK() }

func failAll(res Result) script {
	return func(call) reply { return replyFail(res) }
}

// refusePoison accepts every batch except one that holds a poison record, which it refuses as invalid.
func refusePoison(c call) reply {
	if slices.ContainsFunc(c.recs, isPoison) {
		return replyRefuse()
	}

	return replyOK()
}

// fakeTransport is the backend: it decodes every body, answers as scripted and keeps what it committed.
type fakeTransport struct {
	t       *testing.T
	lastSeq func() uint64

	mu          sync.Mutex
	script      script
	calls       []call
	stored      map[uint64]int
	inflight    int
	maxInflight int
}

func newFakeTransport(t *testing.T) *fakeTransport {
	return &fakeTransport{t: t, script: allOK, stored: map[uint64]int{}}
}

// setScript replaces the script.
func (f *fakeTransport) setScript(s script) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.script = s
}

// ctxResult is what a request that ended with its context comes back with.
func ctxResult(ctx context.Context) Result {
	if errors.Is(ctx.Err(), context.DeadlineExceeded) {
		return Result{Reason: "timeout"}
	}

	return Result{}
}

// decode decodes the body of a request; a body that does not decode fails the test.
func (f *fakeTransport) decode(body []byte) call {
	c := call{size: len(body)}
	dec, err := wire.NewDecoder(bytes.NewReader(body), wire.DefaultLimits())
	if err != nil {
		f.t.Errorf("a request body does not decode: %v", err)

		return c
	}
	defer func() { _ = dec.Close() }()
	c.header = dec.Header()
	for {
		rec, err := dec.Next()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			f.t.Errorf("a request body does not decode: %v", err)

			break
		}
		c.recs = append(c.recs, rec)
	}

	return c
}

// Send records the request and answers it as scripted.
func (f *fakeTransport) Send(ctx context.Context, body []byte) Result {
	c := f.decode(body)
	c.at = time.Now()
	if f.lastSeq != nil {
		c.lastSeq = f.lastSeq()
	}
	f.mu.Lock()
	f.inflight++
	c.inflight = f.inflight
	f.maxInflight = max(f.maxInflight, f.inflight)
	idx := len(f.calls)
	c.n = idx
	f.calls = append(f.calls, c)
	rep := f.script(c)
	f.mu.Unlock()

	res, interrupted := waitReply(ctx, rep)
	if !interrupted {
		res = rep.res
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.inflight--
	fc := &f.calls[idx]
	fc.end, fc.res = time.Now(), res
	if rep.commit && !interrupted {
		fc.committed = true
		for _, r := range c.recs {
			f.stored[r.Seq]++
		}
	}

	return res
}

// waitReply lets the reply's delay pass or its hang last, and reports whether the context ended it.
func waitReply(ctx context.Context, rep reply) (Result, bool) {
	if rep.hang {
		<-ctx.Done()

		return ctxResult(ctx), true
	}
	if rep.delay > 0 && !sleepCtx(ctx, rep.delay) {
		return ctxResult(ctx), true
	}

	return Result{}, false
}

// snapshot returns the requests so far.
func (f *fakeTransport) snapshot() []call {
	f.mu.Lock()
	defer f.mu.Unlock()

	return slices.Clone(f.calls)
}

// callsOf returns the requests of a mode.
func (f *fakeTransport) callsOf(mode wire.Mode) []call {
	var out []call
	for _, c := range f.snapshot() {
		if c.mode() == mode {
			out = append(out, c)
		}
	}

	return out
}

// committedSet returns the committed sequence numbers with the number of times each was committed.
func (f *fakeTransport) committedSet() map[uint64]int {
	f.mu.Lock()
	defer f.mu.Unlock()

	return maps.Clone(f.stored)
}

// maxInFlight returns the largest number of requests that were in flight together.
func (f *fakeTransport) maxInFlight() int {
	f.mu.Lock()
	defer f.mu.Unlock()

	return f.maxInflight
}

// firstReceipt returns, per message tag of a log line, the time of the first request that held it.
func firstReceipt(calls []call) map[string]time.Time {
	out := map[string]time.Time{}
	for _, c := range calls {
		for _, r := range c.recs {
			if _, ok := out[tagOf(r)]; !ok {
				out[tagOf(r)] = c.at
			}
		}
	}

	return out
}

// ---------------------------------------------------------------------------------------------------------------
// Rig

// feeder hands one record to the spool every period.
type feeder struct {
	mu     sync.Mutex
	handed map[string]time.Time
	n      int
	cancel context.CancelFunc
	done   chan struct{}
}

// halt stops the feeder.
func (f *feeder) halt() {
	f.cancel()
	<-f.done
}

// count returns how many records the feeder handed over.
func (f *feeder) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()

	return f.n
}

// handedAt returns the hand-over times by tag.
func (f *feeder) handedAt() map[string]time.Time {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make(map[string]time.Time, len(f.handed))
	for k, v := range f.handed {
		out[k] = v
	}

	return out
}

// rig is a sender on a real spool with the fake backend.
type rig struct {
	t         *testing.T
	dir       string
	sp        *recSpool
	tr        *fakeTransport
	logs      *logSink
	snd       *Sender
	bandwidth int64
	rand      func() float64
	rng       *rand.Rand
	seeds     uint64
	cancel    context.CancelFunc
	done      chan struct{}
	feeders   []*feeder
}

// newRig returns a rig on a fresh spool; call it inside the bubble.
func newRig(t *testing.T) *rig {
	t.Helper()

	dir := t.TempDir()
	r := newRigOn(t, openSpool(t, dir, testBoot, 128*mebi))
	r.dir = dir

	return r
}

// newRigOn returns a rig on an open spool.
func newRigOn(t *testing.T, sp *spool.Spool) *rig {
	t.Helper()

	r := &rig{
		t: t, sp: &recSpool{Spool: sp}, tr: newFakeTransport(t), logs: newLogSink(),
		bandwidth: 1 << 30, rand: zeroRand, rng: rand.New(rand.NewPCG(11, 13)),
	}
	r.tr.lastSeq = sp.LastSeq
	t.Cleanup(r.close)

	return r
}

// start builds a sender and runs it; it returns once the sender has started and is waiting.
func (r *rig) start() {
	r.t.Helper()

	r.stop()
	ctx, cancel := context.WithCancel(context.Background())
	r.cancel = cancel
	done := make(chan struct{})
	r.done = done
	snd := New(Options{Spool: r.sp, Transport: r.tr, Bandwidth: r.bandwidth, Rand: r.rand}, slog.New(r.logs))
	r.snd = snd
	go func() {
		defer close(done)
		snd.Run(ctx)
	}()
	synctest.Wait()
}

// stop stops the sender and waits for Run to return.
func (r *rig) stop() {
	if r.cancel == nil {
		return
	}
	r.cancel()
	<-r.done
	r.cancel = nil
}

// close stops the feeders and the sender.
func (r *rig) close() {
	for _, f := range r.feeders {
		f.halt()
	}
	r.stop()
}

// put hands records to the real spool and returns their sequence numbers.
func (r *rig) put(recs ...model.Record) []uint64 {
	r.t.Helper()

	synctest.Wait()
	before := r.sp.LastSeq()
	if err := r.sp.Spool.Put(context.Background(), recs); err != nil {
		r.t.Fatalf("Put(%d records) error = %v, want nil", len(recs), err)
	}

	return seqRange(before+1, r.sp.LastSeq())
}

// u64 converts a non-negative int.
func u64(t testing.TB, n int) uint64 {
	t.Helper()

	if n < 0 {
		t.Fatalf("u64(%d): negative value in a test", n)

		return 0
	}

	return uint64(n)
}

// seqRange returns first to last.
func seqRange(first, last uint64) []uint64 {
	var out []uint64
	for s := first; s <= last; s++ {
		out = append(out, s)
	}

	return out
}

// backlog hands n text records of size bytes to the spool before the sender starts; they are the backlog of a start.
func (r *rig) backlog(tag string, n, size int) []uint64 {
	r.t.Helper()

	recs := make([]model.Record, 0, n)
	for i := range n {
		recs = append(recs, textRec(time.Now().UTC(), tag+strconv.Itoa(i), size, r.rng))
	}

	return r.put(recs...)
}

// live hands one text record to the spool and returns its sequence number.
func (r *rig) live(tag string, size int) uint64 {
	r.t.Helper()

	return r.put(textRec(time.Now().UTC(), tag, size, r.rng))[0]
}

// feed starts a feeder: after phase, one record every period.
func (r *rig) feed(phase, period time.Duration, mk func(i int, at time.Time) model.Record) *feeder {
	ctx, cancel := context.WithCancel(context.Background())
	f := &feeder{handed: map[string]time.Time{}, cancel: cancel, done: make(chan struct{})}
	r.feeders = append(r.feeders, f)
	go func() {
		defer close(f.done)
		r.runFeeder(ctx, f, phase, period, mk)
	}()

	return f
}

// runFeeder is the body of a feeder.
func (r *rig) runFeeder(ctx context.Context, f *feeder, phase, period time.Duration, mk func(i int, at time.Time) model.Record) {
	if !sleepCtx(ctx, phase) {
		return
	}
	for i := 0; ; i++ {
		rec := mk(i, time.Now().UTC())
		if err := r.sp.Spool.Put(context.Background(), []model.Record{rec}); err != nil {
			r.t.Errorf("feeder Put() error = %v, want nil", err)

			return
		}
		f.mu.Lock()
		f.handed[tagOf(rec)] = time.Now()
		f.n++
		f.mu.Unlock()
		if !sleepCtx(ctx, period) {
			return
		}
	}
}

// ownRng returns a random source for one feeder, so no source is used by two goroutines.
func (r *rig) ownRng() *rand.Rand {
	r.seeds++

	return rand.New(rand.NewPCG(r.seeds, 77))
}

// varied returns the generator of a varied stream.
func (r *rig) varied() func(i int, at time.Time) model.Record {
	rng := r.ownRng()

	return func(i int, at time.Time) model.Record { return variedRec(i, at, rng) }
}

// tagged returns a generator of small text records tagged prefix and the number.
func (r *rig) tagged(prefix string, size int) func(i int, at time.Time) model.Record {
	rng := r.ownRng()

	return func(i int, at time.Time) model.Record { return textRec(at, prefix+strconv.Itoa(i), size, rng) }
}

// hasUnacked reports whether the spool holds an unacknowledged record.
func (r *rig) hasUnacked() bool {
	b, err := r.sp.Spool.Read(context.Background(), 0, 1, 1<<30)
	if err != nil {
		r.t.Fatalf("Read(0) error = %v, want nil", err)
	}

	return len(b.Records) > 0
}

// ---------------------------------------------------------------------------------------------------------------
// Assertions

// requireComplete fails the test unless the backend committed every sequence number the spool issued from first, and
// the spool holds nothing unacknowledged.
func requireComplete(t *testing.T, r *rig, first uint64) {
	t.Helper()

	requireCompleteExcept(t, r, first, nil)
}

// requireCompleteExcept is requireComplete for a stream where the numbers in skip were given up, not committed.
func requireCompleteExcept(t *testing.T, r *rig, first uint64, skip map[uint64]bool) {
	t.Helper()

	stored := r.tr.committedSet()
	missing := 0
	for s := first; s <= r.sp.LastSeq(); s++ {
		if stored[s] == 0 && !skip[s] {
			missing++
		}
	}
	if missing > 0 {
		t.Errorf("the backend committed all but %d of the %d sequence numbers from %d, want every one", missing, r.sp.LastSeq()-first+1, first)
	}
	if b, err := r.sp.Spool.Read(context.Background(), 0, 1, 1<<30); err != nil || len(b.Records) > 0 {
		t.Errorf("the spool holds an unacknowledged record (%v, error %v), want none", describeSeqs(b.Records), err)
	}
}

// requireNoDuplicates fails the test when a sequence number was committed more than once.
func requireNoDuplicates(t *testing.T, r *rig) {
	t.Helper()

	for s, n := range r.tr.committedSet() {
		if n > 1 {
			t.Errorf("sequence number %d was acknowledged %d times, want once", s, n)

			return
		}
	}
}

// requireInOrder fails the test unless each batch of calls starts after the previous one ended.
func requireInOrder(t *testing.T, what string, calls []call) {
	t.Helper()

	for i := 1; i < len(calls); i++ {
		if calls[i].first() <= calls[i-1].last() {
			t.Errorf("%s: batch %d starts at seq %d, not after the previous batch's last seq %d", what, i, calls[i].first(), calls[i-1].last())

			return
		}
	}
}

// requireLatency fails the test when a handed-over record (by tag) arrived later than limit after its hand-over, or
// never.
func requireLatency(t *testing.T, handedOver map[string]time.Time, calls []call, limit time.Duration) {
	t.Helper()

	received := firstReceipt(calls)
	late, never := 0, 0
	var worst time.Duration
	for tag, handed := range handedOver {
		at, ok := received[tag]
		switch {
		case !ok:
			never++
		case at.Sub(handed) > limit:
			late++
			worst = max(worst, at.Sub(handed))
		}
	}
	if never > 0 || late > 0 {
		t.Errorf("of %d handed-over records %d never arrived and %d arrived after more than %v (worst %v), want every one within the limit", len(handedOver), never, late, limit, worst)
	}
}

// requireSeqsAbsent fails the test when any call holds one of the sequence numbers.
func requireSeqsAbsent(t *testing.T, what string, calls []call, seqs []uint64) {
	t.Helper()

	for _, c := range calls {
		for _, s := range seqs {
			if c.has(s) {
				t.Errorf("%s: request %d holds seq %d, want it never sent", what, c.n, s)

				return
			}
		}
	}
}

// gapOf returns the gap payload of a record, or nil.
func gapOf(rec model.Record) *model.Gap {
	g, _ := rec.Data.(*model.Gap)

	return g
}

// describeSeqs returns the first numbers of records for messages.
func describeSeqs(recs []model.Record) []uint64 {
	var out []uint64
	for _, r := range recs[:min(len(recs), 3)] {
		out = append(out, r.Seq)
	}

	return out
}

// describe returns a short text of a call for messages.
func (c call) describe() string {
	return fmt.Sprintf("#%d %s %d records %d..%d at %v", c.n, c.mode(), len(c.recs), c.first(), c.last(), c.at.Format("15:04:05"))
}
