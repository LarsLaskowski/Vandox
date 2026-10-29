package agent

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	manifestName = "MANIFEST.sha256"

	// entryStubCommon is a common.sh stand-in with two functions and a last line that prints "sourced".
	entryStubCommon = "first_function() { :; }\nsecond_function() { :; }\necho sourced\nexit 0\n"

	// manifestBound is the size the entry check allows the manifest (record 0131, option 34).
	manifestBound = 4096
)

// manifestNames are the 7 members the manifest lists, in the order of the manifest.
var manifestNames = []string{
	"VERSION", "agent.yaml", "common.sh", "mariadb-user-remove.sql", "mariadb-user.sql", pinName, "vandox-agent.service",
}

// bundleUnit is a unit stand-in of several lines, so a truncated copy differs from it.
const bundleUnit = "[Unit]\nDescription=Vandox monitoring agent\n\n[Service]\nExecStart=/usr/local/bin/vandox-agent\nRestart=on-failure\n\n[Install]\nWantedBy=multi-user.target\n"

// writeBundle writes the 7 manifest members into dir (common.sh with the given content) and a valid
// MANIFEST.sha256 for them. The scripts are not listed in a manifest and are written by the caller.
func writeBundle(t *testing.T, dir, commonSh string) {
	t.Helper()

	members := map[string]string{
		"VERSION":                 "v1.2.3\n",
		"agent.yaml":              "agent_id: web-1\n",
		"common.sh":               commonSh,
		"mariadb-user-remove.sql": "DROP USER IF EXISTS 'vandox-agent'@'localhost';\n",
		"mariadb-user.sql":        "CREATE USER 'vandox-agent'@'localhost';\n",
		pinName:                   sha256Hex([]byte("binary")) + "  " + binaryName + "\n",
		"vandox-agent.service":    bundleUnit,
	}

	for name, content := range members {
		writeBytes(t, filepath.Join(dir, name), content)
	}

	writeBytes(t, filepath.Join(dir, manifestName), strings.Join(manifestLines(t, dir), "\n")+"\n")
}

// manifestLines returns the 7 sha256sum lines (without line feed) of the members now in dir.
func manifestLines(t *testing.T, dir string) []string {
	t.Helper()

	lines := make([]string, 0, len(manifestNames))

	for _, name := range manifestNames {
		data, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			t.Fatalf("read %s: %v", name, err)
		}

		lines = append(lines, sha256Hex(data)+"  "+name)
	}

	return lines
}

// manifestLine is the refusal line of an incomplete or changed bundle.
func manifestLine(script string) string {
	return script + ": the bundle is incomplete or changed; extract it again in a new directory\n"
}

// manifestCase is one change to a good bundle.
type manifestCase struct {
	name   string
	change func(t *testing.T, dir string)
	// ownerLineToo says the line of the owner and mode check, which names the manifest, is as good a refusal
	// as the manifest line (the entry check may stop on a link or a missing file before it reads the manifest).
	ownerLineToo bool
}

// setLines replaces the manifest by the given lines, each followed by a line feed.
func setLines(lines ...string) func(t *testing.T, dir string) {
	return func(t *testing.T, dir string) {
		t.Helper()

		text := strings.Join(lines, "\n")
		if len(lines) > 0 {
			text += "\n"
		}

		writeBytes(t, filepath.Join(dir, manifestName), text)
	}
}

// editLines applies edit to the 7 good lines and writes the result with line feeds.
func editLines(edit func(lines []string) []string) func(t *testing.T, dir string) {
	return func(t *testing.T, dir string) {
		t.Helper()

		setLines(edit(manifestLines(t, dir))...)(t, dir)
	}
}

// replaceMember rewrites a member after the manifest was made.
func replaceMember(name string, edit func(old string) string) func(t *testing.T, dir string) {
	return func(t *testing.T, dir string) {
		t.Helper()

		data, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			t.Fatalf("read %s: %v", name, err)
		}

		writeBytes(t, filepath.Join(dir, name), edit(string(data)))
	}
}

