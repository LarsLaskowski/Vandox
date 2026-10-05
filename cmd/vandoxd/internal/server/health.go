package server

import (
	"log/slog"
	"net/http"
	"time"
)

// NewWebHandler returns the handler of the web listener: GET and HEAD /healthz, 200 when db answers within
// pingTimeout, 503 otherwise.
func NewWebHandler(db Pinger, pingTimeout time.Duration, logger *slog.Logger) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "not implemented", http.StatusInternalServerError)
	})
}
