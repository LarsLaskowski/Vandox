package logship

import "github.com/LarsLaskowski/Vandox/internal/model"

// filter decides which lines are not shipped: those less severe than the minimum priority and those that match a drop rule.
type filter struct {
	min   uint8
	rules []Rule
}

// newFilter returns the filter of the configuration.
func newFilter(cfg Config) *filter {
	return &filter{min: cfg.MinPriority, rules: cfg.Drop}
}

// drops reports whether the line is not shipped. A line without a priority is never dropped by the minimum or by a priority condition.
func (f *filter) drops(l *model.LogLine) bool {
	if l.Priority != nil && *l.Priority > f.min {
		return true
	}
	for i := range f.rules {
		if f.rules[i].matches(l) {
			return true
		}
	}
	return false
}

// matches reports whether the line meets every condition that is set; a rule without a condition matches nothing.
func (r *Rule) matches(l *model.LogLine) bool {
	if r.Log == "" && r.Program == "" && r.Priority == nil && r.Message == nil {
		return false
	}
	if r.Log != "" && r.Log != l.Log {
		return false
	}
	if r.Program != "" && r.Program != l.Program {
		return false
	}
	if r.Priority != nil && (l.Priority == nil || *l.Priority < *r.Priority) {
		return false
	}
	return r.Message == nil || r.Message.MatchString(l.Message)
}
