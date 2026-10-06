package logparse

import "errors"

// ErrInvalidParser is wrapped by the errors of NewRegistry and CheckType.
var ErrInvalidParser = errors.New("logparse: invalid parser")

// CheckType returns nil when t matches ^[a-z][a-z0-9._-]{0,63}$, else an error wrapping ErrInvalidParser.
func CheckType(t string) error {
	return errors.New("not implemented")
}

// Registry picks the parser for a file. The zero value is not usable; use NewRegistry.
type Registry struct {
	parsers []Parser
}

// NewRegistry returns a registry of parsers in priority order (on equal confidence the earlier one wins).
func NewRegistry(parsers ...Parser) (*Registry, error) {
	return nil, errors.New("not implemented")
}

// Detect returns the parser with the highest confidence for f and head, or nil and NoMatch.
func (r *Registry) Detect(f File, head []byte) (Parser, Confidence) {
	return nil, NoMatch
}

// Types returns the parser types in registration order.
func (r *Registry) Types() []string {
	return nil
}
