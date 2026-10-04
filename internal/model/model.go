// Package model defines the validated records shared by the agent, the backend and the log importer.
package model

import (
	"errors"
	"maps"
	"math"
	"net/netip"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"
)

// Bounds shared by all record types.
const (
	// MaxNameBytes is the maximum length of a name (source, metric name, label key, collector).
	MaxNameBytes = 128
	// MaxShortTextBytes is the maximum length of a short text.
	MaxShortTextBytes = 1024
	// MaxTextBytes is the maximum length of a text.
	MaxTextBytes = 16384
	// MaxItems is the maximum number of entries of a list or map.
	MaxItems = 4096
	// MaxLabels is the maximum number of labels of a metric.
	MaxLabels = 32
)

// Kind names the type of a record's payload on the wire.
type Kind string

// Record kinds.
const (
	KindMetric             Kind = "metric"
	KindProcessSnapshot    Kind = "process_snapshot"
	KindConnectionSnapshot Kind = "connection_snapshot"
	KindServiceState       Kind = "service_state"
	KindMariaDBStatus      Kind = "mariadb_status"
	KindKernelEvent        Kind = "kernel_event"
	KindLogLine            Kind = "log_line"
	KindGap                Kind = "gap"
)

// Origin tells who produced a record.
type Origin string

// Record origins.
const (
	OriginAgent   Origin = "agent"
	OriginImport  Origin = "import"
	OriginBackend Origin = "backend"
)

// ErrInvalid is matched by every *FieldError.
var ErrInvalid = errors.New("invalid record")

// FieldError reports the field of a record that violates a rule.
type FieldError struct {
	Field  string
	Reason string
}

// Error returns "model: <Field>: <Reason>", or "model: <Reason>" when Field is empty.
func (e *FieldError) Error() string {
	if e.Field == "" {
		return "model: " + e.Reason
	}
	return "model: " + e.Field + ": " + e.Reason
}

// Unwrap returns ErrInvalid.
func (e *FieldError) Unwrap() error { return ErrInvalid }

// QuoteName returns s for use in a field path or error message: cut to its first MaxNameBytes bytes,
// quoted with strconv.Quote, followed by "..." when it was cut.
func QuoteName(s string) string {
	if len(s) <= MaxNameBytes {
		return strconv.Quote(s)
	}
	return strconv.Quote(s[:MaxNameBytes]) + "..."
}

// Payload is the kind-specific content of a record.
type Payload interface {
	Kind() Kind
	Validate() error
}

// Meta is the metadata every record carries.
type Meta struct {
	Origin     Origin
	Source     string
	Seq        uint64
	CapturedAt time.Time
}

// Validate checks the metadata.
func (m *Meta) Validate() error {
	switch m.Origin {
	case OriginAgent, OriginImport, OriginBackend:
	default:
		return invalid("origin", "unknown origin")
	}
	if err := checkName("source", m.Source); err != nil {
		return err
	}
	if err := checkTime("captured_at", m.CapturedAt); err != nil {
		return err
	}
	if m.Origin == OriginAgent && m.Seq == 0 {
		return invalid("seq", "must be greater than 0 for origin agent")
	}
	if m.Origin != OriginAgent && m.Seq != 0 {
		return invalid("seq", "must be 0 unless origin is agent")
	}
	return nil
}

// Record is one captured fact: metadata plus a payload.
type Record struct {
	Meta
	Data Payload
}

// Kind returns the payload's kind, or "" when Data is nil.
func (r *Record) Kind() Kind {
	if r.Data == nil {
		return ""
	}
	return r.Data.Kind()
}

// Validate checks the metadata and the payload.
func (r *Record) Validate() error {
	if err := r.Meta.Validate(); err != nil {
		return err
	}
	if r.Data == nil {
		return invalid("data", "required")
	}
	if err := r.Data.Validate(); err != nil {
		return prefixed("data", err)
	}
	if g, ok := r.Data.(*Gap); ok && r.Origin == OriginAgent &&
		(g.Cause == GapSequenceMissing || g.Cause == GapNoData) {
		return invalid("data.cause", "not allowed for origin agent")
	}
	return nil
}

