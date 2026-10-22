package journal

import (
	"bufio"
	"bytes"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

const realDirEnv = "VANDOX_REAL_JOURNAL_DIR"

// exField is one field of a journal export entry.
type exField struct {
	name  string
	value []byte
}

// readExport streams the entries of journal export text to fn; binary fields are read by their length.
func readExport(r io.Reader, fn func(fields []exField)) error {
	br := bufio.NewReaderSize(r, 1<<20)
	var cur []exField
	for {
		line, err := br.ReadBytes('\n')
		if len(line) == 0 && errors.Is(err, io.EOF) {
			break
		}
		if err != nil && !errors.Is(err, io.EOF) {
			return err
		}
		line = bytes.TrimSuffix(line, []byte("\n"))
		if len(line) == 0 {
			cur = flushEntry(cur, fn)
			continue
		}
		f, ferr := parseExportLine(br, line)
		if ferr != nil {
			return ferr
		}
		cur = append(cur, f)
	}
	flushEntry(cur, fn)
	return nil
}

func flushEntry(cur []exField, fn func(fields []exField)) []exField {
	if len(cur) > 0 {
		fn(cur)
	}
	return nil
}

// parseExportLine turns a NAME=value line into a field; a line without = names a binary field that follows.
func parseExportLine(br *bufio.Reader, line []byte) (exField, error) {
	if i := bytes.IndexByte(line, '='); i >= 0 {
		return exField{string(line[:i]), append([]byte(nil), line[i+1:]...)}, nil
	}
	v, err := readBinaryField(br)
	return exField{string(line), v}, err
}

func readBinaryField(br *bufio.Reader) ([]byte, error) {
	var n [8]byte
	if _, err := io.ReadFull(br, n[:]); err != nil {
		return nil, err
	}
	size := binary.LittleEndian.Uint64(n[:])
	if size > 1<<28 {
		return nil, errors.New("binary field too large")
	}
	v := make([]byte, size)
	if _, err := io.ReadFull(br, v); err != nil {
		return nil, err
	}
	if c, err := br.ReadByte(); err != nil || c != '\n' {
		return nil, errors.New("binary field not ended by a line feed")
	}
	return v, nil
}

// privateSet holds the private values of the real files, by value, with the class each one belongs to.
type privateSet struct {
	class map[string]string
}

func (p *privateSet) add(class, v string) {
	v = strings.TrimSpace(v)
	if v != "" {
		p.class[strings.ToLower(v)] = class
	}
}

var (
	ipv4Re     = regexp.MustCompile(`(\d{1,3})[.-](\d{1,3})[.-](\d{1,3})[.-](\d{1,3})`)
	partSplit  = regexp.MustCompile(`[.\-_]`)
	mailRe     = regexp.MustCompile(`[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}`)
	domainRe   = regexp.MustCompile(`\b[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,24}\b`)
	userEqRe   = regexp.MustCompile(`\buser=<?([^\s>,;]+)`)
	forMailRe  = regexp.MustCompile(`\bfor <([^>\s]+)>`)
	forUserRe  = regexp.MustCompile(`\bfor (?:invalid user )?([A-Za-z0-9_.\-]+) from\b`)
	atLocalRe  = regexp.MustCompile(`([A-Za-z0-9._+\-]+)@`)
	tokenRe    = regexp.MustCompile(`[A-Za-z0-9_./@:\-]+`)
	publicTerm = map[string]bool{
		"pzstd": true, "mariadbd": true, "/usr/sbin/amavi": true, "systemd": true, "sshd": true, "apport": true,
		"kernel": true, "systemd-journald": true,
	}
)

func (p *privateSet) addHost(h string) {
	p.add("host name", h)
	for _, m := range ipv4Re.FindAllStringSubmatch(h, -1) {
		p.add("address", m[1]+"."+m[2]+"."+m[3]+"."+m[4])
		p.add("address", m[1]+"-"+m[2]+"-"+m[3]+"-"+m[4])
	}
	for _, part := range partSplit.Split(h, -1) {
		if len(part) >= 4 {
			p.add("host name part", part)
		}
	}
	if i := strings.IndexByte(h, '.'); i > 0 {
		p.add("provider domain", h[i+1:])
		labels := strings.Split(h, ".")
		if len(labels) >= 2 {
			p.add("provider domain", strings.Join(labels[len(labels)-2:], "."))
		}
	}
}

func (p *privateSet) addMessage(m string) {
	if strings.ContainsAny(m, ".@") {
		for _, mail := range mailRe.FindAllString(m, -1) {
			p.add("mail address", mail)
			if at := strings.IndexByte(mail, '@'); at > 0 {
				p.add("user name", mail[:at])
				p.add("domain name", mail[at+1:])
			}
		}
		for _, d := range domainRe.FindAllString(m, -1) {
			p.add("domain name", d)
		}
		for _, mm := range atLocalRe.FindAllStringSubmatch(m, -1) {
			p.add("user name", mm[1])
		}
	}
	for _, re := range []*regexp.Regexp{userEqRe, forMailRe, forUserRe} {
		for _, mm := range re.FindAllStringSubmatch(m, -1) {
			p.add("user name", mm[1])
		}
	}
}

func (p *privateSet) addEntry(fields []exField) {
	for _, f := range fields {
		v := string(f.value)
		switch f.name {
		case "_HOSTNAME":
			p.addHost(v)
		case "_MACHINE_ID", "_BOOT_ID":
			p.add("identity", v)
		case "_SYSTEMD_UNIT", "_SYSTEMD_USER_UNIT":
			p.add("unit", v)
		case "SYSLOG_IDENTIFIER":
			p.add("identifier", v)
		case "_COMM", "_EXE", "_CMDLINE":
			p.add("process", v)
		case "MESSAGE":
			p.addMessage(v)
		}
	}
}

// collectPrivates reads every entry of a real journal file through journalctl; nothing it reads is printed.
func collectPrivates(path string, set *privateSet) (entries int, err error) {
	cmd := exec.Command("journalctl", "--file="+path, "-o", "export", "--no-pager")
	out, err := cmd.StdoutPipe()
	if err != nil {
		return 0, err
	}
	if err := cmd.Start(); err != nil {
		return 0, err
	}
	rerr := readExport(out, func(fields []exField) {
		entries++
		set.addEntry(fields)
	})
	_, _ = io.Copy(io.Discard, out)
	if werr := cmd.Wait(); werr != nil {
		return entries, errors.New("journalctl failed")
	}
	return entries, rerr
}

// scrub blanks every token that is exactly one of the public, generic values.
func scrub(v string) string {
	return tokenRe.ReplaceAllStringFunc(v, func(tok string) string {
		if publicTerm[tok] {
			return " "
		}
		return tok
	})
}

// leakClasses returns the classes of the private values found in v: a private value of 6 bytes or more anywhere in it, a shorter one
// only as the whole value.
func (p *privateSet) leakClasses(v string, exact bool) []string {
	low := strings.ToLower(v)
	if !exact {
		low = strings.ToLower(scrub(v))
	}
	whole := strings.TrimSpace(low)
	found := map[string]bool{}
	for val, class := range p.class {
		if len(val) >= 6 && strings.Contains(low, val) || len(val) < 6 && whole == val {
			found[class] = true
		}
	}
	var out []string
	for c := range found {
		out = append(out, c)
	}
	sort.Strings(out)
	return out
}

func fixtureDir() string { return filepath.Join("..", "..", "testdata", "journal") }

func loadRealPrivates(t *testing.T) *privateSet {
	t.Helper()
	dir := os.Getenv(realDirEnv)
	if dir == "" {
		t.Skipf("%s is not set", realDirEnv)
	}
	if _, err := exec.LookPath("journalctl"); err != nil {
		t.Skip("journalctl is not installed")
	}
	names := realJournalNames(t, dir)
	set := &privateSet{class: map[string]string{}}
	for _, name := range names {
		n, err := collectPrivates(filepath.Join(dir, name), set)
		if err != nil || n == 0 {
			t.Fatalf("reading a real journal file: %d entries, error %v", n, err)
		}
	}
	if len(set.class) < 20 {
		t.Fatalf("collected only %d private values from the real files, want the host, units, comms and mail data", len(set.class))
	}
	t.Logf("checking against %d private values of %d real files", len(set.class), len(names))
	return set
}

// realJournalNames lists the journal files of dir without the code under test.
func realJournalNames(t *testing.T, dir string) []string {
	t.Helper()
	ents, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("reading the real journal directory: %v", err)
	}
	var names []string
	for _, e := range ents {
		n := e.Name()
		if strings.HasPrefix(n, "system") && (strings.HasSuffix(n, ".journal") || strings.HasSuffix(n, ".journal~")) {
			names = append(names, n)
		}
	}
	if len(names) == 0 {
		t.Fatal("found no journal file in the real journal directory")
	}
	return names
}