// manifestCases are the changes the entry check must refuse before anything is sourced.
func manifestCases() []manifestCase {
	hashOf := func(s string) string { return sha256Hex([]byte(s)) }

	return []manifestCase{
		{name: "manifest missing", change: func(t *testing.T, dir string) {
			t.Helper()
			removeFile(t, filepath.Join(dir, manifestName))
		}},
		{name: "manifest a link", ownerLineToo: true, change: func(t *testing.T, dir string) {
			t.Helper()

			target := filepath.Join(filepath.Dir(dir), "manifest-target")
			if err := os.Rename(filepath.Join(dir, manifestName), target); err != nil {
				t.Fatalf("move manifest: %v", err)
			}

			if err := os.Symlink(target, filepath.Join(dir, manifestName)); err != nil {
				t.Fatalf("symlink: %v", err)
			}
		}},
		{name: "manifest a directory", change: func(t *testing.T, dir string) {
			t.Helper()
			removeFile(t, filepath.Join(dir, manifestName))

			if err := os.Mkdir(filepath.Join(dir, manifestName), 0o755); err != nil {
				t.Fatalf("mkdir: %v", err)
			}
		}},
		{name: "manifest of 4097 bytes", change: func(t *testing.T, dir string) {
			t.Helper()

			good := strings.Join(manifestLines(t, dir), "\n") + "\n"
			writeBytes(t, filepath.Join(dir, manifestName), good+strings.Repeat("#", manifestBound+1-len(good)))
		}},
		{name: "empty manifest", change: setLines()},
		{name: "six lines", change: editLines(func(l []string) []string { return l[:6] })},
		{name: "eight lines", change: editLines(func(l []string) []string {
			return append(l, hashOf("x")+"  install.sh")
		})},
		{name: "seven good lines and an eighth partial line without a line feed", change: func(t *testing.T, dir string) {
			t.Helper()
			writeBytes(t, filepath.Join(dir, manifestName), strings.Join(manifestLines(t, dir), "\n")+"\nabcd")
		}},
		{name: "a name twice", change: editLines(func(l []string) []string {
			l[1] = l[0]

			return l
		})},
		{name: "two lines swapped", change: editLines(func(l []string) []string {
			l[0], l[1] = l[1], l[0]

			return l
		})},
		{name: "an unknown name", change: editLines(func(l []string) []string {
			l[6] = hashOf("x") + "  other.txt"

			return l
		})},
		{name: "a name with a path", change: editLines(func(l []string) []string {
			l[6] = hashOf("x") + "  ../x"

			return l
		})},
		{name: "upper-case hex", change: editLines(func(l []string) []string {
			l[0] = strings.ToUpper(l[0][:64]) + l[0][64:]

			return l
		})},
		{name: "one space", change: editLines(func(l []string) []string {
			l[0] = l[0][:64] + " " + l[0][66:]

			return l
		})},
		{name: "a carriage return", change: editLines(func(l []string) []string {
			l[0] += asciiCR

			return l
		})},
		{name: "no final line feed", change: func(t *testing.T, dir string) {
			t.Helper()
			writeBytes(t, filepath.Join(dir, manifestName), strings.Join(manifestLines(t, dir), "\n"))
		}},
		{name: "common.sh missing", change: func(t *testing.T, dir string) {
			t.Helper()
			removeFile(t, filepath.Join(dir, "common.sh"))
		}},
		{name: "a member missing", change: func(t *testing.T, dir string) {
			t.Helper()
			removeFile(t, filepath.Join(dir, "agent.yaml"))
		}},
		{name: "one byte changed", change: replaceMember("VERSION", func(string) string { return "v1.2.4\n" })},
		{name: "common.sh truncated at a function boundary", change: replaceMember("common.sh", func(old string) string {
			head, _, _ := strings.Cut(old, "echo sourced")

			return head
		})},
		{name: "unit truncated", change: replaceMember("vandox-agent.service", func(old string) string {
			return old[:len(old)/2]
		})},
	}
}

// removeFile removes a file or an empty directory.
func removeFile(t *testing.T, path string) {
	t.Helper()

	if err := os.Remove(path); err != nil {
		t.Fatalf("remove %s: %v", path, err)
	}
}

// TestManifest_RefusesAnIncompleteOrChangedBundle runs a copy of each script as a program beside a stub common.sh
// that prints "sourced": the packaged set is accepted, and every missing, reordered, malformed or changed
// member is refused with the manifest line, exit 1, before anything is sourced.
func TestManifest_RefusesAnIncompleteOrChangedBundle(t *testing.T) {
	t.Parallel()

	for _, script := range []string{installScript, uninstallScript} {
		t.Run(script+" accepts the packaged set", func(t *testing.T) {
			t.Parallel()

			runManifestCase(t, script, manifestCase{name: "packaged set", change: func(*testing.T, string) {}})
		})

		for _, tc := range manifestCases() {
			t.Run(script+" "+tc.name, func(t *testing.T) {
				t.Parallel()

				runManifestCase(t, script, tc)
			})
		}
	}
}

// runManifestCase builds a good bundle, applies the change and runs the script as a program.
func runManifestCase(t *testing.T, script string, tc manifestCase) {
	t.Helper()

	h := newHarness(t)
	h.passthrough("sha256sum")
	h.statTable()

	root, err := filepath.EvalSymlinks(h.dir)
	if err != nil {
		t.Fatalf("resolve %s: %v", h.dir, err)
	}

	bundle := filepath.Join(root, "bundle")
	if err := os.Mkdir(bundle, 0o755); err != nil {
		t.Fatalf("mkdir %s: %v", bundle, err)
	}

	source, err := os.ReadFile(shippedFile(t, script))
	if err != nil {
		t.Fatalf("read %s: %v", script, err)
	}

	if err := os.WriteFile(filepath.Join(bundle, script), source, 0o755); err != nil {
		t.Fatalf("write %s: %v", script, err)
	}

	writeBundle(t, bundle, entryStubCommon)
	tc.change(t, bundle)

	got := runProgram(t, h, filepath.Join(bundle, script))

	if tc.name == "packaged set" {
		expectResult(t, script+" with the packaged set", got, 0, "sourced\n", "")

		return
	}

	assertManifestRefusal(t, script, tc, got, filepath.Join(bundle, manifestName))
}

// assertManifestRefusal checks exit 1, nothing sourced and the manifest line (or, where allowed, the owner line).
func assertManifestRefusal(t *testing.T, script string, tc manifestCase, got shellResult, manifest string) {
	t.Helper()

	if got.code != 1 || got.stdout != "" {
		t.Errorf("%s %s: exit status %d, standard output %q, want 1 and nothing sourced (standard error %q)",
			script, tc.name, got.code, got.stdout, got.stderr)

		return
	}

	if got.stderr == manifestLine(script) || (tc.ownerLineToo && got.stderr == bundleDirLine(script, manifest)) {
		return
	}

	t.Errorf("%s %s: standard error = %q, want %q", script, tc.name, got.stderr, manifestLine(script))
}
