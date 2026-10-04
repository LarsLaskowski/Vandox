// Package model defines the validated records shared by the agent, the backend and the log importer.
package model

import (
	"errors"
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
func (e *FieldError) Error() string { return "" }

// Unwrap returns ErrInvalid.
func (e *FieldError) Unwrap() error { return nil }

// QuoteName returns s for use in a field path or error message: cut to its first MaxNameBytes bytes,
// quoted with strconv.Quote, followed by "..." when it was cut.
func QuoteName(s string) string { return "" }

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
func (m *Meta) Validate() error { return errors.New("not implemented") }

// Record is one captured fact: metadata plus a payload.
type Record struct {
	Meta
	Data Payload
}

// Kind returns the payload's kind, or "" when Data is nil.
func (r *Record) Kind() Kind { return "" }

// Validate checks the metadata and the payload.
func (r *Record) Validate() error { return errors.New("not implemented") }
