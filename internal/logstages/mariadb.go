package logstages

import (
	"strings"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	serverProgram = "mariadbd"
	legacyProgram = "mysqld"
)

// item is a record on its way through the stages, with what the collector needs to know about its first line.
type item struct {
	rec      model.Record
	mark     Mark
	runStart time.Time
	// arrival orders the lines by the time they reached the stages.
	arrival uint64
}

func (i *item) output() Output {
	return Output{Rec: i.rec, From: i.mark.Before}
}

// anchor is the earlier of the capture time and the start of the run that read the line.
func (i *item) anchor() time.Time {
	if i.runStart.Before(i.rec.CapturedAt) {
		return i.runStart
	}

	return i.rec.CapturedAt
}

// ownLine gives the item a copy of its log line, so that later changes to the caller's line do not reach it.
func (i *item) ownLine(l *model.LogLine) *model.LogLine {
	c := *l
	if l.Priority != nil {
		p := *l.Priority
		c.Priority = &p
	}

	i.rec.Data = &c

	return &c
}

// mariaGroup is the open MariaDB entry: a header line and the lines joined to it.
type mariaGroup struct {
	first  item
	header *model.LogLine
	level  *mariaHeader
	event  string
	msg    *message
}

func (g *mariaGroup) clone() *mariaGroup {
	c := *g
	c.header = cloneLine(g.header)
	c.first.rec.Data = c.header
	c.msg = g.msg.clone()

	return &c
}

func cloneLine(l *model.LogLine) *model.LogLine {
	c := *l
	if l.Priority != nil {
		p := *l.Priority
		c.Priority = &p
	}

	return &c
}

func isServer(l *model.LogLine) bool {
	return l.Program == serverProgram || l.Program == legacyProgram
}

func sameKey(g *mariaGroup, l *model.LogLine) bool {
	return g.header.Host == l.Host && g.header.Program == l.Program && g.header.PID == l.PID
}

func absDuration(d time.Duration) time.Duration {
	if d < 0 {
		return -d
	}

	return d
}

// addMaria takes the next item through the MariaDB stage and appends the items to pass on now, in order.
func (s *Stages) addMaria(it item, ready []item) []item {
	l, ok := it.rec.Data.(*model.LogLine)
	if !ok || !isServer(l) || strings.IndexByte(l.Message, '\n') >= 0 {
		return append(ready, it)
	}

	if header := s.parseHeader(l.Message); header != nil {
		ready = s.flushMaria(ready)
		s.openMaria(it, l, header)

		return ready
	}

	if g := s.maria; g != nil && sameKey(g, l) {
		if absDuration(it.rec.CapturedAt.Sub(g.first.rec.CapturedAt)) <= MaxSpan && g.msg.tryAdd(l.Message, l.Truncated) {
			return ready
		}

		ready = s.flushMaria(ready)
	}

	return append(ready, it)
}

func (s *Stages) parseHeader(msg string) *mariaHeader {
	if msg != "" && isDigit(msg[0]) && len(msg) <= model.MaxTextBytes {
		return parseHeader(msg)
	}

	return nil
}

func (s *Stages) openMaria(it item, l *model.LogLine, header *mariaHeader) {
	g := &mariaGroup{first: it, level: header}
	g.header = it.ownLine(l)
	g.first.rec.Data = g.header
	g.event = classifyHeader(header, &s.recoveryOpen)
	g.msg = newMessage(header.message, l.Truncated)
	s.maria = g
}

// flushMaria appends the open entry as one record and closes it.
func (s *Stages) flushMaria(ready []item) []item {
	g := s.maria
	if g == nil {
		return ready
	}

	s.maria = nil

	text, truncated := g.msg.build()
	prio := g.header.Priority

	if p, known := g.level.priority(); known {
		v := p
		prio = &v
	}

	first := g.first
	first.rec.Data = &model.LogLine{
		Log:       g.header.Log,
		Host:      g.header.Host,
		Program:   g.header.Program,
		PID:       g.header.PID,
		Priority:  prio,
		Message:   text,
		Truncated: truncated,
		Event:     g.event,
	}

	return append(ready, first)
}
