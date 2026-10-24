package sender

import (
	"bytes"
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/version"
)

// testBody stands in for an encoded batch: the transport sends bytes, it does not look into them.
var testBody = []byte("gzip-bytes-of-a-batch")

// newServer starts a loopback server whose errors stay out of the test log.
func newServer(t *testing.T, h http.Handler) *httptest.Server {
	t.Helper()

	srv := httptest.NewUnstartedServer(h)
	srv.Config.ErrorLog = log.New(io.Discard, "", 0)
	srv.Start()
	t.Cleanup(srv.Close)

	return srv
}

// newTLSServer starts a loopback TLS server with the self-signed certificate of httptest, which no system root trusts.
func newTLSServer(t *testing.T, h http.Handler) *httptest.Server {
	t.Helper()

	srv := httptest.NewUnstartedServer(h)
	srv.Config.ErrorLog = log.New(io.Discard, "", 0)
	srv.StartTLS()
	t.Cleanup(srv.Close)

	return srv
}

// rawServer accepts connections on a loopback port and hands each to serve; it returns the address.
func rawServer(t *testing.T, serve func(c net.Conn)) string {
	t.Helper()

	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("Listen() error = %v, want nil", err)
	}
	t.Cleanup(func() { _ = ln.Close() })
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() {
				defer func() { _ = c.Close() }()
				serve(c)
			}()
		}
	}()

	return ln.Addr().String()
}

// newTransport returns the transport to baseURL with the test token.
func newTransport(t *testing.T, baseURL string) *HTTPTransport {
	t.Helper()

	tr, err := NewHTTPTransport(baseURL, testSecret(t, testToken))
	if err != nil {
		t.Fatalf("NewHTTPTransport(%q) error = %v, want nil", baseURL, err)
	}

	return tr
}

// useDial makes every dial of the transport go through fn; only the package's tests do that.
func useDial(tr *HTTPTransport, fn func(ctx context.Context, network, addr string) (net.Conn, error)) {
	tr.dial = fn
	if tr.client == nil {
		return
	}
	if ht, ok := tr.client.Transport.(*http.Transport); ok {
		ht.DialContext = fn
	}
}

// send sends testBody with a deadline that never fires within a test.
func send(t *testing.T, tr *HTTPTransport) Result {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()

	return tr.Send(ctx, testBody)
}

// requireResult fails the test unless got is want.
func requireResult(t *testing.T, what string, got, want Result) {
	t.Helper()

	if got != want {
		t.Errorf("%s: Send() = %+v, want %+v", what, got, want)
	}
}

// requireOutcome fails the test unless the result's outcome is want.
func requireOutcome(t *testing.T, what string, res Result, want Outcome) {
	t.Helper()

	if got := res.Outcome(); got != want {
		t.Errorf("%s: %+v.Outcome() = %d, want %d", what, res, got, want)
	}
}

// statusHandler answers every request with status.
func statusHandler(status int) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		if status >= 300 && status < 400 {
			w.Header().Set("Location", "/elsewhere")
		}
		w.WriteHeader(status)
	})
}

// This test comes first on purpose: http.ProxyFromEnvironment reads the environment once per process, so a test that
// runs after something read it would not see the variables set here. The transport under test must never read them.
func TestHTTPTransport_IgnoresProxyEnvironment(t *testing.T) {
	const backend = "100.64.0.1:8081"
	var proxied atomic.Int32
	recorder := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		proxied.Add(1)
		w.WriteHeader(http.StatusBadGateway)
	}))
	var mu sync.Mutex
	var uris, hosts, methods []string
	origin := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		uris, hosts, methods = append(uris, r.RequestURI), append(hosts, r.Host), append(methods, r.Method)
		w.WriteHeader(http.StatusOK)
	}))
	for _, name := range []string{"HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy"} {
		t.Setenv(name, recorder.URL)
	}
	t.Setenv("NO_PROXY", "")
	t.Setenv("no_proxy", "")
	tr := newTransport(t, "http://"+backend)
	var dialed []string
	useDial(tr, func(ctx context.Context, _, addr string) (net.Conn, error) {
		mu.Lock()
		dialed = append(dialed, addr)
		mu.Unlock()
		var d net.Dialer

		return d.DialContext(ctx, "tcp", origin.Listener.Addr().String())
	})

	res := send(t, tr)

	requireResult(t, "answer of the origin server", res, Result{Status: 200})
	mu.Lock()
	defer mu.Unlock()
	if len(uris) != 1 || uris[0] != IngestPath || hosts[0] != backend || methods[0] != http.MethodPost {
		t.Errorf("the origin saw %v %v %v, want one POST %s for host %s in origin form", methods, uris, hosts, IngestPath, backend)
	}
	for _, addr := range dialed {
		if addr != backend {
			t.Errorf("the transport dialed %q, want only the host and port of backend.url %q", addr, backend)
		}
	}
	if n := proxied.Load(); n != 0 {
		t.Errorf("the proxy named by the environment received %d requests, want none", n)
	}
}