func TestFixture_HoldsNoRealPrivateValue(t *testing.T) {
	set := loadRealPrivates(t)
	t.Run("export", func(t *testing.T) {
		data, err := os.ReadFile(filepath.Join(fixtureDir(), "kernel.export"))
		if err != nil {
			t.Fatalf("reading the export: %v", err)
		}
		checkExportAgainst(t, set, data)
	})
	t.Run("journal files", func(t *testing.T) {
		files, _ := filepath.Glob(filepath.Join(fixtureDir(), "system*.journal*"))
		if len(files) != 3 {
			t.Fatalf("found %d generated journal files in testdata/journal, want 3 (online, archived and set aside)", len(files))
		}
		for i, path := range files {
			checkJournalFileAgainst(t, set, i, path)
		}
	})
}

func checkExportAgainst(t *testing.T, set *privateSet, data []byte) {
	t.Helper()
	i := 0
	err := readExport(bytes.NewReader(data), func(fields []exField) {
		i++
		for _, f := range fields {
			if classes := set.leakClasses(string(f.value), false); len(classes) > 0 {
				t.Errorf("export entry %d, field %s holds a private value of class %q", i, f.name, classes)
			}
		}
	})
	if err != nil || i == 0 {
		t.Fatalf("reading the export: %d entries, error %v", i, err)
	}
}

