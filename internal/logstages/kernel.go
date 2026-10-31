package logstages

import (
	"strconv"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	kernelMaxLines     = 2000
	kernelHeadBytes    = 8192
	kernelProgram      = "kernel"
	markerReserveBytes = 32
	separatorBytes     = 1
	oomStartMarker     = "invoked oom-killer:"
	warnStartMarker    = "------------[ cut here ]------------"
	traceEndMarker     = "---[ end trace "
)

var oomEnds = []string{
	"Out of memory: Killed process",
	"Out of memory (oom_kill_allocating_task): Killed process",
	"Memory cgroup out of memory: Killed process",
	"Out of memory and no killable processes",
}

type tailLine struct {
	text  string
	bytes int
}

// kernelGroup is the open kernel report. It keeps the head and the tail of the report, so its memory does not depend on
// the length of the report.
type kernelGroup struct {
	first      item
	oom        bool
	lines      int
	priority   *uint8
	truncated  bool
	head       []string
	headBytes  int
	headClosed bool
	tail       []tailLine
	tailBytes  int
	omitted    int
}

func (g *kernelGroup) clone() *kernelGroup {
	c := *g
	c.first.rec.Data = cloneLine(g.first.rec.Data.(*model.LogLine))
	c.head = append([]string(nil), g.head...)
	c.tail = append([]tailLine(nil), g.tail...)

	if g.priority != nil {
		p := *g.priority
		c.priority = &p
	}

	return &c
}

func isKernelStart(message string) bool {
	return strings.Contains(message, oomStartMarker) || strings.Contains(message, warnStartMarker)
}

func (g *kernelGroup) isEnd(message string) bool {
	if g.oom {
		for _, marker := range oomEnds {
			if strings.Contains(message, marker) {
				return true
			}
		}

		return false
	}

	return strings.Contains(message, traceEndMarker)
}

// addKernel takes the next item through the kernel stage and appends the items to emit now, in order.
func (s *Stages) addKernel(it item, ready []Output) []Output {
	l, ok := it.rec.Data.(*model.LogLine)
	if !ok || l.Program != kernelProgram {
		return append(ready, it.output())
	}

	if g := s.kernel; g != nil {
		if g.first.rec.Data.(*model.LogLine).Host != l.Host {
			return append(ready, it.output())
		}

		if !isKernelStart(l.Message) && it.rec.CapturedAt.Sub(g.first.rec.CapturedAt) <= MaxSpan {
			g.append(l)

			if g.isEnd(l.Message) || g.lines >= kernelMaxLines {
				ready = s.flushKernel(ready)
			}

			return ready
		}

		ready = s.flushKernel(ready)
	}

	if isKernelStart(l.Message) {
		g := &kernelGroup{first: it, oom: strings.Contains(l.Message, oomStartMarker)}
		g.first.rec.Data = it.ownLine(l)
		g.append(l)
		s.kernel = g

		return ready
	}

	return append(ready, it.output())
}

func (g *kernelGroup) append(l *model.LogLine) {
	g.lines++
	g.truncated = g.truncated || l.Truncated

	if l.Priority != nil && (g.priority == nil || *l.Priority < *g.priority) {
		p := *l.Priority
		g.priority = &p
	}

	if g.headClosed {
		g.appendTail(l.Message)
	} else {
		g.appendHead(l.Message)
	}
}

func (g *kernelGroup) appendHead(text string) {
	bytes := len(text)
	cost := bytes

	if len(g.head) > 0 {
		cost += separatorBytes
	}

	if g.headBytes+cost <= kernelHeadBytes {
		g.head = append(g.head, text)
		g.headBytes += cost

		return
	}

	g.headClosed = true

	if len(g.head) > 0 {
		g.appendTail(text)

		return
	}

	cut, was := cutText(text, kernelHeadBytes)
	g.truncated = g.truncated || was
	g.head = append(g.head, cut)
	g.headBytes = len(cut)
}

func (g *kernelGroup) appendTail(text string) {
	bytes := len(text)
	capacity := model.MaxTextBytes

	if g.omitted > 0 {
		capacity -= markerReserveBytes
	}

	if g.headBytes+g.tailBytes+bytes+separatorBytes > capacity {
		capacity = model.MaxTextBytes - markerReserveBytes

		for len(g.tail) > 0 && g.headBytes+g.tailBytes+bytes+separatorBytes > capacity {
			g.tailBytes -= g.tail[0].bytes + separatorBytes
			g.tail[0] = tailLine{}
			g.tail = g.tail[1:]
			g.omitted++
		}

		if g.headBytes+g.tailBytes+bytes+separatorBytes > capacity {
			text, _ = cutText(text, capacity-g.headBytes-separatorBytes)
			bytes = len(text)
			g.truncated = true
		}
	}

	g.tail = append(g.tail, tailLine{text, bytes})
	g.tailBytes += bytes + separatorBytes
}

// flushKernel appends the open report, if any, and closes it.
func (s *Stages) flushKernel(ready []Output) []Output {
	g := s.kernel
	if g == nil {
		return ready
	}

	s.kernel = nil

	if g.lines == 1 {
		return append(ready, g.first.output())
	}

	var b strings.Builder

	b.WriteString(strings.Join(g.head, "\n"))

	if g.omitted > 0 {
		b.WriteString("\n[" + strconv.Itoa(g.omitted) + " lines omitted]")
	}

	for _, t := range g.tail {
		b.WriteByte('\n')
		b.WriteString(t.text)
	}

	first := g.first
	fl := first.rec.Data.(*model.LogLine)
	first.rec.Data = &model.LogLine{
		Log:       fl.Log,
		Host:      fl.Host,
		Program:   kernelProgram,
		Priority:  g.priority,
		Message:   b.String(),
		Truncated: g.truncated || g.omitted > 0,
	}

	return append(ready, first.output())
}
