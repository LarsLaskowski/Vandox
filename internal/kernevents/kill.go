package kernevents

import (
	"bytes"
	"math"
	"strconv"
	"strings"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/procstats"
)

const (
	// maxPIDDigits is the most digits of a victim PID (int32 has ten).
	maxPIDDigits = 10
	// maxRSSDigits is the most digits of an anon-rss value.
	maxRSSDigits = 20
	// maxScoreDigits is the most digits of an oom_score_adj value.
	maxScoreDigits = 4
	// maxScoreAdj bounds oom_score_adj in both directions.
	maxScoreAdj = 1000
	// commandLimit is the longest victim command kept, in bytes.
	commandLimit = 1024
	// unnamedCommand stands for an empty process name.
	unnamedCommand = "(unnamed)"

	tokenRSS   = "anon-rss:"
	tokenScore = "oom_score_adj:"
	unitKB     = "kB"
	commEnd    = ") total-vm:"
)

// killPrefixes are the kill line forms of the kernel (mm/oom_kill.c, mm/memcontrol.c).
var killPrefixes = [][]byte{
	[]byte("Out of memory: Killed process "),
	[]byte("Memory cgroup out of memory: Killed process "),
	[]byte("Out of memory (oom_kill_allocating_task): Killed process "),
}

// parseKill parses the message of an OOM kill line. The process name is text the process chose, so it is taken up to the last
// ") total-vm:" and the values are read only from the tokens after it.
func parseKill(message []byte) (model.OOMKill, bool) {
	rest, ok := cutKillPrefix(message)
	if !ok {
		return model.OOMKill{}, false
	}
	pid, rest, ok := cutPID(rest)
	if !ok {
		return model.OOMKill{}, false
	}
	end := bytes.LastIndex(rest, []byte(commEnd))
	if end < 0 {
		return model.OOMKill{}, false
	}
	comm := string(rest[:end])
	if comm == "" {
		comm = unnamedCommand
	}
	comm, _ = procstats.CleanText(comm, commandLimit)

	kill := model.OOMKill{VictimPID: pid, VictimCommand: comm}
	kill.AnonRSSBytes, kill.OOMScoreAdj = parseTail(string(rest[end+len(commEnd):]))
	return kill, true
}

// cutKillPrefix removes one of the kill line prefixes.
func cutKillPrefix(message []byte) ([]byte, bool) {
	for _, p := range killPrefixes {
		if rest, ok := bytes.CutPrefix(message, p); ok {
			return rest, true
		}
	}
	return nil, false
}

// cutPID reads the PID (1 to 10 digits, 1 to 2147483647) and the " (" after it.
func cutPID(b []byte) (int32, []byte, bool) {
	n := 0
	for n < len(b) && b[n] >= '0' && b[n] <= '9' {
		n++
	}
	if n == 0 || n > maxPIDDigits {
		return 0, nil, false
	}
	rest, ok := bytes.CutPrefix(b[n:], []byte(" ("))
	if !ok {
		return 0, nil, false
	}
	v, err := strconv.ParseInt(string(b[:n]), 10, 32)
	if err != nil || v < 1 {
		return 0, nil, false
	}
	return int32(v), rest, true
}

// parseTail reads anon-rss and oom_score_adj from the text after "total-vm:". A value that is missing, malformed, out of range or
// named twice is absent.
func parseTail(tail string) (*uint64, *int16) {
	var rssTokens, scoreTokens int
	var rss, score string
	for _, tok := range strings.Split(strings.ReplaceAll(tail, ", ", " "), " ") {
		switch {
		case strings.HasPrefix(tok, tokenRSS):
			rssTokens++
			rss = tok[len(tokenRSS):]
		case strings.HasPrefix(tok, tokenScore):
			scoreTokens++
			score = tok[len(tokenScore):]
		}
	}
	var rssBytes *uint64
	var adj *int16
	if rssTokens == 1 {
		rssBytes = parseRSS(rss)
	}
	if scoreTokens == 1 {
		adj = parseScore(score)
	}
	return rssBytes, adj
}

// parseRSS reads "<1-20 digits>kB" as bytes; an overflow is absent.
func parseRSS(v string) *uint64 {
	digits, ok := strings.CutSuffix(v, unitKB)
	if !ok || len(digits) == 0 || len(digits) > maxRSSDigits || !allDigits(digits) {
		return nil
	}
	kb, err := strconv.ParseUint(digits, 10, 64)
	if err != nil || kb > math.MaxUint64/1024 {
		return nil
	}
	bytesValue := kb * 1024
	return &bytesValue
}

// parseScore reads "-?<1-4 digits>" in the range -1000 to 1000.
func parseScore(v string) *int16 {
	digits := strings.TrimPrefix(v, "-")
	if len(digits) == 0 || len(digits) > maxScoreDigits || !allDigits(digits) {
		return nil
	}
	n, err := strconv.ParseInt(v, 10, 16)
	if err != nil || n < -maxScoreAdj || n > maxScoreAdj {
		return nil
	}
	adj := int16(n)
	return &adj
}

// allDigits reports whether s consists of ASCII digits only.
func allDigits(s string) bool {
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}
