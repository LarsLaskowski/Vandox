package config

// LogShipping configures the log shipping collector.
type LogShipping struct {
	Journal     bool       `yaml:"journal"`
	MinPriority string     `yaml:"min_priority"`
	Files       []LogFile  `yaml:"files"`
	Drop        []DropRule `yaml:"drop"`
}

// LogFile is a log file to ship.
type LogFile struct {
	Path   string `yaml:"path"`
	Format string `yaml:"format"`
}

// DropRule drops the lines that match all of its conditions.
type DropRule struct {
	Log      string `yaml:"log"`
	Program  string `yaml:"program"`
	Priority string `yaml:"priority"`
	Message  string `yaml:"message"`
}

// Bounds of log_shipping.
const (
	MaxLogFiles          = 16
	MaxDropRules         = 64
	MaxPatternBytes      = 1024
	MaxPatternInsts      = 400
	MaxPatternInstsTotal = 400
)

// PriorityLevel returns the syslog priority (0 to 7) named name and whether name is one.
func PriorityLevel(name string) (uint8, bool) {
	return 0, false
}
