package logstages

import (
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// Event names of the mail classification.
const (
	eventPostfixFatal = "postfix.fatal"
	eventPostfixPanic = "postfix.panic"
	eventPostfixDB    = "postfix.database_unavailable"
	eventDovecotFatal = "dovecot.fatal"
	eventDovecotPanic = "dovecot.panic"
	eventDovecotDB    = "dovecot.database_unavailable"
)

const (
	postfixSlash     = "postfix/"
	postfixDash      = "postfix-"
	dovecotProgram   = "dovecot"
	separator        = ": "
	authWorker       = "auth-worker("
	postfixHost      = "connect to mysql server "
	postfixTable     = "mysql:"
	postfixQueryFail = "query failed: "
	dovecotConnect   = "Connect failed to database ("
	dovecotQuery     = "Query failed, retrying: "
	driverMysql      = "mysql: "
	driverMysqlOpen  = "mysql("
	driverSQLPool    = "sqlpool("
	maxDriverSegment = 3

	priorityDebug    uint8 = 7
	priorityWarning  uint8 = 4
	priorityError    uint8 = 3
	priorityCritical uint8 = 2

	severityOther = 0
	severityFatal = 1
	severityPanic = 2
)

type marker struct {
	text     string
	priority uint8
	severity int
}

type found struct {
	priority  uint8
	severity  int
	textStart int
	// segmentEnd is the end of the first segment, or -1 when the marker is at the message start.
	segmentEnd int
}

var postfixMarkers = []marker{
	{"warning: ", priorityWarning, severityOther},
	{"error: ", priorityError, severityOther},
	{"fatal: ", priorityCritical, severityFatal},
	{"panic: ", priorityCritical, severityPanic},
}

var dovecotMarkers = []marker{
	{"Debug: ", priorityDebug, severityOther},
	{"Warning: ", priorityWarning, severityOther},
	{"Error: ", priorityError, severityOther},
	{"Fatal: ", priorityCritical, severityFatal},
	{"Panic: ", priorityCritical, severityPanic},
}

var clientErrors = []string{
	"Can't connect to local MySQL server",
	"Can't connect to MySQL server",
	"MySQL server has gone away",
	"Lost connection to MySQL server",
	"Can't connect to local server",
	"Can't connect to server",
	"Server has gone away",
	"Lost connection to server",
}

// ClassifyMail sets the priority and the event of a mail log line as the import's mail classifier does.
func ClassifyMail(l *model.LogLine) {
	postfix := isPostfix(l.Program)
	if !postfix && l.Program != dovecotProgram {
		return
	}
	var m found
	var ok bool
	if postfix {
		m, ok = tryMarker(l.Message, 0, -1, postfixMarkers)
	} else {
		m, ok = findDovecotMarker(l.Message)
	}
	if ok {
		applyMarker(l, m, postfix)
	}
}

func isPostfix(program string) bool {
	if strings.HasPrefix(program, postfixSlash) || strings.HasPrefix(program, postfixDash) {
		slash := strings.LastIndexByte(program, '/')
		return slash >= 0 && slash < len(program)-1
	}
	return false
}

func applyMarker(l *model.LogLine, m found, postfix bool) {
	p := m.priority
	l.Priority = &p
	switch {
	case m.severity == severityFatal:
		l.Event = pick(postfix, eventPostfixFatal, eventDovecotFatal)
	case m.severity == severityPanic:
		l.Event = pick(postfix, eventPostfixPanic, eventDovecotPanic)
	case m.priority == priorityWarning || m.priority == priorityError:
		var unavailable bool
		if postfix {
			unavailable = isPostfixDatabaseFailure(l.Message[m.textStart:])
		} else {
			unavailable = isDovecotDatabaseFailure(l.Message, m)
		}
		if unavailable {
			l.Event = pick(postfix, eventPostfixDB, eventDovecotDB)
		}
	}
}

func pick(first bool, a, b string) string {
	if first {
		return a
	}
	return b
}

func findDovecotMarker(message string) (found, bool) {
	if m, ok := tryMarker(message, 0, -1, dovecotMarkers); ok {
		return m, true
	}
	end := strings.Index(message, separator)
	if end > 0 && strings.IndexByte(message[:end], ' ') < 0 {
		return tryMarker(message, end+len(separator), end, dovecotMarkers)
	}
	return found{}, false
}

func tryMarker(message string, start, segmentEnd int, markers []marker) (found, bool) {
	rest := message[start:]
	for _, spec := range markers {
		if strings.HasPrefix(rest, spec.text) {
			return found{spec.priority, spec.severity, start + len(spec.text), segmentEnd}, true
		}
	}
	return found{}, false
}

func isPostfixDatabaseFailure(text string) bool {
	if rest, ok := strings.CutPrefix(text, postfixHost); ok {
		return hasNameThenClientError(rest, "", false)
	}
	if rest, ok := strings.CutPrefix(text, postfixTable); ok {
		return hasNameThenClientError(rest, postfixQueryFail, true)
	}
	return false
}

func hasNameThenClientError(text, fixed string, hasFixed bool) bool {
	end := strings.Index(text, separator)
	if end <= 0 || strings.IndexByte(text[:end], ' ') >= 0 {
		return false
	}
	rest := text[end+len(separator):]
	if hasFixed {
		var ok bool
		if rest, ok = strings.CutPrefix(rest, fixed); !ok {
			return false
		}
	}
	return startsWithClientError(rest)
}

func isDovecotDatabaseFailure(message string, m found) bool {
	if m.segmentEnd > 0 && isAuthWorker(message[:m.segmentEnd]) {
		text := message[m.textStart:]
		segments := 0
		for segments < maxDriverSegment {
			var ok bool
			if text, ok = skipDriverSegment(text); !ok {
				break
			}
			segments++
		}
		return segments > 0 && isDovecotClientError(text)
	}
	return false
}

func isDovecotClientError(text string) bool {
	if strings.HasPrefix(text, dovecotConnect) {
		closeAt := strings.Index(text, "): ")
		return closeAt >= 0 && startsWithClientError(text[closeAt+len("): "):])
	}
	if rest, ok := strings.CutPrefix(text, dovecotQuery); ok {
		return startsWithClientError(rest)
	}
	return false
}

func isAuthWorker(segment string) bool {
	if strings.HasPrefix(segment, authWorker) && strings.HasSuffix(segment, ")") {
		digits := segment[len(authWorker) : len(segment)-1]
		if digits == "" {
			return false
		}
		for i := 0; i < len(digits); i++ {
			if digits[i] < '0' || digits[i] > '9' {
				return false
			}
		}
		return true
	}
	return false
}

func skipDriverSegment(text string) (string, bool) {
	if rest, ok := strings.CutPrefix(text, driverMysql); ok {
		return rest, true
	}
	open := 0
	switch {
	case strings.HasPrefix(text, driverMysqlOpen):
		open = len(driverMysqlOpen)
	case strings.HasPrefix(text, driverSQLPool):
		open = len(driverSQLPool)
	default:
		return text, false
	}
	closeAt := strings.IndexByte(text[open:], ')')
	if closeAt <= 0 || strings.IndexByte(text[open:open+closeAt], ' ') >= 0 {
		return text, false
	}
	next := open + closeAt + 1
	if rest, ok := strings.CutPrefix(text[next:], separator); ok {
		return rest, true
	}
	return text, false
}

func startsWithClientError(text string) bool {
	for _, e := range clientErrors {
		if strings.HasPrefix(text, e) {
			return true
		}
	}
	return false
}
