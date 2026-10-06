package wire_test

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/wire"
)

// updateGoldenEnv names the environment variable that rewrites the golden files instead of comparing against them.
const updateGoldenEnv = "VANDOX_UPDATE_GOLDEN"

// TestEncodeBatchGolden pins the decompressed wire format. The .NET backend decodes the same file in its contract
// test, so a change to the encoder that is not mirrored there fails on one side or the other.
func TestEncodeBatchGolden(t *testing.T) {
	batch := &wire.Batch{
		Header:  wire.NewHeader("agent-1", testBootID, wire.ModeLive),
		Records: allKindRecords(time.UTC),
	}
	var buf bytes.Buffer
	if err := wire.EncodeBatch(&buf, batch); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	got := gunzip(t, buf.Bytes())
	path := filepath.Join("..", "..", "testdata", "wire", "all-kinds.jsonl")
	if os.Getenv(updateGoldenEnv) != "" {
		if err := os.WriteFile(path, []byte(got), 0o600); err != nil {
			t.Fatalf("writing golden file = %v, want nil", err)
		}
	}
	want, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("reading golden file = %v, want nil (set %s=1 to create it)", err, updateGoldenEnv)
	}
	if got != string(want) {
		t.Errorf("encoded batch differs from %s (set %s=1 to update)\ngot:\n%s\nwant:\n%s", path, updateGoldenEnv, got, want)
	}
}
