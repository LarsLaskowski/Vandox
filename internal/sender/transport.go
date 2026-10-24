package sender

import (
	"context"
	"net"
	"net/http"

	"github.com/LarsLaskowski/Vandox/internal/config"
)

// Outcome is how an answer is treated.
type Outcome int

// The outcomes of an answer.
const (
	// Acknowledged is a 2xx answer.
	Acknowledged Outcome = iota
	// Refused is an answer with Result.Refused.
	Refused
	// NotAccepting is every other answer and every failure.
	NotAccepting
	// Stopped means the caller's context was cancelled.
	Stopped
)

// Result is what one request came back with.
type Result struct {
	// Status is the HTTP status; 0 without an answer.
	Status int
	// Reason is "", "timeout", "connection failed", "tls failed", "token refused" or "status".
	Reason string
	// Refused is true for 400, 413 or 422 with RefusedHeader: RefusedValue.
	Refused bool
}

// Outcome returns how the result is treated.
func (r Result) Outcome() Outcome {
	return NotAccepting
}

// Transport sends one encoded batch.
type Transport interface {
	// Send sends body and returns what came back.
	Send(ctx context.Context, body []byte) Result
}

// HTTPTransport sends batches to the backend over HTTP.
type HTTPTransport struct {
	client *http.Client
	url    string
	token  config.Secret
	// dial replaces the dialer; nil: net.Dialer. Only tests set it.
	dial func(ctx context.Context, network, addr string) (net.Conn, error)
}

// NewHTTPTransport returns the transport to the backend at baseURL, authenticated with token.
func NewHTTPTransport(baseURL string, token config.Secret) (*HTTPTransport, error) {
	return &HTTPTransport{url: baseURL + IngestPath, token: token}, nil
}

// Send sends body and returns what came back.
func (t *HTTPTransport) Send(ctx context.Context, body []byte) Result {
	return Result{Reason: "status"}
}
