// Package errorlogline parses the lines of the web server and panel error logs (Plesk panel, nginx, Apache) into syslog lines.
package errorlogline

import (
	"bytes"

	"github.com/LarsLaskowski/Vandox/internal/syslogline"
)

// Format names the layout of an error log.
type Format uint8

const (
	// PleskPanel is the layout of the Plesk panel log.
	PleskPanel Format = iota + 1
	// Nginx is the layout of the nginx error log.
	Nginx
	// Apache is the layout of the Apache error log.
	Apache
)

const (
	maxFractionDigits = 6
	maxPIDDigits      = 10
	maxRequestIDLen   = 64
	maxModuleLen      = 64
	yearDigits        = 4
	pairDigits        = 2
)

var (
	pleskLevels = [...]string{"EMERG", "ALERT", "CRIT", "ERR", "WARN", "NOTICE", "INFO", "DEBUG"}
	levels      = [...]string{"emerg", "alert", "crit", "error", "warn", "notice", "info", "debug"}
	weekdays    = [...]string{"Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"}
	months      = [...]string{"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"}
)

// stamp is the digits of a line's time, not yet range-checked.
type stamp struct {
	year, month, day     int
	hour, minute, second int
	nanos                int
}

// Parse parses one line of an error log in the given format, resolving its time with clock. A line that is not of the format, or whose
// time does not exist or cannot be stored, comes back as a line without a header whose message is the whole line.
func Parse(format Format, line []byte, clock *syslogline.Clock) syslogline.Line {
	var (
		ts       stamp
		priority int
		start    int
		ok       bool
	)
	switch format {
	case PleskPanel:
		ts, priority, start, ok = parsePlesk(line)
	case Nginx:
		ts, priority, start, ok = parseNginx(line)
	case Apache:
		ts, priority, start, ok = parseApache(line)
	}
	if ok {
		if instant, resolved := clock.ResolveLocal(ts.year, ts.month, ts.day, ts.hour, ts.minute, ts.second, ts.nanos); resolved {
			p := uint8(priority) // #nosec G115 -- a level index, 0 to 7
			return syslogline.Line{Header: true, Time: instant, Priority: &p, Message: string(line[start:])}
		}
	}
	return syslogline.Line{Message: string(line)}
}

// parsePlesk reads [YYYY-MM-DD HH:MM:SS(.D{1,6})] (PID:REQUESTID )LEVEL( message).
func parsePlesk(line []byte) (ts stamp, priority, start int, ok bool) {
	if len(line) == 0 || line[0] != '[' {
		return ts, 0, 0, false
	}
	pos := 1
	if ts, ok = dateTime(line, &pos, '-'); ok == false {
		return ts, 0, 0, false
	}
	if ts.nanos, ok = fraction(line, &pos, 1); ok == false {
		return ts, 0, 0, false
	}
	if bytes.HasPrefix(line[pos:], []byte("] ")) == false {
		return ts, 0, 0, false
	}
	pos += 2
	start = pos
	if pos < len(line) && isDigit(line[pos]) && skipProcessAndRequest(line, &pos) == false {
		return ts, 0, 0, false
	}
	end := bytes.IndexByte(line[pos:], ' ')
	if end < 0 {
		end = len(line) - pos
	}
	priority = indexOf(pleskLevels[:], line[pos:pos+end])
	return ts, priority, start, priority >= 0
}

// skipProcessAndRequest skips PID:REQUESTID and the space behind it.
func skipProcessAndRequest(line []byte, pos *int) bool {
	digits := countWhile(line, *pos, isDigit, maxPIDDigits+1)
	if digits < 1 || digits > maxPIDDigits {
		return false
	}
	*pos += digits
	if *pos >= len(line) || line[*pos] != ':' {
		return false
	}
	*pos++
	length := countWhile(line, *pos, isLetterOrDigit, maxRequestIDLen+1)
	if length < 1 || length > maxRequestIDLen {
		return false
	}
	*pos += length
	if *pos >= len(line) || line[*pos] != ' ' {
		return false
	}
	*pos++
	return true
}

// parseNginx reads YYYY/MM/DD HH:MM:SS [level]( message).
func parseNginx(line []byte) (ts stamp, priority, start int, ok bool) {
	pos := 0
	if ts, ok = dateTime(line, &pos, '/'); ok == false {
		return ts, 0, 0, false
	}
	if bytes.HasPrefix(line[pos:], []byte(" [")) == false {
		return ts, 0, 0, false
	}
	start = pos + 1
	pos += 2
	priority = bracketLevel(line, &pos, false)
	return ts, priority, start, priority >= 0
}

// parseApache reads [Www Mmm DD HH:MM:SS(.uuuuuu) YYYY] [(module:)level]( message).
func parseApache(line []byte) (ts stamp, priority, start int, ok bool) {
	if len(line) == 0 || line[0] != '[' {
		return ts, 0, 0, false
	}
	pos := 1
	if ts, ok = apacheTime(line, &pos); ok == false {
		return ts, 0, 0, false
	}
	if bytes.HasPrefix(line[pos:], []byte("] [")) == false {
		return ts, 0, 0, false
	}
	start = pos + 2
	pos += 3
	length := countWhile(line, pos, isModuleChar, maxModuleLen+1)
	if length > maxModuleLen || pos+length >= len(line) {
		return ts, 0, 0, false
	}
	if line[pos+length] == ':' {
		pos += length + 1
	}
	priority = bracketLevel(line, &pos, true)
	return ts, priority, start, priority >= 0
}

