package logparse

import (
	"errors"
	"fmt"
	"regexp"
)

// ErrInvalidParser is wrapped by the errors of NewRegistry and CheckType.
var ErrInvalidParser = errors.New("logparse: invalid parser")

var typePattern = regexp.MustCompile(`^[a-z][a-z0-9._-]{0,63}$`)

// CheckType returns nil when t matches ^[a-z][a-z0-9._-]{0,63}$, else an error wrapping ErrInvalidParser.
func CheckType(t string) error {
	if !typePattern.MatchString(t) {
		return fmt.Errorf("%w: type %q must match %s", ErrInvalidParser, t, typePattern)
	}
	return nil
}

// Registry picks the parser for a file. The zero value is not usable; use NewRegistry.
type Registry struct {
	parsers []Parser
}

// NewRegistry returns a registry of parsers in priority order (on equal confidence the earlier one wins).
func NewRegistry(parsers ...Parser) (*Registry, error) {
	seen := make(map[string]bool, len(parsers))
	for i, p := range parsers {
		if p == nil {
			return nil, fmt.Errorf("%w: parser %d is nil", ErrInvalidParser, i)
		}
		t := p.Type()
		if err := CheckType(t); err != nil {
			return nil, err
		}
		if seen[t] {
			return nil, fmt.Errorf("%w: type %q registered twice", ErrInvalidParser, t)
		}
		seen[t] = true
	}
	return &Registry{parsers: append([]Parser(nil), parsers...)}, nil
}

// Detect returns the parser with the highest confidence for f and head, or nil and NoMatch.
func (r *Registry) Detect(f File, head []byte) (Parser, Confidence) {
	var best Parser
	bestConf := NoMatch
	for _, p := range r.parsers {
		if c := p.Detect(f, head); c > bestConf {
			best, bestConf = p, c
		}
	}
	return best, bestConf
}

// Types returns the parser types in registration order.
func (r *Registry) Types() []string {
	types := make([]string, len(r.parsers))
	for i, p := range r.parsers {
		types[i] = p.Type()
	}
	return types
}
