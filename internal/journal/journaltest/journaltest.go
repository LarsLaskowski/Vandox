// Package journaltest writes journal files in the server's layout and reads journal export text; tests only.
//
// The layout is that of systemd 249 on the monitored server: a 256-byte header, the KEYED-HASH and COMPRESSED-ZSTD flags, no COMPACT
// layout, a data hash table keyed with the file ID, entry arrays that double in size, and items of 512 bytes and more stored as zstd
// frames made of one raw block (so that the file needs no compressor and the reader's refusal to decompress is exercised).
package journaltest

import (
	"errors"
	"fmt"
	"io"
	"sort"
	"strconv"
	"strings"
)

const (
	headerSize     = 256
	compressMin    = 512 // journald compresses a payload (NAME=value) from this size on
	maxRawFrame    = 65792 - 1
	firstArrayCap  = 4
	minBuckets     = 16
	objectData     = 1
	objectEntry    = 3
	objectDataHash = 4
	objectFieldTab = 5
	objectArray    = 6
	flagZSTD       = 4
	incompatKeyed  = 1 << 2
	incompatZSTD   = 1 << 3
	stateOnline    = 1
	stateArchived  = 2
)

// Options configures Write.
type Options struct {
	FileID, MachineID, SeqnumID [16]byte
	Online                      bool
}

// Write writes entries as a journal file to w, in the order given. An entry holds the pseudo fields __REALTIME_TIMESTAMP and
// __MONOTONIC_TIMESTAMP (µs, decimal), __SEQNUM (decimal) and __SEQNUM_ID (ignored: the file's seqnum ID is Options.SeqnumID), which
// are never items, and its other fields; NAME#2, NAME#3 are further items named NAME. The field _BOOT_ID, when present, also sets
// the boot ID of the entry.
func Write(w io.Writer, entries []map[string][]byte, opts Options) error {
	parsed, err := parseEntries(entries)
	if err != nil {
		return err
	}
	b := newBuilder(parsed, opts)
	for i := range parsed {
		b.addEntry(&parsed[i])
	}
	if err := b.finish(parsed); err != nil {
		return err
	}
	_, err = w.Write(b.buf)
	return err
}

// parsedEntry is an entry with its stamps decoded and its items in a fixed order.
type parsedEntry struct {
	seqnum, realtime, mono uint64
	boot                   [16]byte
	items                  [][]byte
}

func parseEntries(entries []map[string][]byte) ([]parsedEntry, error) {
	out := make([]parsedEntry, 0, len(entries))
	for i, e := range entries {
		p, err := parseEntry(e)
		if err != nil {
			return nil, fmt.Errorf("journaltest: entry %d: %w", i, err)
		}
		out = append(out, p)
	}
	return out, nil
}

func parseEntry(e map[string][]byte) (parsedEntry, error) {
	var p parsedEntry
	for _, f := range []struct {
		name string
		dst  *uint64
	}{{"__SEQNUM", &p.seqnum}, {"__REALTIME_TIMESTAMP", &p.realtime}, {"__MONOTONIC_TIMESTAMP", &p.mono}} {
		v, err := strconv.ParseUint(string(e[f.name]), 10, 64)
		if err != nil {
			return p, fmt.Errorf("%s is not a decimal number", f.name)
		}
		*f.dst = v
	}
	if id, ok := e["_BOOT_ID"]; ok {
		if err := parseID(string(id), &p.boot); err != nil {
			return p, errors.New("_BOOT_ID is not 32 hex digits")
		}
	}
	p.items = itemPayloads(e)
	return p, nil
}

// parseID decodes 32 hex digits.
func parseID(s string, id *[16]byte) error {
	if len(s) != 32 {
		return errors.New("bad length")
	}
	for i := range id {
		v, err := strconv.ParseUint(s[2*i:2*i+2], 16, 8)
		if err != nil {
			return err
		}
		id[i] = byte(v)
	}
	return nil
}

// fieldKey is a key of an entry split into the field name and its occurrence.
type fieldKey struct {
	key  string
	name string
	nth  int
}

func splitKey(key string) fieldKey {
	if i := strings.LastIndexByte(key, '#'); i > 0 {
		if n, err := strconv.Atoi(key[i+1:]); err == nil && n >= 2 {
			return fieldKey{key: key, name: key[:i], nth: n}
		}
	}
	return fieldKey{key: key, name: key, nth: 1}
}

// itemPayloads returns the NAME=value payloads of the items of an entry: names in order, the occurrences of a name in order.
func itemPayloads(e map[string][]byte) [][]byte {
	keys := make([]fieldKey, 0, len(e))
	for k := range e {
		if !strings.HasPrefix(k, "__") {
			keys = append(keys, splitKey(k))
		}
	}
	sort.Slice(keys, func(i, j int) bool {
		if keys[i].name != keys[j].name {
			return keys[i].name < keys[j].name
		}
		return keys[i].nth < keys[j].nth
	})
	out := make([][]byte, 0, len(keys))
	for _, k := range keys {
		payload := make([]byte, 0, len(k.name)+1+len(e[k.key]))
		payload = append(payload, k.name...)
		payload = append(payload, '=')
		out = append(out, append(payload, e[k.key]...))
	}
	return out
}