// checkJournalFileAgainst scans the objects of a generated journal file and its header IDs.
func checkJournalFileAgainst(t *testing.T, set *privateSet, idx int, path string) {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil || len(b) < fixedHeaderSize {
		t.Fatalf("reading generated file %d: %d bytes, error %v", idx, len(b), err)
	}
	for _, off := range []int{hdrFileID, hdrMachineID, hdrBootID, hdrSeqnumID} {
		if classes := set.leakClasses(hex.EncodeToString(b[off:off+16]), true); len(classes) > 0 {
			t.Errorf("generated file %d: header ID at %d holds a private value of class %q", idx, off, classes)
		}
	}
	hs, arena := le.Uint64(b[hdrHeaderSize:]), le.Uint64(b[hdrArenaSize:])
	end := min(uint64(len(b)), hs+arena)
	for off := hs; off+16 <= end; {
		size := le.Uint64(b[off+8:])
		if size < 16 || off+size > end {
			t.Fatalf("generated file %d: object at %d has size %d, want a well-formed file", idx, off, size)
		}
		switch b[off] {
		case objData:
			checkDataObject(t, set, idx, b[off:off+size])
		case objEntry:
			if classes := set.leakClasses(hex.EncodeToString(b[off+40:off+56]), true); len(classes) > 0 {
				t.Errorf("generated file %d: entry at %d holds a private boot ID of class %q", idx, off, classes)
			}
		}
		off += (size + 7) &^ 7
	}
}

