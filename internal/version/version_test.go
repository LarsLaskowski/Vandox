package version_test

import (
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/version"
)

func TestString(t *testing.T) {
	origVersion, origCommit, origDate := version.Version, version.Commit, version.Date
	t.Cleanup(func() {
		version.Version, version.Commit, version.Date = origVersion, origCommit, origDate
	})

	tests := []struct {
		name    string
		binary  string
		version string
		commit  string
		date    string
		want    string
	}{
		{
			name:    "defaults for the agent",
			binary:  "vandox-agent",
			version: "dev",
			commit:  "unknown",
			date:    "unknown",
			want:    "vandox-agent dev (commit unknown, built unknown)",
		},
		{
			name:    "defaults for the backend",
			binary:  "vandoxd",
			version: "dev",
			commit:  "unknown",
			date:    "unknown",
			want:    "vandoxd dev (commit unknown, built unknown)",
		},
		{
			name:    "all values set by ldflags",
			binary:  "vandoxd",
			version: "v0.1.0",
			commit:  "abc1234",
			date:    "2026-10-04T00:00:00Z",
			want:    "vandoxd v0.1.0 (commit abc1234, built 2026-10-04T00:00:00Z)",
		},
		{
			name:    "empty name",
			binary:  "",
			version: "v0.1.0",
			commit:  "abc1234",
			date:    "2026-10-04T00:00:00Z",
			want:    " v0.1.0 (commit abc1234, built 2026-10-04T00:00:00Z)",
		},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			version.Version, version.Commit, version.Date = tc.version, tc.commit, tc.date

			got := version.String(tc.binary)

			if got != tc.want {
				t.Errorf("String(%q) = %q, want %q", tc.binary, got, tc.want)
			}
		})
	}
}
