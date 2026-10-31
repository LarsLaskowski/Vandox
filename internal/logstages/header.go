package logstages

import "strings"

// Levels of a MariaDB error log header.
const (
	levelError   = "ERROR"
	levelWarning = "Warning"
	levelNote    = "Note"
)

// Events of the MariaDB error log.
const (
	eventMariaStart            = "mariadb.start"
	eventMariaReady            = "mariadb.ready"
	eventMariaShutdown         = "mariadb.shutdown"
	eventMariaShutdownComplete = "mariadb.shutdown_complete"
	eventMariaAbort            = "mariadb.abort"
	eventMariaRecoveryStart    = "mariadb.recovery_start"
	eventMariaRecoveryEnd      = "mariadb.recovery_end"
)

const (
	longDateLength  = 10
	shortDateLength = 6
	clockLength     = 10
	maxThreadDigits = 20
	maxHexDigits    = 16

	startPrefix          = "Starting MariaDB "
	startInfix           = " as process "
	oldStartServer       = " (server "
	oldStartProcess      = ") starting as process "
	oldStartSuffix       = " ..."
	readySuffix          = ": ready for connections."
	shutdownSuffix       = ": Normal shutdown"
	shutdownDoneSuffix   = ": Shutdown complete"
	signalInfix          = " got signal "
	signalSuffix         = " ;"
	maxSignalDigits      = 3
	recoveryPrefix       = "InnoDB: Starting crash recovery"
	tableRecoveryStart   = "Starting table crash recovery..."
	tableRecoveryEnd     = "Crash table recovery finished."
	innoDBPrefix         = "InnoDB: "
	startedInfix         = " started; log sequence number "
	startScriptPrefix    = "mysqld_safe "
	priorityNoteLevel    = 6
	priorityErrorLevel   = 3
	priorityWarningLevel = 4
)

// mariaHeader is the parsed header of an entry of the MariaDB error log.
type mariaHeader struct {
	level   string
	message string
}

// priority returns the syslog priority of the level and false for a header without a known level.
func (h *mariaHeader) priority() (uint8, bool) {
	switch h.level {
	case levelError:
		return priorityErrorLevel, true
	case levelWarning:
		return priorityWarningLevel, true
	case levelNote:
		return priorityNoteLevel, true
	}

	return 0, false
}

func isDigit(b byte) bool { return b >= '0' && b <= '9' }

func allDigits(s string) bool {
	for i := 0; i < len(s); i++ {
		if !isDigit(s[i]) {
			return false
		}
	}

	return true
}

// parseHeader parses the header of an entry; nil for a continuation line.
func parseHeader(line string) *mariaHeader {
	var dateLen int

	switch {
	case len(line) >= longDateLength && allDigits(line[:4]) && line[4] == '-' && allDigits(line[5:7]) && line[7] == '-' && allDigits(line[8:10]):
		dateLen = longDateLength
	case len(line) >= shortDateLength && allDigits(line[:shortDateLength]):
		dateLen = shortDateLength
	default:
		return nil
	}

	rest := line[dateLen:]
	if len(rest) < clockLength || rest[0] != ' ' || rest[3] != ':' || rest[6] != ':' || rest[9] != ' ' {
		return nil
	}

	hourOK := (rest[1] == ' ' && isDigit(rest[2])) || (isDigit(rest[1]) && isDigit(rest[2]))
	if !hourOK || !allDigits(rest[4:6]) || !allDigits(rest[7:9]) {
		return nil
	}

	rest = rest[clockLength:]
	if dateLen == longDateLength {
		return parseServer(rest)
	}

	return parseShort(rest)
}

func parseServer(rest string) *mariaHeader {
	threadEnd := 0
	for threadEnd < len(rest) && isDigit(rest[threadEnd]) {
		threadEnd++
	}

	if threadEnd >= len(rest) {
		return nil
	}

	if threadEnd >= 1 && threadEnd <= maxThreadDigits && rest[threadEnd] == ' ' {
		return parseLevel(rest[threadEnd+1:])
	}

	return parseInnoDB(rest)
}

func parseInnoDB(rest string) *mariaHeader {
	if len(rest) < 3 || rest[0] != '0' || rest[1] != 'x' {
		return nil
	}

	hex := rest[2:]
	hexEnd := 0

	for hexEnd < len(hex) && ((hex[hexEnd] >= '0' && hex[hexEnd] <= '9') || (hex[hexEnd] >= 'a' && hex[hexEnd] <= 'f')) {
		hexEnd++
	}

	if hexEnd < 1 || hexEnd > maxHexDigits || hexEnd >= len(hex) || hex[hexEnd] != ' ' {
		return nil
	}

	after := strings.TrimLeft(hex[hexEnd:], " ")

	return &mariaHeader{message: after}
}

func parseShort(rest string) *mariaHeader {
	if after, ok := strings.CutPrefix(rest, startScriptPrefix); ok {
		return &mariaHeader{message: after}
	}

	return parseLevel(rest)
}

func parseLevel(span string) *mariaHeader {
	var level string

	switch {
	case strings.HasPrefix(span, "[ERROR]"):
		level = levelError
	case strings.HasPrefix(span, "[Warning]"):
		level = levelWarning
	case strings.HasPrefix(span, "[Note]"):
		level = levelNote
	default:
		return nil
	}

	after := span[len(level)+2:]
	if after == "" {
		return &mariaHeader{level: level}
	}

	if after[0] != ' ' {
		return nil
	}

	return &mariaHeader{level: level, message: after[1:]}
}

// classifyHeader returns the event of a header; recoveryOpen tracks an open crash recovery in call order.
func classifyHeader(h *mariaHeader, recoveryOpen *bool) string {
	switch h.level {
	case levelNote:
		return classifyNote(h.message, recoveryOpen)
	case levelError:
		if isAbort(h.message) {
			return eventMariaAbort
		}
	}

	return ""
}

func classifyNote(message string, recoveryOpen *bool) string {
	if isStart(message) {
		*recoveryOpen = false

		return eventMariaStart
	}

	if strings.HasPrefix(message, recoveryPrefix) || message == tableRecoveryStart {
		*recoveryOpen = true

		return eventMariaRecoveryStart
	}

	if message == tableRecoveryEnd || (*recoveryOpen && isInnoDBStarted(message)) {
		*recoveryOpen = false

		return eventMariaRecoveryEnd
	}

	switch {
	case strings.HasSuffix(message, readySuffix):
		return eventMariaReady
	case strings.HasSuffix(message, shutdownSuffix):
		return eventMariaShutdown
	case strings.HasSuffix(message, shutdownDoneSuffix):
		return eventMariaShutdownComplete
	}

	return ""
}

func isStart(message string) bool {
	if strings.HasPrefix(message, startPrefix) {
		return strings.Contains(message, startInfix)
	}

	server := strings.Index(message, oldStartServer)
	if server >= 1 && strings.HasSuffix(message, oldStartSuffix) {
		return strings.Contains(message[server+len(oldStartServer):], oldStartProcess)
	}

	return false
}

func isAbort(message string) bool {
	body, ok := strings.CutSuffix(message, signalSuffix)
	if !ok {
		return false
	}

	infix := strings.LastIndex(body, signalInfix)
	if infix < 1 {
		return false
	}

	digits := body[infix+len(signalInfix):]

	return len(digits) >= 1 && len(digits) <= maxSignalDigits && allDigits(digits)
}

func isInnoDBStarted(message string) bool {
	return strings.HasPrefix(message, innoDBPrefix) && strings.Contains(message, startedInfix)
}
