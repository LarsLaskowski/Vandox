package journal

import (
	"errors"
	"io"
	"io/fs"
	"sort"
	"strings"
)

const (
	maxFiles     = 1024
	listChunk    = 256
	machineIDLen = 32
	// maxMachineFile bounds what is read of /etc/machine-id: the ID and a line feed.
	maxMachineFile = 64
)

var errMachineID = errors.New("journal: machine ID not readable")

// ListFiles returns the base names of the journal files in dir: system.journal, system@*.journal and system@*.journal~ (a file set
// aside after an unclean shutdown), sorted, at most 1,024. The directory is read in chunks.
func ListFiles(dir fs.FS) ([]string, error) {
	d, err := dir.Open(".")
	if err != nil {
		return nil, err
	}
	defer func() { _ = d.Close() }()
	rd, ok := d.(fs.ReadDirFile)
	if !ok {
		return nil, errors.New("journal: directory not listable")
	}
	var names []string
	for len(names) < maxFiles {
		chunk, err := rd.ReadDir(listChunk)
		for _, e := range chunk {
			if e.Type().IsRegular() && isJournalName(e.Name()) && len(names) < maxFiles {
				names = append(names, e.Name())
			}
		}
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, err
		}
	}
	sort.Strings(names)
	return names, nil
}

// isJournalName tells whether name is one of the journal file names the reader takes.
func isJournalName(name string) bool {
	if name == "system.journal" {
		return true
	}
	rest, ok := strings.CutPrefix(name, "system@")
	return ok && (strings.HasSuffix(rest, ".journal") || strings.HasSuffix(rest, ".journal~")) && strings.IndexByte(rest, '/') < 0
}

// MachineDir returns the machine ID directory name read from /etc/machine-id in etc: 32 lower-case hex digits and an optional line
// feed, nothing else.
func MachineDir(etc fs.FS) (string, error) {
	f, err := etc.Open("machine-id")
	if err != nil {
		return "", errMachineID
	}
	defer func() { _ = f.Close() }()
	b, err := io.ReadAll(io.LimitReader(f, maxMachineFile))
	if err != nil {
		return "", errMachineID
	}
	id := strings.TrimSuffix(string(b), "\n")
	if len(id) != machineIDLen || strings.Trim(id, "0123456789abcdef") != "" {
		return "", errMachineID
	}
	return id, nil
}
