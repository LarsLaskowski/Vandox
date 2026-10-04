package config

import (
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"
	"unicode/utf16"
)

// testSection is a nested section of testDoc.
type testSection struct {
	Name  string `yaml:"name"`
	Other string `yaml:"other"`
}

// testDoc is the white-box target of decodeStrict. It has fields of every kind the walker must handle.
type testDoc struct {
	Title   string        `yaml:"title"`
	Count   int           `yaml:"count"`
	Flag    bool          `yaml:"flag"`
	Wait    time.Duration `yaml:"wait"`
	Tags    []string      `yaml:"tags"`
	Section testSection   `yaml:"section"`
	Skip    string        `yaml:"-"`
}

func testDocDefaults() testDoc {
	return testDoc{Title: "def-title", Count: 7, Section: testSection{Name: "def-name", Other: "def-other"}, Skip: "def-skip"}
}

func decodeDoc(t *testing.T, doc string) (testDoc, map[string]int, error) {
	t.Helper()
	got := testDocDefaults()
	lines, err := decodeStrict("test.yaml", []byte(doc), &got)
	return got, lines, err
}

func TestDecodeStrict_Valid(t *testing.T) {
	t.Run("values are set and lines recorded", func(t *testing.T) {
		doc := "title: hello\ncount: 5\nflag: true\nwait: 5s\nsection:\n  name: nm\n"
		got, lines, err := decodeDoc(t, doc)
		if err != nil {
			t.Fatalf("decodeStrict(%q) error = %v, want nil", doc, err)
		}
		want := testDocDefaults()
		want.Title, want.Count, want.Flag, want.Wait, want.Section.Name = "hello", 5, true, 5*time.Second, "nm"
		if !reflect.DeepEqual(got, want) {
			t.Errorf("decodeStrict(%q) = %+v, want %+v", doc, got, want)
		}
		wantLines := map[string]int{"title": 1, "count": 2, "flag": 3, "wait": 4, "section.name": 6}
		for k, l := range wantLines {
			if lines[k] != l {
				t.Errorf("decodeStrict(%q) lines[%q] = %d, want %d", doc, k, lines[k], l)
			}
		}
	})

	defaultsKept := []struct {
		name string
		doc  string
	}{
		{"empty file", ""},
		{"only comments", "# a\n# b\n"},
		{"document marker only", "---\n"},
		{"explicit null", "~\n"},
		{"null word", "null\n"},
		{"empty section", "section:\n"},
		{"section null tilde", "section: ~\n"},
		{"section tagged null", "section: !!null\n"},
		{"section tagged null tilde", "section: !!null ~\n"},
		{"section children commented out", "section:\n  # name: x\n"},
		{"empty flow mapping", "{}\n"},
		{"directive yaml 1.1", "%YAML 1.1\n---\n{}\n"},
	}
	for _, tt := range defaultsKept {
		t.Run("defaults kept: "+tt.name, func(t *testing.T) {
			got, _, err := decodeDoc(t, tt.doc)
			if err != nil {
				t.Fatalf("decodeStrict(%q) error = %v, want nil", tt.doc, err)
			}
			if want := testDocDefaults(); !reflect.DeepEqual(got, want) {
				t.Errorf("decodeStrict(%q) = %+v, want the defaults %+v", tt.doc, got, want)
			}
		})
	}

	titles := []struct {
		name string
		doc  string
		want string
	}{
		{"plain int taken as text", "title: 5\n", "5"},
		{"plain bool taken as text", "title: true\n", "true"},
		{"quoted key", "\"title\": x\n", "x"},
		{"flow mapping", "{title: x}\n", "x"},
		{"verbatim str tag", "title: !<tag:yaml.org,2002:str> x\n", "x"},
		{"non-specific tag", "title: ! x\n", "x"},
		{"explicit str tag", "title: !!str 12\n", "12"},
		{"utf-8 bom", "\xEF\xBB\xBFtitle: x\n", "x"},
		{"utf-16 le bom", string(utf16LE("title: x\n")), "x"},
	}
	for _, tt := range titles {
		t.Run(tt.name, func(t *testing.T) {
			got, _, err := decodeDoc(t, tt.doc)
			if err != nil {
				t.Fatalf("decodeStrict(%q) error = %v, want nil", tt.doc, err)
			}
			if got.Title != tt.want {
				t.Errorf("decodeStrict(%q) Title = %q, want %q", tt.doc, got.Title, tt.want)
			}
		})
	}
}

// utf16LE encodes s as UTF-16 little endian with a byte order mark.
func utf16LE(s string) []byte {
	out := []byte{0xFF, 0xFE}
	for _, u := range utf16.Encode([]rune(s)) {
		out = append(out, byte(u), byte(u>>8))
	}
	return out
}