func TestHTTPTransport_BuiltWithoutProxyAndRedirects(t *testing.T) {
	tr := newTransport(t, "https://backend.example:8081")
	if tr.client == nil {
		t.Fatalf("NewHTTPTransport() built no http client")
	}
	ht, ok := tr.client.Transport.(*http.Transport)
	if !ok {
		t.Fatalf("client.Transport is %T, want the transport's own *http.Transport", tr.client.Transport)
	}
	if ht.Proxy != nil {
		t.Errorf("Transport.Proxy is set, want nil so no proxy of the environment is used")
	}
	if tr.client.CheckRedirect == nil {
		t.Fatalf("client.CheckRedirect is nil, want a function that stops at a redirect")
	}
	if err := tr.client.CheckRedirect(&http.Request{}, []*http.Request{{}}); !errors.Is(err, http.ErrUseLastResponse) {
		t.Errorf("CheckRedirect() = %v, want http.ErrUseLastResponse", err)
	}
	if tr.dial != nil {
		t.Errorf("dial hook of a new transport is set, want nil (only tests set it)")
	}
	requireBuiltLimits(t, ht)
}

// requireBuiltLimits checks the limits and the certificate verification of the built transport.
func requireBuiltLimits(t *testing.T, ht *http.Transport) {
	t.Helper()

	if ht.MaxResponseHeaderBytes != MaxAnswerHeader {
		t.Errorf("MaxResponseHeaderBytes = %d, want %d", ht.MaxResponseHeaderBytes, MaxAnswerHeader)
	}
	if !ht.DisableCompression {
		t.Errorf("DisableCompression = false, want true")
	}
	if ht.ForceAttemptHTTP2 {
		t.Errorf("ForceAttemptHTTP2 = true, want false")
	}
	if ht.MaxConnsPerHost != 2 {
		t.Errorf("MaxConnsPerHost = %d, want 2 (one live and one backfill request)", ht.MaxConnsPerHost)
	}
	cfg := ht.TLSClientConfig
	if cfg == nil {
		t.Fatalf("TLSClientConfig is nil, want one that sets MinVersion TLS 1.2")
	}
	if cfg.InsecureSkipVerify {
		t.Errorf("TLSClientConfig.InsecureSkipVerify = true, want certificate verification")
	}
	if cfg.MinVersion != tls.VersionTLS12 {
		t.Errorf("TLSClientConfig.MinVersion = %#x, want TLS 1.2 (%#x)", cfg.MinVersion, tls.VersionTLS12)
	}
	if cfg.RootCAs != nil || cfg.VerifyPeerCertificate != nil || cfg.VerifyConnection != nil {
		t.Errorf("TLSClientConfig replaces the system roots or the verification, want the defaults")
	}
}

func TestHTTPTransport_FollowsNoRedirect(t *testing.T) {
	for _, code := range []int{301, 302, 303, 307, 308} {
		t.Run(strconv.Itoa(code), func(t *testing.T) {
			var targetHits, frontHits atomic.Int32
			target := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				targetHits.Add(1)
				w.WriteHeader(http.StatusOK)
			}))
			front := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				frontHits.Add(1)
				w.Header().Set("Location", target.URL+IngestPath)
				w.WriteHeader(code)
			}))
			tr := newTransport(t, front.URL)

			res := send(t, tr)

			requireResult(t, "redirect answer", res, Result{Status: code, Reason: "status"})
			requireOutcome(t, "redirect answer", res, NotAccepting)
			if n := targetHits.Load(); n != 0 {
				t.Errorf("the redirect target received %d requests, want none (the token must not follow a redirect)", n)
			}
			if n := frontHits.Load(); n != 1 {
				t.Errorf("the backend received %d requests, want 1", n)
			}
		})
	}
}