// apacheTime reads Www Mmm DD HH:MM:SS(.uuuuuu) YYYY, up to the closing bracket.
func apacheTime(line []byte, pos *int) (ts stamp, ok bool) {
	if name(line, pos, weekdays[:]) < 0 || expect(line, pos, ' ') == false {
		return ts, false
	}
	month := name(line, pos, months[:])
	if month < 0 || expect(line, pos, ' ') == false {
		return ts, false
	}
	ts.month = month + 1
	if ts.day, ok = number(line, pos, pairDigits); ok == false || expect(line, pos, ' ') == false {
		return ts, false
	}
	if ts.hour, ts.minute, ts.second, ok = clockTime(line, pos); ok == false {
		return ts, false
	}
	if ts.nanos, ok = fraction(line, pos, maxFractionDigits); ok == false || expect(line, pos, ' ') == false {
		return ts, false
	}
	ts.year, ok = number(line, pos, yearDigits)
	return ts, ok
}

// dateTime reads YYYY-MM-DD HH:MM:SS or YYYY/MM/DD HH:MM:SS without a range check.
func dateTime(line []byte, pos *int, separator byte) (ts stamp, ok bool) {
	if ts.year, ok = number(line, pos, yearDigits); ok == false || expect(line, pos, separator) == false {
		return ts, false
	}
	if ts.month, ok = number(line, pos, pairDigits); ok == false || expect(line, pos, separator) == false {
		return ts, false
	}
	if ts.day, ok = number(line, pos, pairDigits); ok == false || expect(line, pos, ' ') == false {
		return ts, false
	}
	ts.hour, ts.minute, ts.second, ok = clockTime(line, pos)
	return ts, ok
}

// clockTime reads HH:MM:SS without a range check.
func clockTime(line []byte, pos *int) (hour, minute, second int, ok bool) {
	if hour, ok = number(line, pos, pairDigits); ok == false || expect(line, pos, ':') == false {
		return 0, 0, 0, false
	}
	if minute, ok = number(line, pos, pairDigits); ok == false || expect(line, pos, ':') == false {
		return 0, 0, 0, false
	}
	second, ok = number(line, pos, pairDigits)
	return hour, minute, second, ok
}

// fraction reads an optional fraction (a dot and minDigits to six digits) and returns it in nanoseconds; no fraction is 0.
func fraction(line []byte, pos *int, minDigits int) (nanos int, ok bool) {
	if *pos >= len(line) || line[*pos] != '.' {
		return 0, true
	}
	*pos++
	digits := countWhile(line, *pos, isDigit, maxFractionDigits)
	if digits < minDigits {
		return 0, false
	}
	value, ok := number(line, pos, digits)
	if ok == false {
		return 0, false
	}
	for missing := digits; missing < 9; missing++ {
		value *= 10
	}
	return value, true
}

// bracketLevel reads the level and the closing bracket of a level bracket and requires a space or the end of the line behind it. A
// trace level (trace1 to trace8) has priority 7 when allowTrace is set. It returns -1 for no level.
func bracketLevel(line []byte, pos *int, allowTrace bool) int {
	end := bytes.IndexByte(line[*pos:], ']')
	if end < 0 {
		return -1
	}
	text := line[*pos : *pos+end]
	priority := indexOf(levels[:], text)
	if priority < 0 && allowTrace && len(text) == 6 && string(text[:5]) == "trace" && text[5] >= '1' && text[5] <= '8' {
		priority = 7
	}
	*pos += end + 1
	if priority >= 0 && (*pos == len(line) || line[*pos] == ' ') {
		return priority
	}
	return -1
}

// indexOf returns the index of the name equal to text, or -1.
func indexOf(names []string, text []byte) int {
	for i, n := range names {
		if string(text) == n {
			return i
		}
	}
	return -1
}

// name reads a three-letter name of the list and returns its index, or -1.
func name(line []byte, pos *int, names []string) int {
	if *pos+3 > len(line) {
		return -1
	}
	i := indexOf(names, line[*pos:*pos+3])
	if i >= 0 {
		*pos += 3
	}
	return i
}

// number reads count ASCII digits.
func number(line []byte, pos *int, count int) (int, bool) {
	if count < 1 || *pos+count > len(line) {
		return 0, false
	}
	value := 0
	for i := 0; i < count; i++ {
		c := line[*pos+i]
		if isDigit(c) == false {
			return 0, false
		}
		value = value*10 + int(c-'0')
	}
	*pos += count
	return value, true
}

// expect consumes the byte c.
func expect(line []byte, pos *int, c byte) bool {
	if *pos < len(line) && line[*pos] == c {
		*pos++
		return true
	}
	return false
}

// countWhile counts the bytes from pos that satisfy pred, at most max.
func countWhile(line []byte, pos int, pred func(byte) bool, max int) int {
	count := 0
	for count < max && pos+count < len(line) && pred(line[pos+count]) {
		count++
	}
	return count
}

func isDigit(c byte) bool { return c >= '0' && c <= '9' }

func isLetterOrDigit(c byte) bool {
	return isDigit(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
}

func isModuleChar(c byte) bool { return isLetterOrDigit(c) || c == '_' }
