// Package syslogline parses the lines of syslog files the way the import does: a hand-written, linear port of its syslog line rules.
package syslogline

import (
	"bytes"
	"time"
	"unicode/utf8"
)

// Limits of the import's syslog line rules.
const (
	maxHostBytes     = 255
	maxProgramBytes  = 128
	maxPidDigits     = 10
	maxPriority      = 191
	maxPriorityDigit = 3
	priorityClasses  = 8
	traditionalLen   = 15
	rfc3339Len       = 19
	maxFractionDigit = 9
	tickDigits       = 7 // the fraction is kept to 100 ns
	tickNanoseconds  = 100
	maxOffsetHours   = 14
	replacementBytes = 3 // the UTF-8 length of U+FFFD, which an invalid byte becomes
)

var months = [...]string{"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"}

// Line is one parsed syslog line.
//
// The text fields are the bytes of the line as they are: the caller makes them valid UTF-8 and cuts them to the bounds of the record.
type Line struct {
	Time     time.Time // UTC; zero when the line has no header time
	Host     string
	Program  string
	PID      int32
	Priority *uint8
	Message  string
	Header   bool // the line has a syslog header
}

// stamp is the time of a header as written, before it is resolved to an instant.
type stamp struct {
	year, month, day     int // year is 0 for a traditional time
	hour, minute, second int
	nanos                int
	offset               int  // minutes east of UTC, RFC 3339 only
	traditional          bool // no year and no zone
}

// Parse parses line; clock supplies the year and the daylight-saving rules of a time without them, now is the time of reading.
//
// A line without a syslog header, or whose header time does not exist or lies outside the range the storage holds (the lines the
// import skips), is returned whole as the message with Header false, and does not change the clock.
func Parse(line []byte, clock *Clock, now time.Time) Line {
	raw := Line{Message: string(line)}
	pos := 0
	var priority *uint8
	if len(line) > 0 && line[0] == '<' {
		value, next, ok := parsePriority(line)
		if !ok {
			return raw
		}
		priority, pos = &value, next
	}
	var ts stamp
	var ok bool
	if pos < len(line) && isDigit(line[pos]) {
		ts, pos, ok = parseRFC3339(line, pos)
	} else {
		ts, pos, ok = parseTraditional(line, pos)
	}
	if !ok || pos >= len(line) || line[pos] != ' ' {
		return raw
	}
	pos++
	hostLen := bytes.IndexByte(line[pos:], ' ')
	if hostLen <= 0 || hostLen > maxHostBytes || encodedLen(line[pos:pos+hostLen]) > maxHostBytes {
		return raw
	}
	instant, ok := resolve(ts, clock, now)
	if !ok {
		return raw
	}
	program, pid, message := parseTag(line, pos+hostLen+1)
	return Line{
		Time:     instant,
		Host:     string(line[pos : pos+hostLen]),
		Program:  program,
		PID:      pid,
		Priority: priority,
		Message:  message,
		Header:   true,
	}
}

// resolve turns the time of a header into an instant; false means the time does not exist or is not storable.
func resolve(ts stamp, clock *Clock, now time.Time) (time.Time, bool) {
	if ts.traditional {
		if clock == nil {
			return time.Time{}, false
		}
		return clock.resolve(ts, now)
	}
	return resolveAbsolute(ts)
}

// parsePriority reads <PRI> at the start of line: one to three digits, 0 to 191; the value is the number modulo 8.
func parsePriority(line []byte) (value uint8, next int, ok bool) {
	index, number := 1, 0
	for index < len(line) && isDigit(line[index]) && index <= maxPriorityDigit {
		number = number*10 + int(line[index]-'0')
		index++
	}
	if index == 1 || index >= len(line) || line[index] != '>' || number > maxPriority {
		return 0, 0, false
	}
	return uint8(number % priorityClasses), index + 1, true // #nosec G115 -- the remainder of a division by 8
}

// parseTraditional reads "Mmm dd HH:MM:SS": a month name, a day with a leading space or zero and a time of day.
func parseTraditional(line []byte, pos int) (stamp, int, bool) {
	if len(line)-pos < traditionalLen || line[pos+3] != ' ' || line[pos+6] != ' ' {
		return stamp{}, 0, false
	}
	month := 0
	for i, name := range months {
		if string(line[pos:pos+3]) == name {
			month = i + 1
			break
		}
	}
	var day int
	if line[pos+4] == ' ' {
		day = digits(line, pos+5, 1)
	} else {
		day = digits(line, pos+4, 2)
	}
	hour, minute, second, ok := parseClock(line, pos+7)
	if month == 0 || day < 1 || day > 31 || !ok {
		return stamp{}, 0, false
	}
	return stamp{month: month, day: day, hour: hour, minute: minute, second: second, traditional: true}, pos + traditionalLen, true
}