func TestHTTPTransport_ReasonPerFailure(t *testing.T) {
	t.Run("status of an answer", testReasonPerStatus)
	t.Run("connection refused", func(t *testing.T) {
		ln, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatalf("Listen() error = %v, want nil", err)
		}
		addr := ln.Addr().String()
		_ = ln.Close()

		requireFailure(t, "http://"+addr, "connection failed")
	})
	t.Run("https at a plain http listener", func(t *testing.T) {
		srv := newServer(t, statusHandler(http.StatusOK))

		requireFailure(t, "https://"+srv.Listener.Addr().String(), "connection failed")
	})
	t.Run("untrusted certificate", func(t *testing.T) {
		srv := newTLSServer(t, statusHandler(http.StatusOK))

		requireFailure(t, srv.URL, "tls failed")
	})
	t.Run("answer that is not http", func(t *testing.T) {
		addr := rawServer(t, func(c net.Conn) { _, _ = io.WriteString(c, "this is not http\r\n\r\n") })

		requireFailure(t, "http://"+addr, "connection failed")
	})
	t.Run("connection closed without an answer", func(t *testing.T) {
		addr := rawServer(t, func(net.Conn) {})

		requireFailure(t, "http://"+addr, "connection failed")
	})
}

// requireFailure sends to baseURL and expects no status, the reason and the outcome NotAccepting.
func requireFailure(t *testing.T, baseURL, reason string) {
	t.Helper()

	res := send(t, newTransport(t, baseURL))

	requireResult(t, "failure", res, Result{Reason: reason})
	requireOutcome(t, "failure", res, NotAccepting)
}

// testReasonPerStatus checks the result and the outcome of every status class the spec names.
func testReasonPerStatus(t *testing.T) {
	cases := []struct {
		status  int
		reason  string
		outcome Outcome
	}{
		{200, "", Acknowledged}, {202, "", Acknowledged}, {204, "", Acknowledged},
		{301, "status", NotAccepting}, {302, "status", NotAccepting}, {307, "status", NotAccepting}, {308, "status", NotAccepting},
		{400, "status", NotAccepting}, {401, "token refused", NotAccepting}, {403, "token refused", NotAccepting},
		{404, "status", NotAccepting}, {408, "status", NotAccepting}, {413, "status", NotAccepting},
		{415, "status", NotAccepting}, {422, "status", NotAccepting}, {429, "status", NotAccepting},
		{500, "status", NotAccepting}, {502, "status", NotAccepting}, {503, "status", NotAccepting},
	}
	for _, tc := range cases {
		t.Run(strconv.Itoa(tc.status), func(t *testing.T) {
			srv := newServer(t, statusHandler(tc.status))

			res := send(t, newTransport(t, srv.URL))

			requireResult(t, "answer", res, Result{Status: tc.status, Reason: tc.reason})
			requireOutcome(t, "answer", res, tc.outcome)
		})
	}
}

func TestHTTPTransport_DeadlineIsTimeout(t *testing.T) {
	release := make(chan struct{})
	arrived := make(chan struct{}, 4)
	srv := newServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		arrived <- struct{}{}
		<-release
	}))
	// Registered after the server's own cleanup, so it runs first and frees the handlers before the server closes.
	t.Cleanup(func() { close(release) })

	t.Run("deadline of the caller", func(t *testing.T) {
		tr := newTransport(t, srv.URL)
		ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
		defer cancel()

		res := tr.Send(ctx, testBody)

		requireResult(t, "deadline", res, Result{Reason: "timeout"})
		requireOutcome(t, "deadline", res, NotAccepting)
	})
	t.Run("cancellation of the caller", func(t *testing.T) {
		tr := newTransport(t, srv.URL)
		ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
		defer cancel()
		go func() {
			select {
			case <-arrived:
				cancel()
			case <-ctx.Done():
			}
		}()

		res := tr.Send(ctx, testBody)

		requireResult(t, "cancellation", res, Result{})
		requireOutcome(t, "cancellation", res, Stopped)
	})
}

// refusalMarkers are the ways a refusing answer can carry the marker header, and whether each is a refusal.
var refusalMarkers = []struct {
	name    string
	set     func(h http.Header)
	refused bool
}{
	{"marker", func(h http.Header) { h.Set(RefusedHeader, RefusedValue) }, true},
	{"marker name in lower case on the wire", func(h http.Header) { h["vandox-refused"] = []string{RefusedValue} }, true},
	{"no marker", func(http.Header) {}, false},
	{"marker value with a capital", func(h http.Header) { h.Set(RefusedHeader, "Batch") }, false},
	{"marker value in capitals", func(h http.Header) { h.Set(RefusedHeader, "BATCH") }, false},
	{"marker with another value", func(h http.Header) { h.Set(RefusedHeader, "other") }, false},
	{"marker without a value", func(h http.Header) { h.Set(RefusedHeader, "") }, false},
}

