package model

// LogLine is one line of a log.
type LogLine struct {
	Log       string `json:"log"`
	Host      string `json:"host,omitempty"`
	Program   string `json:"program,omitempty"`
	PID       int32  `json:"pid,omitempty"`
	Priority  *uint8 `json:"priority,omitempty"`
	Message   string `json:"message"`
	Truncated bool   `json:"truncated,omitempty"`
}

// Kind returns KindLogLine.
func (l *LogLine) Kind() Kind { return KindLogLine }

// Validate checks the payload.
func (l *LogLine) Validate() error {
	if l == nil {
		return nilReceiver()
	}
	if err := checkRequiredShort("log", l.Log); err != nil {
		return err
	}
	if err := checkShort("program", l.Program); err != nil {
		return err
	}
	if l.PID < 0 {
		return invalid("pid", "must not be negative")
	}
	if l.Priority != nil && *l.Priority > 7 {
		return invalid("priority", "must be at most 7")
	}
	return checkText("message", l.Message)
}