func checkDataObject(t *testing.T, set *privateSet, idx int, obj []byte) {
	t.Helper()
	payload := obj[64:]
	compressed := obj[1] != 0
	value := payload
	if i := bytes.IndexByte(payload, '='); i >= 0 && !compressed {
		value = payload[i+1:]
	}
	if classes := set.leakClasses(string(value), compressed); len(classes) > 0 {
		t.Errorf("generated file %d: data object of field %q holds a private value of class %q", idx, fieldNameOf(payload, compressed), classes)
	}
}

// fieldNameOf returns the field name of a data payload for a message; a compressed payload has none.
func fieldNameOf(payload []byte, compressed bool) string {
	if compressed {
		return "(compressed)"
	}
	if i := bytes.IndexByte(payload, '='); i >= 0 && i < 64 {
		return string(payload[:i])
	}
	return "(unnamed)"
}

func TestFile_RealServerJournals(t *testing.T) {
	dir := os.Getenv(realDirEnv)
	if dir == "" {
		t.Skipf("%s is not set", realDirEnv)
	}
	names, err := ListFiles(os.DirFS(dir))
	if err != nil || len(names) != 2 {
		t.Fatalf("ListFiles() = %d files, error %v, want 2 (system.journal and one archived file)", len(names), err)
	}
	kills := 0
	for _, n := range names {
		want := 1456
		if strings.HasPrefix(n, "system@") {
			want = 2107
		}
		got := readRealFile(t, filepath.Join(dir, n))
		kills += got.kills
		if got.kernel != want {
			t.Errorf("%s: %d kernel entries, want %d", archivedOrOnline(n), got.kernel, want)
		}
		if got.compressed != 0 {
			t.Errorf("%s: %d kernel entries with a compressed item, want 0", archivedOrOnline(n), got.compressed)
		}
		t.Logf("%s: %d kernel entries, %d kills, %d with a compressed item", archivedOrOnline(n), got.kernel, got.kills, got.compressed)
	}
	if kills != 8 {
		t.Errorf("%d kill lines in both files, want 8", kills)
	}
}

func archivedOrOnline(n string) string {
	if strings.HasPrefix(n, "system@") {
		return "archived file"
	}
	return "online file"
}

type realCounts struct{ kernel, kills, compressed int }

func readRealFile(t *testing.T, path string) realCounts {
	t.Helper()
	fh, err := os.Open(path)
	if err != nil {
		t.Fatalf("opening a real file: %v", err)
	}
	defer func() { _ = fh.Close() }()
	st, err := fh.Stat()
	if err != nil {
		t.Fatalf("stat of a real file: %v", err)
	}
	f, err := Open(fh, st.Size(), false)
	if err != nil {
		t.Fatalf("Open() of a real file: %v", err)
	}
	d, found, err := f.Lookup([]byte(kernelPayload))
	if err != nil || !found {
		t.Fatalf("Lookup(%q) in a real file = found %v, err %v, want found true", kernelPayload, found, err)
	}
	c, err := f.Seek(d, 0)
	if err != nil {
		t.Fatalf("Seek() in a real file: %v", err)
	}
	var out realCounts
	var e Entry
	for {
		ok, err := f.Next(&c, []string{"MESSAGE", "SYSLOG_FACILITY"}, &e)
		if err != nil {
			t.Fatalf("Next() in a real file after %d entries: %v", out.kernel, err)
		}
		if !ok {
			return out
		}
		out.kernel++
		out.count(&e)
	}
}

func (r *realCounts) count(e *Entry) {
	for _, f := range e.Fields {
		if f.Compressed {
			r.compressed++
			return
		}
	}
	m, ok := e.Field("MESSAGE")
	fac, _ := e.Field("SYSLOG_FACILITY")
	if ok && len(m) == 1 && len(fac) == 1 && string(fac[0].Value) == "0" && strings.HasPrefix(string(m[0].Value), "Out of memory: Killed process ") {
		r.kills++
	}
}
