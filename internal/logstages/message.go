package logstages

import (
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

// cutText cuts a text at a character boundary to at most limit bytes.
func cutText(s string, limit int) (string, bool) {
	if len(s) <= limit {
		return s, false
	}

	if limit <= 0 {
		return "", true
	}

	end := limit
	for end > 0 && !utf8.RuneStart(s[end]) {
		end--
	}

	return s[:end], true
}

// message is the message of one MariaDB entry: the header message and the joined continuation lines, bounded in size.
// Empty lines are held back until a line follows them, so trailing ones are dropped.
type message struct {
	text         []byte
	truncated    bool
	overflow     bool
	pendingEmpty int64
}

func newMessage(first string, truncated bool) *message {
	m := &message{truncated: truncated}

	whole, cut := cutText(first, model.MaxTextBytes)
	m.text = append(m.text, whole...)

	if cut {
		m.truncated = true
		m.overflow = true
	}

	return m
}

func (m *message) clone() *message {
	c := *m
	c.text = append([]byte(nil), m.text...)

	return &c
}

// tryAdd adds a continuation line when the message with it, and with the empty lines held back before it, stays within
// the text limit; an empty line is held back and always taken.
func (m *message) tryAdd(line string, truncated bool) bool {
	if line == "" {
		m.truncated = m.truncated || truncated
		m.pendingEmpty++

		return true
	}

	if m.overflow || int64(len(m.text))+m.pendingEmpty+1+int64(len(line)) > model.MaxTextBytes {
		return false
	}

	m.truncated = m.truncated || truncated

	for ; m.pendingEmpty > 0; m.pendingEmpty-- {
		m.text = append(m.text, '\n')
	}

	m.text = append(m.text, '\n')
	m.text = append(m.text, line...)

	return true
}

// build returns the message of the entry and whether any text was cut.
func (m *message) build() (string, bool) {
	return string(m.text), m.truncated
}