func TestHTTPTransport_RefusedOnlyWithHeader(t *testing.T) {
	for _, status := range []int{400, 413, 422} {
		for _, m := range refusalMarkers {
			t.Run(strconv.Itoa(status)+" "+m.name, func(t *testing.T) {
				srv := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
					m.set(w.Header())
					w.WriteHeader(status)
				}))

				requireRefusal(t, send(t, newTransport(t, srv.URL)), status, m.refused)
			})
		}
	}
	for _, status := range []int{200, 401, 404, 500} {
		t.Run(strconv.Itoa(status)+" with the marker is no refusal", func(t *testing.T) {
			srv := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				w.Header().Set(RefusedHeader, RefusedValue)
				w.WriteHeader(status)
			}))

			res := send(t, newTransport(t, srv.URL))

			if res.Refused || res.Status != status {
				t.Errorf("Send() = %+v, want Status %d and no refusal: only 400, 413 and 422 can be one", res, status)
			}
		})
	}
}

// requireRefusal checks the result of a 400, 413 or 422: a refusal exactly when the marker was right.
func requireRefusal(t *testing.T, res Result, status int, refused bool) {
	t.Helper()

	if res.Refused != refused || res.Status != status {
		t.Errorf("Send() = %+v, want Status %d and Refused %v", res, status, refused)
	}
	if refused {
		requireOutcome(t, "refusal", res, Refused)

		return
	}
	requireResult(t, "answer without the exact marker", res, Result{Status: status, Reason: "status"})
	requireOutcome(t, "answer without the exact marker", res, NotAccepting)
}

func TestHTTPTransport_RefusesUntrustedCertificate(t *testing.T) {
	var hits atomic.Int32
	srv := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		hits.Add(1)
		w.WriteHeader(http.StatusOK)
	}))

	res := send(t, newTransport(t, srv.URL))

	requireResult(t, "untrusted certificate", res, Result{Reason: "tls failed"})
	requireOutcome(t, "untrusted certificate", res, NotAccepting)
	if n := hits.Load(); n != 0 {
		t.Errorf("the server behind an untrusted certificate received %d requests, want none (verification must not be skipped)", n)
	}
}

// seenRequest is what the server of a token test saw.
type seenRequest struct {
	method, uri, query string
	header             http.Header
	contentLength      int64
	body               []byte
}

func TestHTTPTransport_SendsTokenOnlyToBackend(t *testing.T) {
	for _, suffix := range []string{"", "/"} {
		t.Run("base url "+strconv.Quote(suffix), func(t *testing.T) {
			var mu sync.Mutex
			var seen []seenRequest
			srv := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				body, _ := io.ReadAll(r.Body)
				mu.Lock()
				defer mu.Unlock()
				seen = append(seen, seenRequest{r.Method, r.RequestURI, r.URL.RawQuery, r.Header.Clone(), r.ContentLength, body})
				w.WriteHeader(http.StatusOK)
			}))
			tr := newTransport(t, srv.URL+suffix)

			res := send(t, tr)

			requireResult(t, "answer", res, Result{Status: 200})
			requireOutcome(t, "answer", res, Acknowledged)
			mu.Lock()
			defer mu.Unlock()
			if len(seen) != 1 {
				t.Fatalf("the backend saw %d requests, want 1", len(seen))
			}
			requireIngestRequest(t, seen[0])
			requireRedacted(t, tr)
		})
	}
}

// requireIngestRequest checks the request contract of the plan: path, token, content type, user agent, no encoding.
func requireIngestRequest(t *testing.T, got seenRequest) {
	t.Helper()

	if got.method != http.MethodPost || got.uri != IngestPath || got.query != "" {
		t.Errorf("request = %s %s?%s, want POST %s without a query", got.method, got.uri, got.query, IngestPath)
	}
	want := map[string]string{
		"Authorization": "Bearer " + testToken,
		"Content-Type":  "application/gzip",
		"User-Agent":    "vandox-agent/" + version.Version,
	}
	for name, value := range want {
		if v := got.header.Get(name); v != value {
			t.Errorf("header %s = %q, want %q", name, v, value)
		}
	}
	for _, name := range []string{"Content-Encoding", "Accept-Encoding"} {
		if v := got.header.Get(name); v != "" {
			t.Errorf("header %s = %q, want none (the batch is sent as it is, the answer is not compressed)", name, v)
		}
	}
	if got.contentLength != int64(len(testBody)) || !bytes.Equal(got.body, testBody) {
		t.Errorf("body = %d bytes announced as %d, want the %d bytes handed to Send", len(got.body), got.contentLength, len(testBody))
	}
	for name, values := range got.header {
		if name != "Authorization" && strings.Contains(strings.Join(values, ","), testToken) {
			t.Errorf("header %s carries the token, want it only in Authorization", name)
		}
	}
	if strings.Contains(got.uri, testToken) {
		t.Errorf("request URI %q carries the token, want it only in the header", got.uri)
	}
}

