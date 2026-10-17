package sysstats_test

import (
	"strconv"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

// kbLine returns a meminfo line of field with value kB.
func kbLine(field string, value int) string {
	return field + ":       " + strconv.Itoa(value) + " kB"
}

// memoryCase is a variant of the memory lines of the fixture.
type memoryCase struct {
	name   string
	edits  map[string]string // field -> the whole replacement line
	want   map[string]int    // record name -> value in kB
	absent []string          // record names that are left out
}

func memoryCases() []memoryCase {
	return []memoryCase{
		{
			name: "used and cache from the sums",
			edits: map[string]string{
				"MemTotal": kbLine("MemTotal", 1000), "MemFree": kbLine("MemFree", 100), "Buffers": kbLine("Buffers", 100),
				"Cached": kbLine("Cached", 200), "SReclaimable": kbLine("SReclaimable", 50), "MemAvailable": kbLine("MemAvailable", 700),
			},
			want: map[string]int{
				"mem_total_bytes": 1000, "mem_free_bytes": 100, "mem_used_bytes": 550, "mem_buff_cache_bytes": 350, "mem_available_bytes": 700,
			},
		},
		{
			name: "negative used falls back to total minus free",
			edits: map[string]string{
				"MemTotal": kbLine("MemTotal", 1000), "MemFree": kbLine("MemFree", 100), "Buffers": kbLine("Buffers", 500),
				"Cached": kbLine("Cached", 400), "SReclaimable": kbLine("SReclaimable", 300),
			},
			want: map[string]int{"mem_used_bytes": 900, "mem_buff_cache_bytes": 1200},
		},
		{
			name: "used of exactly zero is not negative",
			edits: map[string]string{
				"MemTotal": kbLine("MemTotal", 1000), "MemFree": kbLine("MemFree", 100), "Buffers": kbLine("Buffers", 400),
				"Cached": kbLine("Cached", 400), "SReclaimable": kbLine("SReclaimable", 100),
			},
			want: map[string]int{"mem_used_bytes": 0, "mem_buff_cache_bytes": 900},
		},
		{
			name:  "swap used is total minus free",
			edits: map[string]string{"SwapTotal": kbLine("SwapTotal", 2000), "SwapFree": kbLine("SwapFree", 500)},
			want:  map[string]int{"swap_total_bytes": 2000, "swap_free_bytes": 500, "swap_used_bytes": 1500},
		},
		{
			name:   "swap used is left out when free is above total",
			edits:  map[string]string{"SwapTotal": kbLine("SwapTotal", 100), "SwapFree": kbLine("SwapFree", 200)},
			want:   map[string]int{"swap_total_bytes": 100, "swap_free_bytes": 200},
			absent: []string{"swap_used_bytes"},
		},
	}
}

func TestSystem_MemoryAsTopPrintsIt(t *testing.T) {
	for _, tt := range memoryCases() {
		t.Run(tt.name, func(t *testing.T) {
			tr := loadTree(t)
			for field, line := range tt.edits {
				replaceLine(t, tr.proc, "meminfo", field+":", line)
			}

			got := mustCollect(t, sysstats.NewSystem(tr.sources(), nil), runStart)

			requireMemory(t, got, tt.want, tt.absent)
		})
	}

	required := []string{"MemTotal", "MemFree", "MemAvailable", "Buffers", "Cached", "SReclaimable", "SwapTotal", "SwapFree"}
	for _, field := range required {
		want := "sysstats: meminfo: no " + field + " value in kB"
		t.Run(field+" missing", func(t *testing.T) {
			tr := loadTree(t)
			replaceLine(t, tr.proc, "meminfo", field+":", "")

			requireFails(t, sysstats.NewSystem(tr.sources(), nil), runStart, want)
		})
		t.Run(field+" without kB", func(t *testing.T) {
			tr := loadTree(t)
			replaceLine(t, tr.proc, "meminfo", field+":", field+":       123")

			requireFails(t, sysstats.NewSystem(tr.sources(), nil), runStart, want)
		})
	}
}

// requireMemory checks that each record in want holds its kB in bytes and that the records in absent are left out.
func requireMemory(t *testing.T, got []model.Record, want map[string]int, absent []string) {
	t.Helper()

	for name, kB := range want {
		recs := find(got, name)
		if len(recs) != 1 || recs[0].unit != "bytes" || recs[0].value != float64(kB*kib) {
			t.Errorf("%s records = %v, want one with %d bytes", name, recs, kB*kib)
		}
	}
	for _, name := range absent {
		if recs := find(got, name); len(recs) != 0 {
			t.Errorf("%s records = %v, want none", name, recs)
		}
	}
}