func TestDecodeStrict_Errors(t *testing.T) {
	long := sentinel + strings.Repeat("a", 24) // 32 bytes
	tests := []struct {
		name string
		doc  string
		key  string
		line int
	}{
		// AC5: unknown keys
		{"unknown top-level key", "agnet_id: x\n", "agnet_id", 1},
		{"unknown key in section", "title: a\nsection:\n  nam: x\n", "section.nam", 3},
		{"keys are case-sensitive", "Title: x\n", "Title", 1},
		{"field without yaml name is unknown", "skip: x\n", "skip", 1},
		{"scalar non-string key is unknown", "1: x\n", "1", 1},
		{"key with hyphen and underscore is shown", "a-b_c: x\n", "a-b_c", 1},
		{"key of 31 bytes is shown", strings.Repeat("a", 31) + ": x\n", strings.Repeat("a", 31), 1},

		// AC5: names that are not shown
		{"newline in top-level key", "\"a\\nb\": 1\n", "", 1},
		{"newline in section key", "section:\n  \"a\\nb\": 1\n", "section", 2},
		{"bidi in top-level key", "\"x\\u202Ey\": 1\n", "", 1},
		{"bidi in section key", "section:\n  \"x\\u202Ey\": 1\n", "section", 2},
		{"key of 32 bytes", long + ": 1\n", "", 1},
		{"key of 32 bytes in section", "section:\n  " + long + ": 1\n", "section", 2},
		{"block scalar key", "? |\n  " + sentinel + "\n: 1\n", "", 1},
		{"quoted key with dot", "\"a.b\": 1\n", "", 1},
		{"key with colon", "\"a:b\": 1\n", "", 1},
		{"key with dollar", "\"$argon\": 1\n", "", 1},
		{"non-ascii key", "\"k\u00e4\": 1\n", "", 1},

		// AC5: secret-like keys are unknown too
		{"token key", "token: " + sentinel + "-value\n", "token", 1},
		{"upper-case token key", "TOKEN: x\n", "TOKEN", 1},
		{"password in section", "section:\n  Password: " + sentinel + "-value\n", "section.Password", 2},
		{"secret in section", "section:\n  my_secret: x\n", "section.my_secret", 2},
		{"bot token in section", "section:\n  bot_token: x\n", "section.bot_token", 2},

		// AC7: unsupported YAML constructs
		{"duplicate top-level key", "title: a\ntitle: b\n", "title", 2},
		{"duplicate key in section", "section:\n  name: a\n  name: b\n", "section.name", 3},
		{"second document", "title: a\n---\ntitle: b\n", "", 2},
		{"second document after null", "~\n---\ntitle: x\n", "", 2},
		{"second document after empty", "---\n---\n", "", 2},
		{"trailing empty document", "title: x\n---\n", "", 2},
		{"anchor", "title: &a x\n", anyKey, 1},
		{"alias", "section:\n  name: &a x\n  other: *a\n", anyKey, 2},
		{"merge key flow", "<<: {title: x}\n", anyKey, 1},
		{"merge key in section", "section:\n  <<: {name: x}\n", anyKey, 2},
		{"non-string key", "? [a]\n: 1\n", anyKey, 1},
		{"custom tag", "title: !env X\n", anyKey, 1},
		{"custom tag on mapping", "section: !foo {name: x}\n", anyKey, 1},
		{"binary tag", "title: !!binary aGk=\n", anyKey, 1},
		{"verbatim binary tag", "title: !<tag:yaml.org,2002:binary> aGk=\n", anyKey, 1},
		{"set tag", "title: !!set {x}\n", anyKey, 1},
		{"omap tag", "title: !!omap [{x: 1}]\n", anyKey, 1},
		{"tag handle remapped to evil prefix", "%TAG !! tag:evil.example,2000:\n---\ntitle: !!str x\n", anyKey, 3},
		{"tag handle remapped to binary", "%TAG !e! tag:yaml.org,2002:\n---\ntitle: !e!binary aGk=\n", anyKey, 3},
		{"top-level sequence", "- a\n", "", 1},
		{"top-level scalar", "foo\n", "", 1},
		{"null tag with content on section", "section: !!null " + sentinel + "\n", "section", 1},
		{"null tag with content on leaf", "title: !!null x\n", "title", 1},

		// AC6: leaf kinds and values
		{"leaf null", "title:\n", "title", 1},
		{"leaf null tilde", "title: ~\n", "title", 1},
		{"leaf mapping", "title: {a: b}\n", "title", 1},
		{"leaf sequence", "title: [a]\n", "title", 1},
		{"leaf nested deeper", "title:\n  a: b\n", "title", 1},
		{"section scalar", "section: " + sentinel + "\n", "section", 1},
		{"section sequence", "section: [a]\n", "section", 1},
		{"leaf in section null", "section:\n  name:\n", "section.name", 2},
		{"tab character", "title: \"a\\tb\"\n", "title", 1},
		{"control character", "title: \"a" + sentinel + "\\x01b\"\n", "title", 1},
		{"del character", "title: \"a\\x7Fb\"\n", "title", 1},
		{"c1 character", "title: \"a\\x85b\"\n", "title", 1},
		{"block scalar newline", "title: |\n  " + sentinel + "\n", "title", 1},
		{"line separator", "title: \"a\\u2028" + sentinel + "\"\n", "title", 1},
		{"paragraph separator", "title: \"a\\u2029" + sentinel + "\"\n", "title", 1},
		{"bidi override", "title: \"a\\u202E" + sentinel + "\"\n", "title", 1},
		{"zero width no-break space", "title: \"\\uFEFFa" + sentinel + "\"\n", "title", 1},
		{"zero width space", "title: \"a\\u200B\"\n", "title", 1},
		{"control character in section", "section:\n  name: \"a\\x01\"\n", "section.name", 2},
		{"int field not a number", "count: " + sentinel + "-value\n", "count", 1},
		{"bool field not a bool", "flag: " + sentinel + "-value\n", "flag", 1},
		{"duration field not a duration", "wait: " + sentinel + "-value\n", "wait", 1},
		{"slice field given a scalar", "tags: " + sentinel + "-value\n", "tags", 1},
		{"int field given a mapping", "count: {a: b}\n", "count", 1},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, _, err := decodeDoc(t, tt.doc)
			ke := requireKeyError(t, err, tt.key, tt.line)
			if ke.File != "test.yaml" {
				t.Errorf("KeyError.File = %q, want %q", ke.File, "test.yaml")
			}
			requireNoLeak(t, err, sentinel, "\n", "\u202e", "\u2028", "evil.example", "!env", "binary")
		})
	}
}

