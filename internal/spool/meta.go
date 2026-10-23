package spool

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"os"
	"time"
)

const (
	// metaFileName is the name of the meta file; metaTmpFileName is the temporary file it is replaced through.
	metaFileName    = "spool-meta.json"
	metaTmpFileName = "spool-meta.json.tmp"
	// maxMetaBytes is the largest meta file that is read.
	maxMetaBytes = 16 << 10
	// metaVersion is the version of the meta file.
	metaVersion = 1
	// metaSlack is the size reserved for the meta file and its temporary file.
	metaSlack = 32 << 10
	// metaErrPrefix starts the errors of writing the meta file.
	metaErrPrefix = "spool: meta: "
	// maxStoredRanges is the most ranges the meta file holds: MaxAckRanges above the oldest unacknowledged record plus
	// the one range that reaches down to it, which Ack does not count.
	maxStoredRanges = MaxAckRanges + 1
	// clockBoundAhead is added to the clock's microseconds: the agent issues far fewer numbers per second.
	clockBoundAhead = 1_000_000
)

// ClockBound returns the lower bound for sequence numbers derived from the time now: the microseconds since the Unix
// epoch plus 1,000,000.
func ClockBound(now time.Time) uint64 {
	micros := now.UnixMicro()
	if micros < 0 {
		return clockBoundAhead
	}

	return uint64(micros) + clockBoundAhead
}

// ackRange is a range of acknowledged numbers, both ends included.
type ackRange struct {
	First uint64 `json:"first"`
	Last  uint64 `json:"last"`
}

// metaDoc is the content of the meta file.
type metaDoc struct {
	Version int        `json:"version"`
	AgentID string     `json:"agent_id"`
	BootID  string     `json:"boot_id"`
	Clean   bool       `json:"clean"`
	Next    uint64     `json:"next"`
	Acked   []ackRange `json:"acked"`
}

// The keys of the meta file, every one required.
const (
	keyVersion = "version"
	keyAgentID = "agent_id"
	keyBootID  = "boot_id"
	keyClean   = "clean"
	keyNext    = "next"
	keyAcked   = "acked"
)

// metaKeys lists the keys of the meta file.
var metaKeys = []string{keyVersion, keyAgentID, keyBootID, keyClean, keyNext, keyAcked}

// parseMeta decodes the meta file strictly: one object, version 1, every key once and no other.
func parseMeta(data []byte) (metaDoc, error) {
	var doc metaDoc
	if len(data) > maxMetaBytes {
		return doc, errDamageNotFormat
	}
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	if tok, err := dec.Token(); err != nil || tok != json.Delim('{') {
		return doc, errDamageNotFormat
	}
	seen := map[string]bool{}
	for dec.More() {
		key, err := dec.Token()
		name, isString := key.(string)
		if err != nil || !isString {
			return doc, errDamageNotFormat
		}
		if seen[name] {
			return doc, errDamageInconsistent
		}
		seen[name] = true
		if err := decodeMetaValue(dec, &doc, name); err != nil {
			return doc, err
		}
	}
	if tok, err := dec.Token(); err != nil || tok != json.Delim('}') {
		return doc, errDamageNotFormat
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return doc, errDamageNotFormat
	}

	return doc, doc.check(seen)
}

// decodeMetaValue decodes the value of one key.
func decodeMetaValue(dec *json.Decoder, doc *metaDoc, key string) error {
	var err error
	switch key {
	case keyVersion:
		err = dec.Decode(&doc.Version)
	case keyAgentID:
		err = dec.Decode(&doc.AgentID)
	case keyBootID:
		err = dec.Decode(&doc.BootID)
	case keyClean:
		err = dec.Decode(&doc.Clean)
	case keyNext:
		err = dec.Decode(&doc.Next)
	case keyAcked:
		err = dec.Decode(&doc.Acked)
	default:
		return errDamageInconsistent
	}
	if err == nil {
		return nil
	}
	var typeErr *json.UnmarshalTypeError
	if errors.As(err, &typeErr) {
		return errDamageInconsistent
	}

	return errDamageNotFormat
}

