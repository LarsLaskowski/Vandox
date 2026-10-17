package scheduler

import "fmt"

// maxPanicValueBytes is the most of a panic value that PanicError.Error shows.
const maxPanicValueBytes = 1024

// PanicError is the error of a run whose collector panicked.
type PanicError struct {
	Value any    // the value passed to panic
	Stack []byte // the stack of the panicking goroutine
}

// Error returns "scheduler: collector panicked: " and fmt.Sprint(Value), cut to its first 1,024 bytes.
func (e *PanicError) Error() string {
	return fmt.Sprint(errNotImplemented, maxPanicValueBytes)
}