var (
	namePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._:/@+-]*$`)
	uuidPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
)

func invalid(field, reason string) error { return &FieldError{Field: field, Reason: reason} }

// prefixed prepends path to the field of a *FieldError; other errors are returned unchanged. An empty
// field becomes path itself.
func prefixed(path string, err error) error {
	var fe *FieldError
	if !errors.As(err, &fe) {
		return err
	}
	if fe.Field == "" {
		return &FieldError{Field: path, Reason: fe.Reason}
	}
	sep := "."
	if strings.HasPrefix(fe.Field, "[") {
		sep = ""
	}
	return &FieldError{Field: path + sep + fe.Field, Reason: fe.Reason}
}

func indexed(list string, i int) string { return list + "[" + strconv.Itoa(i) + "]" }

func keyed(field, key string) string { return field + "[" + QuoteName(key) + "]" }

// checkPattern requires s to be non-empty, at most limit bytes and to match re.
func checkPattern(field, s string, re *regexp.Regexp, limit int) error {
	switch {
	case s == "":
		return invalid(field, "required")
	case len(s) > limit:
		return invalid(field, "too long")
	case !re.MatchString(s):
		return invalid(field, "invalid characters")
	}
	return nil
}

func checkName(field, s string) error { return checkPattern(field, s, namePattern, MaxNameBytes) }

func checkOptionalName(field, s string) error {
	if s == "" {
		return nil
	}
	return checkName(field, s)
}

func checkUUID(field, s string) error { return checkPattern(field, s, uuidPattern, 36) }

func checkOptionalUUID(field, s string) error {
	if s == "" {
		return nil
	}
	return checkUUID(field, s)
}

func checkLen(field, s string, limit int) error {
	if len(s) > limit {
		return invalid(field, "too long")
	}
	return nil
}

func checkShort(field, s string) error { return checkLen(field, s, MaxShortTextBytes) }

func checkText(field, s string) error { return checkLen(field, s, MaxTextBytes) }

func checkRequiredShort(field, s string) error {
	if s == "" {
		return invalid(field, "required")
	}
	return checkShort(field, s)
}

func checkCount(field string, n int) error {
	if n > MaxItems {
		return invalid(field, "too many entries")
	}
	return nil
}

func checkTime(field string, t time.Time) error {
	if t.IsZero() {
		return invalid(field, "required")
	}
	if _, off := t.Zone(); off != 0 {
		return invalid(field, "must be UTC")
	}
	return nil
}

func checkOptionalTime(field string, t time.Time) error {
	if t.IsZero() {
		return nil
	}
	return checkTime(field, t)
}

func checkFinite(field string, f float64) error {
	if math.IsNaN(f) || math.IsInf(f, 0) {
		return invalid(field, "must be finite")
	}
	return nil
}

func checkRate(field string, f float64) error {
	if err := checkFinite(field, f); err != nil {
		return err
	}
	if f < 0 {
		return invalid(field, "must not be negative")
	}
	return nil
}

func checkOOMScoreAdj(field string, v *int16) error {
	if v != nil && (*v < -1000 || *v > 1000) {
		return invalid(field, "out of range")
	}
	return nil
}

func checkOneOf[T ~string](field string, v T, allowed ...T) error {
	for _, a := range allowed {
		if v == a {
			return nil
		}
	}
	return invalid(field, "unknown value")
}

func checkAddr(field string, a netip.Addr) error {
	if !a.IsValid() {
		return invalid(field, "invalid address")
	}
	if a.Zone() != "" {
		return invalid(field, "zone not allowed")
	}
	return nil
}

func checkAddrPort(field string, p netip.AddrPort) error {
	if !p.IsValid() {
		return invalid(field, "invalid address")
	}
	return checkAddr(field, p.Addr())
}

// nilReceiver is returned by a payload's Validate on a nil receiver.
func nilReceiver() error { return invalid("", "required") }

// sortedKeys returns the keys of m in order, so the first reported error is deterministic.
func sortedKeys[V any](m map[string]V) []string {
	return slices.Sorted(maps.Keys(m))
}