// check applies the consistency rules to a decoded meta file.
func (m *metaDoc) check(seen map[string]bool) error {
	if seen[keyVersion] && m.Version != metaVersion {
		return errDamageVersion
	}
	for _, key := range metaKeys {
		if !seen[key] {
			return errDamageInconsistent
		}
	}
	if len(m.AgentID) > maxAgentLen || len(m.BootID) > bootIDLen || m.Next >= maxSeq || len(m.Acked) > maxStoredRanges {
		return errDamageInconsistent
	}

	return checkRanges(m.Acked, m.Next)
}

// checkRanges requires the ranges to be sorted, apart from each other, ordered inside and below next.
func checkRanges(ranges []ackRange, next uint64) error {
	var prevLast uint64
	for i, r := range ranges {
		if r.First == 0 || r.First > r.Last || r.Last >= next || (i > 0 && r.First <= prevLast) {
			return errDamageInconsistent
		}
		prevLast = r.Last
	}

	return nil
}

// readMeta reads the meta file. A missing file gives errAbsent, an entry the spool refuses errDamageRefused; damage is one of
// the damage reasons.
func (s *Spool) readMeta() (metaDoc, error) {
	f, _, err := s.dir.openEntry(metaFileName, false)
	if err != nil {
		return metaDoc{}, err
	}
	defer func() { _ = f.Close() }()
	data, err := io.ReadAll(io.LimitReader(f, maxMetaBytes+1))
	if err != nil {
		return metaDoc{}, errors.New(readErrPrefix + errText(err))
	}

	return parseMeta(data)
}

// checkMetaNames requires the meta file and its temporary file to be absent or trusted.
func (s *Spool) checkMetaNames() error {
	for _, name := range []string{metaTmpFileName, metaFileName} {
		f, _, err := s.dir.openEntry(name, false)
		if errors.Is(err, errAbsent) {
			continue
		}
		if err != nil {
			return metaError(err)
		}
		_ = f.Close()
	}

	return nil
}

// metaError words an error of the meta file.
func metaError(err error) error {
	if errors.Is(err, errDamageRefused) {
		return errors.New(metaErrPrefix + errDamageRefused.Error())
	}

	return errors.New(metaErrPrefix + errText(err))
}

// encodeMeta returns the meta file for the current state.
func (s *Spool) encodeMeta(clean bool) ([]byte, error) {
	acked := s.acked
	if acked == nil {
		acked = []ackRange{}
	}

	return json.Marshal(metaDoc{Version: metaVersion, AgentID: s.opts.AgentID, BootID: s.opts.BootID, Clean: clean, Next: s.next, Acked: acked})
}

// writeMeta replaces the meta file through its temporary file. With flush set the file and the directory are synced
// before it returns; without it the file stays open for the next sync.
func (s *Spool) writeMeta(clean, flush bool) error {
	data, err := s.encodeMeta(clean)
	if err != nil {
		return metaError(err)
	}
	if err := s.checkMetaNames(); err != nil {
		return err
	}
	if err := s.dir.removeTrusted(metaTmpFileName, nil); err != nil {
		return metaError(err)
	}
	f, _, err := s.dir.create(metaTmpFileName)
	if err != nil {
		return metaError(err)
	}
	if err := s.fillMeta(f, data, flush); err != nil {
		_ = f.Close()
		_ = s.dir.removeTrusted(metaTmpFileName, nil)

		return err
	}
	if err := s.dir.rename(metaTmpFileName, metaFileName); err != nil {
		_ = f.Close()
		_ = s.dir.removeTrusted(metaTmpFileName, nil)

		return metaError(err)
	}
	s.keepMeta(f, int64(len(data)))
	s.dirNeed = true
	if flush {
		return s.flushMeta()
	}

	return nil
}

// fillMeta writes the content of the temporary file.
func (s *Spool) fillMeta(f *os.File, data []byte, flush bool) error {
	if err := writeAll(f, data, 0); err != nil {
		return metaError(err)
	}
	if flush {
		if err := f.Sync(); err != nil {
			return metaError(err)
		}
	}

	return nil
}

// keepMeta remembers the meta file that was written last.
func (s *Spool) keepMeta(f *os.File, size int64) {
	if s.metaF != nil {
		_ = s.metaF.Close()
	}
	s.metaF, s.metaSize = f, size
	s.metaNeed = true
	s.gen++
}

// flushMeta syncs the directory after the meta file was replaced and clears the need.
func (s *Spool) flushMeta() error {
	if err := s.dir.sync(); err != nil {
		return metaError(err)
	}
	s.metaNeed, s.dirNeed = false, false

	return nil
}
