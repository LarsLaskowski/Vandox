package sysstats

import (
	"context"
	"log/slog"
	"slices"
	"sync"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/scheduler"
)

// Filesystem collects the use of the configured mount points; it implements scheduler.Collector.
type Filesystem struct {
	src         Sources
	mountPoints []string
	log         *slog.Logger
	mu          sync.Mutex
}

// NewFilesystem returns the file system collector for mountPoints, which it copies; a nil log logs nothing.
func NewFilesystem(src Sources, mountPoints []string, log *slog.Logger) *Filesystem {
	return &Filesystem{src: src, mountPoints: slices.Clone(mountPoints), log: log}
}

// Name returns FilesystemName.
func (c *Filesystem) Name() string {
	return FilesystemName
}

// Collect takes one sample of the configured mount points.
func (c *Filesystem) Collect(ctx context.Context, run scheduler.Run) ([]model.Record, error) {
	return nil, errNotImplemented
}
