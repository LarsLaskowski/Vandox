// Package version holds build information injected via -ldflags.
package version

import "fmt"

// Set at build time, e.g.
//
//	-ldflags "-X github.com/LarsLaskowski/Vandox/internal/version.Version=v0.1.0 ..."
var (
	Version = "dev"
	Commit  = "unknown"
	Date    = "unknown"
)

// String returns a single-line description of the build.
func String(name string) string {
	return fmt.Sprintf("%s %s (commit %s, built %s)", name, Version, Commit, Date)
}