// requireRedacted fails the test when a formatting of the transport shows the token.
func requireRedacted(t *testing.T, tr *HTTPTransport) {
	t.Helper()

	for _, format := range []string{"%v", "%+v", "%#v", "%s"} {
		if out := fmt.Sprintf(format, tr); strings.Contains(out, testToken) {
			t.Errorf("Sprintf(%q, transport) shows the token, want it redacted", format)
		}
	}
}

// countConn counts the bytes the client reads.
type countConn struct {
	net.Conn
	n *atomic.Int64
}

// Read reads and counts.
func (c *countConn) Read(p []byte) (int, error) {
	n, err := c.Conn.Read(p)
	c.n.Add(int64(n))

	return n, err
}

func TestHTTPTransport_ReadsAtMostFourKiBOfBody(t *testing.T) {
	const total = 8 << 20
	cases := []struct {
		name    string
		status  int
		chunked bool
		outcome Outcome
		reason  string
	}{
		{"200 with content length", 200, false, Acknowledged, ""},
		{"200 chunked", 200, true, Acknowledged, ""},
		{"500 with content length", 500, false, NotAccepting, "status"},
		{"500 chunked", 500, true, NotAccepting, "status"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			srv := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				writeHostileBody(w, tc.status, tc.chunked, total)
			}))
			tr := newTransport(t, srv.URL)
			var read atomic.Int64
			useDial(tr, func(ctx context.Context, network, addr string) (net.Conn, error) {
				var d net.Dialer
				c, err := d.DialContext(ctx, network, addr)
				if err != nil {
					return nil, err
				}

				return &countConn{Conn: c, n: &read}, nil
			})

			res := send(t, tr)

			requireResult(t, "answer with a hostile body", res, Result{Status: tc.status, Reason: tc.reason})
			requireOutcome(t, "answer with a hostile body", res, tc.outcome)
			if got := read.Load(); got > MaxAnswerHeader {
				t.Errorf("the transport read %d bytes of an answer of %d body bytes, want the body cut at %d bytes (at most %d read in all)", got, total, MaxAnswerBody, MaxAnswerHeader)
			}
		})
	}
}

// writeHostileBody answers with status and up to total bytes of body, until the client stops reading.
func writeHostileBody(w http.ResponseWriter, status int, chunked bool, total int) {
	if !chunked {
		w.Header().Set("Content-Length", strconv.Itoa(total))
	}
	w.WriteHeader(status)
	flusher, _ := w.(http.Flusher)
	chunk := bytes.Repeat([]byte("x"), 16<<10)
	for sent := 0; sent < total; sent += len(chunk) {
		if _, err := w.Write(chunk); err != nil {
			return
		}
		if chunked && flusher != nil {
			flusher.Flush()
		}
	}
}

func TestHTTPTransport_RefusesOversizedHeaders(t *testing.T) {
	cases := []struct {
		name string
		set  func(h http.Header)
		want Result
	}{
		{"one header of 64 KiB", func(h http.Header) { h.Set("X-Pad", strings.Repeat("a", 64<<10)) }, Result{Reason: "connection failed"}},
		{"1,000 headers of 64 bytes", func(h http.Header) {
			for i := range 1000 {
				h.Set("X-Pad-"+strconv.Itoa(i), strings.Repeat("a", 64))
			}
		}, Result{Reason: "connection failed"}},
		{"one header of 8 KiB", func(h http.Header) { h.Set("X-Pad", strings.Repeat("a", 8<<10)) }, Result{Status: 200}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			srv := newServer(t, http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				tc.set(w.Header())
				w.WriteHeader(http.StatusOK)
			}))

			res := send(t, newTransport(t, srv.URL))

			requireResult(t, "answer with large headers", res, tc.want)
		})
	}
}
