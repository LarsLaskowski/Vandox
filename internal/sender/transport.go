package sender

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/httptrace"
	"net/netip"
	"net/url"
	"syscall"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/config"
	"github.com/LarsLaskowski/Vandox/internal/version"
)

// Texts of Result.Reason.
const (
	reasonTimeout       = "timeout"
	reasonConnection    = "connection failed"
	reasonTLS           = "tls failed"
	reasonTokenRefused  = "token refused"
	reasonStatus        = "status"
	maxConnsPerHost     = 2
	idleConnTimeout     = 30 * time.Second
	errBackendURLText   = "sender: backend url: not usable"
	httpContentTypeGzip = "application/gzip"
)

// tailnetDevice is Tailscale's network interface on Linux; every connection to an address that is not a loopback address is bound to it.
const tailnetDevice = "tailscale0"

// errBackendURL is returned for a backend URL the transport cannot use; it never holds the URL.
var errBackendURL = errors.New(errBackendURLText)

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
	switch {
	case r.Refused:
		return Refused
	case r.Status >= http.StatusOK && r.Status < http.StatusMultipleChoices:
		return Acknowledged
	case r.Status == 0 && r.Reason == "":
		return Stopped
	default:
		return NotAccepting
	}
}

// BatchSender sends one encoded batch.
type BatchSender interface {
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
	// device is the interface every non-loopback connection is bound to; tailnetDevice, only tests change it.
	device string
}

// NewHTTPTransport returns the transport to the backend at baseURL, authenticated with token. The client uses no
// proxy, follows no redirect, verifies certificates against the system roots and bounds the answer's header.
func NewHTTPTransport(baseURL string, token config.Secret) (*HTTPTransport, error) {
	u, err := url.Parse(baseURL)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" || u.User != nil ||
		(u.Path != "" && u.Path != "/") || u.RawQuery != "" || u.Fragment != "" {
		return nil, errBackendURL
	}
	t := &HTTPTransport{url: u.Scheme + "://" + u.Host + IngestPath, token: token, device: tailnetDevice}
	t.client = &http.Client{
		Transport: &http.Transport{
			Proxy:                  nil,
			DialContext:            t.dialContext,
			TLSClientConfig:        &tls.Config{MinVersion: tls.VersionTLS12},
			MaxResponseHeaderBytes: MaxAnswerHeader,
			DisableCompression:     true,
			ForceAttemptHTTP2:      false,
			MaxConnsPerHost:        maxConnsPerHost,
			IdleConnTimeout:        idleConnTimeout,
		},
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}

	return t, nil
}

// dialContext dials with the test hook when one is set, else with a dialer that binds every non-loopback connection to the device.
func (t *HTTPTransport) dialContext(ctx context.Context, network, addr string) (net.Conn, error) {
	if t.dial != nil {
		return t.dial(ctx, network, addr)
	}
	d := net.Dialer{Control: bindControl(t.device)}

	return d.DialContext(ctx, network, addr)
}

// bindControl returns the dialer's Control function: it binds the socket to device before connect unless needsDevice(address) is false.
func bindControl(device string) func(network, address string, c syscall.RawConn) error {
	return func(_, address string, c syscall.RawConn) error {
		if !needsDevice(address) {
			return nil
		}
		var bindErr error
		if err := c.Control(func(fd uintptr) {
			bindErr = syscall.BindToDevice(int(fd), device) // #nosec G115 -- a descriptor of an open socket fits an int
		}); err != nil {
			return err
		}

		return bindErr
	}
}

// needsDevice reports whether a connection to address (host:port with an IP literal, as net.Dialer passes it) must be bound:
// false only for a loopback address; an address it cannot parse is bound.
func needsDevice(address string) bool {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return true
	}
	addr, err := netip.ParseAddr(host)
	if err != nil {
		return true
	}

	return !addr.Unmap().IsLoopback()
}

// String returns a fixed text, so no formatting of the transport shows its fields.
func (t *HTTPTransport) String() string { return "sender.HTTPTransport" }

// GoString returns the same fixed text.
func (t *HTTPTransport) GoString() string { return t.String() }

// Send sends body and returns what came back. The deadline is the caller's context's; no error text, URL or host
// leaves Send, and at most MaxAnswerBody bytes of the answer's body are read.
func (t *HTTPTransport) Send(ctx context.Context, body []byte) Result {
	// Send ends a connection whose body it will not read to the end by closing it itself: after the body is closed,
	// net/http would otherwise read on for up to 256 KiB of a chunked body to keep the connection, and cancelling the
	// request's context alone does not stop that reliably (the transport's read loop may see the closed body first).
	reqCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	var conn net.Conn
	reqCtx = httptrace.WithClientTrace(reqCtx, &httptrace.ClientTrace{GotConn: func(info httptrace.GotConnInfo) { conn = info.Conn }})
	req, err := http.NewRequestWithContext(reqCtx, http.MethodPost, t.url, bytes.NewReader(body))
	if err != nil {
		return Result{Reason: reasonConnection}
	}
	req.Header.Set("Authorization", "Bearer "+t.token.Value())
	req.Header.Set("Content-Type", httpContentTypeGzip)
	req.Header.Set("User-Agent", "vandox-agent/"+version.Version)
	resp, err := t.client.Do(req)
	if err != nil {
		return classifyFailure(ctx, err)
	}
	read, _ := io.Copy(io.Discard, io.LimitReader(resp.Body, MaxAnswerBody))
	if read >= MaxAnswerBody {
		if conn != nil {
			_ = conn.Close()
		}
		cancel()
	}
	_ = resp.Body.Close()

	return classifyAnswer(resp)
}

// classifyAnswer turns the status and the refusal marker of an answer into a Result. The marker is read with
// Header.Get and compared exactly (case-sensitive) with RefusedValue.
func classifyAnswer(resp *http.Response) Result {
	status := resp.StatusCode
	switch {
	case status >= http.StatusOK && status < http.StatusMultipleChoices:
		return Result{Status: status}
	case status == http.StatusUnauthorized || status == http.StatusForbidden:
		return Result{Status: status, Reason: reasonTokenRefused}
	case (status == http.StatusBadRequest || status == http.StatusRequestEntityTooLarge || status == http.StatusUnprocessableEntity) &&
		resp.Header.Get(RefusedHeader) == RefusedValue:
		return Result{Status: status, Refused: true}
	default:
		return Result{Status: status, Reason: reasonStatus}
	}
}

// classifyFailure maps the error of a request without an answer to a Result with a fixed reason.
func classifyFailure(ctx context.Context, err error) Result {
	if cause := ctx.Err(); cause != nil {
		if errors.Is(cause, context.DeadlineExceeded) {
			return Result{Reason: reasonTimeout}
		}

		return Result{}
	}
	var netErr net.Error
	if errors.Is(err, context.DeadlineExceeded) || (errors.As(err, &netErr) && netErr.Timeout()) {
		return Result{Reason: reasonTimeout}
	}
	if isTLSFailure(err) {
		return Result{Reason: reasonTLS}
	}

	return Result{Reason: reasonConnection}
}

// isTLSFailure reports whether err is a failed certificate check or a TLS alert.
func isTLSFailure(err error) bool {
	var verify *tls.CertificateVerificationError
	var unknown x509.UnknownAuthorityError
	var invalid x509.CertificateInvalidError
	var host x509.HostnameError
	var alert tls.AlertError

	return errors.As(err, &verify) || errors.As(err, &unknown) || errors.As(err, &invalid) ||
		errors.As(err, &host) || errors.As(err, &alert)
}
