package wire_test

import (
	"bytes"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
	"unicode/utf8"

	"github.com/LarsLaskowski/Vandox/internal/model"
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

// replacedTextRecord builds the log line of the replaced-text golden batch: a host of exactly model.MaxShortTextBytes and a message
// of exactly model.MaxTextBytes counted bytes (every invalid byte as the 3 bytes of U+FFFD), each holding every invalid form once, a
// valid U+FFFD and the multi-byte characters é€😀, filled with a.
func replacedTextRecord() model.Record {
	build := func(limit int) string {
		var sb strings.Builder
		counted := 0
		for _, form := range invalidTextForms() {
			if form.atEnd {
				continue
			}
			sb.WriteString(form.raw + "-")
			counted += form.counted() + 1
		}
		sb.WriteString("\xef\xbf\xbd-é€😀-")
		counted += 3 + 1 + 9 + 1
		end := invalidTextForms()[7]
		filler := limit - counted - end.counted()
		sb.WriteString(strings.Repeat("a", filler))
		sb.WriteString(end.raw)
		return sb.String()
	}
	return model.Record{Meta: agentMeta(1, "journal", time.UTC), Data: &model.LogLine{
		Log: "journal", Host: build(model.MaxShortTextBytes), Message: build(model.MaxTextBytes),
	}}
}

// countedLen measures s as the decoders do after the encoder's replacement: a byte that does not start a valid sequence is 3 bytes.
func countedLen(s string) int {
	n := 0
	for _, r := range s {
		n += utf8.RuneLen(r)
	}
	return n
}

// TestEncodeBatchGolden_ReplacedText pins a batch whose texts sit at their limits only after the encoder's replacement. The .NET
// backend decodes the same file in its contract test.
func TestEncodeBatchGolden_ReplacedText(t *testing.T) {
	rec := replacedTextRecord()
	line := rec.Data.(*model.LogLine)
	if len(line.Message) >= model.MaxTextBytes || countedLen(line.Message) != model.MaxTextBytes {
		t.Fatalf("message raw length = %d, counted = %d, want raw below and counted equal to %d", len(line.Message), countedLen(line.Message), model.MaxTextBytes)
	}
	if len(line.Host) >= model.MaxShortTextBytes || countedLen(line.Host) != model.MaxShortTextBytes {
		t.Fatalf("host raw length = %d, counted = %d, want raw below and counted equal to %d", len(line.Host), countedLen(line.Host), model.MaxShortTextBytes)
	}
	if utf8.ValidString(line.Message) || utf8.ValidString(line.Host) {
		t.Fatalf("message and host must hold invalid UTF-8")
	}
	batch := &wire.Batch{Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive), Records: []model.Record{rec}}
	var buf bytes.Buffer
	if err := wire.EncodeBatch(&buf, batch); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	got := gunzip(t, buf.Bytes())
	path := filepath.Join("..", "..", "testdata", "wire", "replaced-text.jsonl")
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
		t.Errorf("encoded batch differs from %s (set %s=1 to update)", path, updateGoldenEnv)
	}
}

// TestEncodeBatchStartSnapshotCarriesInventoryAndTables pins the three values the listener restart check adds to a connection snapshot
// on the wire (the golden batch gains them with the backend decoder that accepts them).
func TestEncodeBatchStartSnapshotCarriesInventoryAndTables(t *testing.T) {
	batch := &wire.Batch{
		Header: wire.NewHeader("agent-1", testBootID, wire.ModeLive),
		Records: []model.Record{{Meta: agentMeta(1, "connections", time.UTC), Data: &model.ConnectionSnapshot{
			Complete:      true,
			ListenerCheck: model.ListenerCheckStart,
			Inventory:     model.InventoryPrivileged,
			Tables:        []model.Proto{model.ProtoTCP, model.ProtoUDP6},
			Listeners:     []model.Listener{{Proto: model.ProtoTCP, Local: netip.MustParseAddrPort("0.0.0.0:22")}},
		}}},
	}
	var buf bytes.Buffer
	if err := wire.EncodeBatch(&buf, batch); err != nil {
		t.Fatalf("EncodeBatch() = %v, want nil", err)
	}
	got := gunzip(t, buf.Bytes())
	for _, want := range []string{`"listener_check":"start"`, `"inventory":"privileged"`, `"tables":["tcp","udp6"]`} {
		if !strings.Contains(got, want) {
			t.Errorf("encoded batch lacks %s\ngot:\n%s", want, got)
		}
	}

	t.Run("a snapshot without the fields omits them", func(t *testing.T) {
		batch.Records[0].Data = &model.ConnectionSnapshot{Complete: true}
		var buf bytes.Buffer
		if err := wire.EncodeBatch(&buf, batch); err != nil {
			t.Fatalf("EncodeBatch() = %v, want nil", err)
		}
		plain := gunzip(t, buf.Bytes())
		for _, absent := range []string{`"inventory"`, `"tables"`, `"listener_check"`} {
			if strings.Contains(plain, absent) {
				t.Errorf("encoded batch holds %s, want it omitted when empty\ngot:\n%s", absent, plain)
			}
		}
	})
}
