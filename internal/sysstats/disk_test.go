package sysstats_test

import (
	"errors"
	"fmt"
	"io/fs"
	"strings"
	"testing"
	"testing/fstest"

	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

func TestSystem_DiskSelection(t *testing.T) {
	t.Run("only entries with a device entry, the first 16 in name order", requireFirstSixteenDisks)
	t.Run("a device entry without stat fails the sample", requireMissingStatFails)
}

func requireFirstSixteenDisks(t *testing.T) {
	tr := loadTree(t)
	tr.sys = fstest.MapFS{
		"block/aaa/stat":   {Data: []byte(statText(900, 1, 1, 1, 1))},
		"block/loop9/stat": {Data: []byte(statText(901, 1, 1, 1, 1))},
	}
	for i := 0; i < 20; i++ {
		tr.addDisk(fmt.Sprintf("d%02d", i), statText(uint64(i+1), 1, 1, 1, 1))
	}

	got := mustCollect(t, sysstats.NewSystem(tr.sources(), nil), runStart)

	reads := find(got, "disk_reads_total")
	if len(reads) != sysstats.MaxDisks {
		t.Fatalf("disk_reads_total records = %v, want %d, one per recorded disk", reads, sysstats.MaxDisks)
	}
	for i, r := range reads {
		wantDevice := fmt.Sprintf("d%02d", i)
		if r.labels["device"] != wantDevice || r.value != float64(i+1) {
			t.Errorf("disk_reads_total record %d = %s, want device %s with value %d", i, r, wantDevice, i+1)
		}
	}
	perDisk := 0
	for _, r := range got {
		if g, ok := gotRec(r); ok && strings.HasPrefix(g.name, "disk_") {
			perDisk++
		}
	}
	if perDisk != sysstats.MaxDisks*5 {
		t.Errorf("disk records = %d, want %d (five counters of each of %d disks)", perDisk, sysstats.MaxDisks*5, sysstats.MaxDisks)
	}
}

func requireMissingStatFails(t *testing.T) {
	tr := loadTree(t)
	tr.sys = fstest.MapFS{"block/vda/device/uevent": {Data: []byte("DRIVER=virtio_blk\n")}}

	got, err := collect(sysstats.NewSystem(tr.sources(), nil), runStart)

	if err == nil || !strings.HasPrefix(err.Error(), "sysstats: block/vda/stat: ") {
		t.Fatalf("Collect() error = %v, want one starting with %q", err, "sysstats: block/vda/stat: ")
	}
	if !errors.Is(err, fs.ErrNotExist) {
		t.Errorf("Collect() error = %v, want it to wrap fs.ErrNotExist", err)
	}
	if len(got) != 0 {
		t.Errorf("Collect() returned %d records with its error, want none", len(got))
	}
}