// parseRFC3339 reads YYYY-MM-DDTHH:MM:SS, an optional fraction of 1 to 9 digits and Z or an offset of up to 14 hours.
func parseRFC3339(line []byte, pos int) (stamp, int, bool) {
	if len(line)-pos < rfc3339Len+1 || line[pos+4] != '-' || line[pos+7] != '-' || line[pos+10] != 'T' {
		return stamp{}, 0, false
	}
	year := digits(line, pos, 4)
	month := digits(line, pos+5, 2)
	day := digits(line, pos+8, 2)
	hour, minute, second, ok := parseClock(line, pos+11)
	if year < 0 || month < 1 || month > 12 || day < 1 || day > 31 || !ok {
		return stamp{}, 0, false
	}
	index := pos + rfc3339Len
	nanos, index, ok := parseFraction(line, index)
	if !ok {
		return stamp{}, 0, false
	}
	offset, index, ok := parseOffset(line, index)
	if !ok {
		return stamp{}, 0, false
	}
	return stamp{year: year, month: month, day: day, hour: hour, minute: minute, second: second, nanos: nanos, offset: offset}, index, true
}

// parseClock reads HH:MM:SS at start.
func parseClock(line []byte, start int) (hour, minute, second int, ok bool) {
	if line[start+2] != ':' || line[start+5] != ':' {
		return 0, 0, 0, false
	}
	hour, minute, second = digits(line, start, 2), digits(line, start+3, 2), digits(line, start+6, 2)
	if hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 {
		return 0, 0, 0, false
	}
	return hour, minute, second, true
}

// parseFraction reads an optional fraction: a point and 1 to 9 digits, kept to 100 ns.
func parseFraction(line []byte, index int) (nanos, next int, ok bool) {
	if index >= len(line) || line[index] != '.' {
		return 0, index, true
	}
	start := index + 1
	end := start
	ticks := 0
	for end < len(line) && isDigit(line[end]) && end-start < maxFractionDigit+1 {
		if end-start < tickDigits {
			ticks = ticks*10 + int(line[end]-'0')
		}
		end++
	}
	count := end - start
	if count < 1 || count > maxFractionDigit {
		return 0, 0, false
	}
	for padding := count; padding < tickDigits; padding++ {
		ticks *= 10
	}
	return ticks * tickNanoseconds, end, true
}

// parseOffset reads Z or +HH:MM / -HH:MM with an offset of at most 14:00; the result is in minutes.
func parseOffset(line []byte, index int) (minutes, next int, ok bool) {
	if index >= len(line) {
		return 0, 0, false
	}
	if line[index] == 'Z' {
		return 0, index + 1, true
	}
	if (line[index] != '+' && line[index] != '-') || len(line)-index < 6 || line[index+3] != ':' {
		return 0, 0, false
	}
	h, m := digits(line, index+1, 2), digits(line, index+4, 2)
	if h < 0 || h > maxOffsetHours || m < 0 || m > 59 || (h == maxOffsetHours && m != 0) {
		return 0, 0, false
	}
	minutes = h*60 + m
	if line[index] == '-' {
		minutes = -minutes
	}
	return minutes, index + 6, true
}

// parseTag reads the tag "PROGRAM[PID]:" or "PROGRAM:" at start and the message after it; a line without a tag is all message.
func parseTag(line []byte, start int) (program string, pid int32, message string) {
	index, width := start, 0
	for index < len(line) && width <= maxProgramBytes {
		r, size := utf8.DecodeRune(line[index:])
		if r == ' ' || r == '[' || r == ']' || r == ':' {
			break
		}
		if r == utf8.RuneError && size <= 1 {
			size, width = 1, width+replacementBytes
		} else {
			width += size
		}
		index += size
	}
	messageStart := -1
	if index > start && width <= maxProgramBytes && index < len(line) {
		if line[index] == ':' {
			messageStart = index + 1
		} else {
			messageStart, pid = parsePid(line, index)
		}
	}
	if messageStart < 0 {
		return "", 0, string(line[start:])
	}
	if messageStart < len(line) && line[messageStart] == ' ' {
		messageStart++
	}
	return string(line[start:index]), pid, string(line[messageStart:])
}

// parsePid reads "[PID]:" at bracket; the process ID is 0 above 2,147,483,647. The position behind the colon is -1 for a line without
// such a tag.
func parsePid(line []byte, bracket int) (next int, pid int32) {
	if line[bracket] != '[' {
		return -1, 0
	}
	var value int64
	index := bracket + 1
	for index < len(line) && isDigit(line[index]) && index-bracket <= maxPidDigits {
		value = value*10 + int64(line[index]-'0')
		index++
	}
	if index == bracket+1 || index+1 >= len(line) || line[index] != ']' || line[index+1] != ':' {
		return -1, 0
	}
	if value <= 1<<31-1 {
		pid = int32(value) // #nosec G115 -- bounded by the check above
	}
	return index + 2, pid
}

// encodedLen returns the length of b in UTF-8 bytes once every invalid byte has become U+FFFD.
func encodedLen(b []byte) int {
	n := 0
	for i := 0; i < len(b); {
		r, size := utf8.DecodeRune(b[i:])
		if r == utf8.RuneError && size <= 1 {
			n += replacementBytes
			size = 1
		} else {
			n += size
		}
		i += size
	}
	return n
}

// digits reads count ASCII digits at start; -1 when there are fewer or one is not a digit.
func digits(line []byte, start, count int) int {
	if start < 0 || start+count > len(line) {
		return -1
	}
	n := 0
	for _, c := range line[start : start+count] {
		if !isDigit(c) {
			return -1
		}
		n = n*10 + int(c-'0')
	}
	return n
}

func isDigit(c byte) bool { return c >= '0' && c <= '9' }