func TestDecodeStrict_ErrorTextHints(t *testing.T) {
	secretKeys := []string{"token: x\n", "section:\n  password_hash: x\n", "section:\n  Secret: x\n"}
	for _, doc := range secretKeys {
		t.Run("secret hint "+strings.TrimSpace(doc), func(t *testing.T) {
			_, _, err := decodeDoc(t, doc)
			ke := requireKeyError(t, err, anyKey, anyLine)
			if !strings.Contains(strings.ToLower(ke.Reason), "environment variable") {
				t.Errorf("decodeStrict(%q) reason = %q, want it to say that secrets come only from environment variables", doc, ke.Reason)
			}
		})
	}
	t.Run("plain unknown key has no secret hint", func(t *testing.T) {
		_, _, err := decodeDoc(t, "titel: x\n")
		ke := requireKeyError(t, err, "titel", 1)
		if strings.Contains(strings.ToLower(ke.Reason), "environment") {
			t.Errorf("decodeStrict reason = %q, want no secret hint for a plain unknown key", ke.Reason)
		}
	})
	t.Run("unsafe name says it is not shown", func(t *testing.T) {
		_, _, err := decodeDoc(t, "\"a\\nb\": 1\n")
		ke := requireKeyError(t, err, "", 1)
		if !strings.Contains(ke.Reason, "name not shown") {
			t.Errorf("decodeStrict reason = %q, want it to contain %q", ke.Reason, "name not shown")
		}
	})
}

func TestDecodeStrict_ParserErrors(t *testing.T) {
	const wantReason = "not valid YAML (syntax error or alias to an undefined anchor)"
	tests := []struct {
		name string
		doc  string
		line int
	}{
		{"unclosed flow sequence", "a: [\n", 1},
		{"undefined alias", "a: *" + sentinel + "\n", 0},
		{"nested mapping on one line", "a: b: c\n", 0},
		{"unknown escape", "a: \"\\q\"\n", 0},
		{"undefined tag handle", "a: !x!y z\n", 0},
		{"error on line two", "a: 1\n" + sentinel + ": [\n", 2},
		{"error in second document", "a: 1\n---\nb: [\n", 3},
		{"yaml 1.2 directive", "%YAML 1.2\n---\na: 1\n", anyLine},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			_, _, err := decodeDoc(t, tt.doc)
			ke := requireKeyError(t, err, "", tt.line)
			if ke.Reason != wantReason {
				t.Errorf("KeyError.Reason = %q, want %q", ke.Reason, wantReason)
			}
			if u := errors.Unwrap(err); u != nil {
				t.Errorf("errors.Unwrap(err) = %v, want nil (no yaml text passed through)", u)
			}
			if !strings.HasPrefix(err.Error(), "config: test.yaml:") {
				t.Errorf("err.Error() = %q, want it to begin with %q", err.Error(), "config: test.yaml:")
			}
			requireNoLeak(t, err, sentinel, "yaml:", "unknown anchor")
		})
	}
}
