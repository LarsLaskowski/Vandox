package agent

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"reflect"
	"regexp"
	"slices"
	"sort"
	"strings"
	"testing"
)

const (
	// tailnetPolicyFile is the example tailnet policy, relative to the repository root.
	tailnetPolicyFile = "deploy/agent/tailnet-policy.hujson"

	// agentReadmeFile is the agent's README, relative to the repository root.
	agentReadmeFile = "deploy/agent/README.md"

	// tailscaleHeading opens the README section the procedure lives in.
	tailscaleHeading = "## Tailscale on the monitored server"

	// checksHeading opens the README section with the check results.
	checksHeading = "## Checks on the server"

	// agentTag and backendTag are the two tags of the policy.
	agentTag   = "tag:vandox-agent"
	backendTag = "tag:vandox-backend"

	// settingsLine is the one `tailscale set` line of the join step.
	settingsLine = "tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false"
)

// stripHuJSON turns HuJSON into JSON: it removes // and /* */ comments and trailing commas outside strings. Inside a
// string, escapes are honored, so an escaped quote does not end the string and // or /* in a string stay content.
// It returns an error for a leading BOM, an unterminated block comment and an unterminated string.
func stripHuJSON(text string) (string, error) {
	if strings.HasPrefix(text, "\uFEFF") {
		return "", errors.New("leading byte order mark")
	}

	var out bytes.Buffer

	pendingComma := -1
	for i := 0; i < len(text); {
		c := text[i]

		switch {
		case c == '"':
			end, err := stringEnd(text, i)
			if err != nil {
				return "", err
			}

			out.WriteString(text[i:end])
			pendingComma = -1
			i = end
		case c == '/' && i+1 < len(text) && (text[i+1] == '/' || text[i+1] == '*'):
			end, err := commentEnd(text, i)
			if err != nil {
				return "", err
			}

			out.WriteByte(' ')
			i = end
		default:
			pendingComma = copyStructural(&out, c, pendingComma)
			i++
		}
	}

	return out.String(), nil
}

// copyStructural writes one byte outside strings and comments. A comma stays pending until the next non-space byte:
// before a closing brace or bracket it is dropped, which makes it a trailing comma. It returns the new pending index.
func copyStructural(out *bytes.Buffer, c byte, pendingComma int) int {
	if c == ' ' || c == '\t' || c == '\n' || c == '\r' {
		out.WriteByte(c)

		return pendingComma
	}

	if (c == '}' || c == ']') && pendingComma >= 0 {
		replaceByte(out, pendingComma, ' ')
	}

	if c == ',' {
		out.WriteByte(c)

		return out.Len() - 1
	}

	out.WriteByte(c)

	return -1
}

// replaceByte overwrites the byte at index of the buffer.
func replaceByte(out *bytes.Buffer, index int, value byte) {
	data := out.Bytes()
	data[index] = value
}

// stringEnd returns the index after the closing quote of the string that starts at start.
func stringEnd(text string, start int) (int, error) {
	for i := start + 1; i < len(text); i++ {
		switch text[i] {
		case '\\':
			i++
		case '"':
			return i + 1, nil
		}
	}

	return 0, errors.New("unterminated string")
}

// commentEnd returns the index after the comment that starts at start: after the line break of a line comment (the
// break itself is kept) or after the closing */ of a block comment.
func commentEnd(text string, start int) (int, error) {
	if text[start+1] == '/' {
		newline := strings.IndexByte(text[start:], '\n')
		if newline < 0 {
			return len(text), nil
		}

		return start + newline, nil
	}

	closing := strings.Index(text[start+2:], "*/")
	if closing < 0 {
		return 0, errors.New("unterminated block comment")
	}

	return start + 2 + closing + 2, nil
}

// decodeStrict decodes JSON into maps, slices and scalars token by token. It refuses a duplicate key at any level
// (keys compare case-sensitively) and data after the top value.
func decodeStrict(text string) (any, error) {
	decoder := json.NewDecoder(strings.NewReader(text))

	value, err := decodeValue(decoder)
	if err != nil {
		return nil, err
	}

	if _, err = decoder.Token(); errors.Is(err, io.EOF) {
		return value, nil
	}

	return nil, errors.New("data after the top value")
}

// decodeValue reads the next value from the decoder.
func decodeValue(decoder *json.Decoder) (any, error) {
	token, err := decoder.Token()
	if err != nil {
		return nil, err
	}

	delim, isDelim := token.(json.Delim)
	switch {
	case isDelim && delim == '{':
		return decodeObject(decoder)
	case isDelim && delim == '[':
		return decodeArray(decoder)
	default:
		return token, nil
	}
}

// decodeObject reads the members of an object whose opening brace is consumed.
func decodeObject(decoder *json.Decoder) (any, error) {
	object := map[string]any{}

	for decoder.More() {
		keyToken, err := decoder.Token()
		if err != nil {
			return nil, err
		}

		key, isString := keyToken.(string)
		if isString == false {
			return nil, fmt.Errorf("object key %v is not a string", keyToken)
		}

		if _, duplicate := object[key]; duplicate {
			return nil, fmt.Errorf("duplicate key %q", key)
		}

		value, err := decodeValue(decoder)
		if err != nil {
			return nil, err
		}

		object[key] = value
	}

	if _, err := decoder.Token(); err != nil {
		return nil, err
	}

	return object, nil
}

// decodeArray reads the elements of an array whose opening bracket is consumed.
func decodeArray(decoder *json.Decoder) (any, error) {
	array := []any{}

	for decoder.More() {
		value, err := decodeValue(decoder)
		if err != nil {
			return nil, err
		}

		array = append(array, value)
	}

	if _, err := decoder.Token(); err != nil {
		return nil, err
	}

	return array, nil
}

// compactJSON removes the insignificant whitespace of valid JSON.
func compactJSON(t *testing.T, text string) string {
	t.Helper()

	var out bytes.Buffer
	if err := json.Compact(&out, []byte(text)); err != nil {
		t.Fatalf("json.Compact(%q) = %v, want valid JSON", text, err)
	}

	return out.String()
}

func TestTailnetPolicy_StripsHuJSON(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name  string
		input string
		want  string
	}{
		{"line comment", "{\"a\": 1 // note\n}", `{"a":1}`},
		{"block comment", `{"a": /* note */ 1}`, `{"a":1}`},
		{"multi-line block comment", "{\"a\": 1 /* one\ntwo */}", `{"a":1}`},
		{"quotes inside a line comment", "{\"a\": 1 // it's \"quoted\"\n}", `{"a":1}`},
		{"slashes in a string", `{"a": "http://x"}`, `{"a":"http://x"}`},
		{"comment openers and closers in a string", `{"a": "/* not */ a comment"}`, `{"a":"/* not */ a comment"}`},
		{"escaped quote then slashes", `{"a": "q\" // still the string"}`, `{"a":"q\" // still the string"}`},
		{"escaped backslash then closing quote then comment", "{\"a\": \"b\\\\\" // comment\n}", `{"a":"b\\"}`},
		{"escaped backslash and quote", `{"a": "\\\" /* x */"}`, `{"a":"\\\" /* x */"}`},
		{"comma and brace in a string", `{"a": ",}"}`, `{"a":",}"}`},
		{"trailing comma before brace", `{"a": 1,}`, `{"a":1}`},
		{"trailing comma before bracket", `{"a": [1, 2,]}`, `{"a":[1,2]}`},
		{"trailing comma, comment, brace", "{\"a\": 1, // note\n}", `{"a":1}`},
		{"trailing comma, block comment, bracket", `{"a": [1, /* note */ ]}`, `{"a":[1]}`},
		{"trailing comma, comment, then more members", "{\"a\": 1, // note\n\"b\": 2}", `{"a":1,"b":2}`},
		{"commas between members stay", `{"a": [1,2], "b": 3}`, `{"a":[1,2],"b":3}`},
		{"CRLF line ends", "{\r\n  \"a\": 1, // note\r\n  \"b\": [2,],\r\n}\r\n", `{"a":1,"b":[2]}`},
		{"comment on the last line without a line break", "{\"a\": 1} // end", `{"a":1}`},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			got, err := stripHuJSON(tc.input)
			if err != nil {
				t.Fatalf("stripHuJSON(%q) error = %v, want none", tc.input, err)
			}

			if compact := compactJSON(t, got); compact != tc.want {
				t.Errorf("stripHuJSON(%q) = %q, want %q", tc.input, compact, tc.want)
			}
		})
	}
}

func TestTailnetPolicy_StripRefusesMalformedHuJSON(t *testing.T) {
	t.Parallel()

	cases := map[string]string{
		"leading BOM":                   "\uFEFF{\"a\": 1}",
		"unterminated block comment":    `{"a": 1 /* never closed}`,
		"unterminated string":           `{"a": "never closed}`,
		"unterminated after an escape":  `{"a": "x\"}`,
		"string ending in a backslash":  `{"a": "x\`,
		"block comment closer missing*": `{"a": 1 /* x *}`,
	}

	for name, input := range cases {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			if got, err := stripHuJSON(input); err == nil {
				t.Errorf("stripHuJSON(%q) = %q, want an error", input, got)
			}
		})
	}
}

func TestTailnetPolicy_DecodeRefusesDuplicateKeys(t *testing.T) {
	t.Parallel()

	refused := map[string]string{
		"duplicate top-level grants":  `{"grants": [], "tests": [], "grants": []}`,
		"duplicate src in a grant":    `{"grants": [{"src": ["a"], "dst": ["b"], "src": ["c"]}]}`,
		"duplicate nested key":        `{"a": {"b": {"c": 1, "c": 2}}}`,
		"duplicate in a tests entry":  `{"tests": [{"src": "a", "accept": [], "accept": []}]}`,
		"same duplicate, equal value": `{"a": 1, "a": 1}`,
		"data after the top value":    `{"a": 1} {"b": 2}`,
		"a second top value":          `{"a": 1} []`,
		"empty input":                 ``,
	}

	for name, input := range refused {
		t.Run("refuses "+name, func(t *testing.T) {
			t.Parallel()

			if got, err := decodeStrict(input); err == nil {
				t.Errorf("decodeStrict(%q) = %v, want an error", input, got)
			}
		})
	}

	t.Run("keys differing in case are two keys", func(t *testing.T) {
		t.Parallel()

		got, err := decodeStrict(`{"src": 1, "SRC": 2, "Src": 3}`)
		if err != nil {
			t.Fatalf("decodeStrict(keys differing in case) error = %v, want none", err)
		}

		want := map[string]any{"src": float64(1), "SRC": float64(2), "Src": float64(3)}
		if !reflect.DeepEqual(got, want) {
			t.Errorf("decodeStrict(keys differing in case) = %v, want %v", got, want)
		}
	})

	t.Run("a document without duplicates decodes", func(t *testing.T) {
		t.Parallel()

		got, err := decodeStrict(` {"a": [1, "x", null, true, {"b": []}], "c": {}} `)
		if err != nil {
			t.Fatalf("decodeStrict(valid document) error = %v, want none", err)
		}

		want := map[string]any{
			"a": []any{float64(1), "x", nil, true, map[string]any{"b": []any{}}},
			"c": map[string]any{},
		}
		if !reflect.DeepEqual(got, want) {
			t.Errorf("decodeStrict(valid document) = %v, want %v", got, want)
		}
	})
}

// loadPolicyText reads the example tailnet policy as it is on disk.
func loadPolicyText(t *testing.T) string {
	t.Helper()

	return readRepositoryText(t, tailnetPolicyFile)
}

// loadPolicy reads the example tailnet policy through the helpers above, never through json.Unmarshal.
func loadPolicy(t *testing.T) map[string]any {
	t.Helper()

	stripped, err := stripHuJSON(loadPolicyText(t))
	if err != nil {
		t.Fatalf("strip %s: %v", tailnetPolicyFile, err)
	}

	value, err := decodeStrict(stripped)
	if err != nil {
		t.Fatalf("decode %s: %v", tailnetPolicyFile, err)
	}

	policy, ok := value.(map[string]any)
	if ok == false {
		t.Fatalf("%s holds %T at the top, want an object", tailnetPolicyFile, value)
	}

	return policy
}

// policyList returns a policy member as a list.
func policyList(t *testing.T, policy map[string]any, key string) []any {
	t.Helper()

	list, ok := policy[key].([]any)
	if ok == false {
		t.Fatalf("policy member %q = %v (%T), want a list", key, policy[key], policy[key])
	}

	return list
}

// asObject returns a decoded value as an object.
func asObject(t *testing.T, value any, what string) map[string]any {
	t.Helper()

	object, ok := value.(map[string]any)
	if ok == false {
		t.Fatalf("%s = %v (%T), want an object", what, value, value)
	}

	return object
}

// asStrings returns a decoded value as a list of strings.
func asStrings(t *testing.T, value any, what string) []string {
	t.Helper()

	list, ok := value.([]any)
	if ok == false {
		t.Fatalf("%s = %v (%T), want a list", what, value, value)
	}

	result := make([]string, 0, len(list))
	for _, element := range list {
		text, isString := element.(string)
		if isString == false {
			t.Fatalf("%s holds %v (%T), want strings only", what, element, element)
		}

		result = append(result, text)
	}

	return result
}

// sortedKeys returns the keys of an object in order.
func sortedKeys(object map[string]any) []string {
	keys := make([]string, 0, len(object))
	for key := range object {
		keys = append(keys, key)
	}

	sort.Strings(keys)

	return keys
}

// grantsOf returns the grants of the policy as objects.
func grantsOf(t *testing.T, policy map[string]any) []map[string]any {
	t.Helper()

	var grants []map[string]any
	for i, element := range policyList(t, policy, "grants") {
		grants = append(grants, asObject(t, element, fmt.Sprintf("grants[%d]", i)))
	}

	return grants
}

// agentGrants returns the grants that name the agent's tag in their source.
func agentGrants(t *testing.T, policy map[string]any) []map[string]any {
	t.Helper()

	var found []map[string]any
	for _, grant := range grantsOf(t, policy) {
		if slices.Contains(asStrings(t, grant["src"], "grant src"), agentTag) {
			found = append(found, grant)
		}
	}

	return found
}

func TestTailnetPolicy_HasOnlyKnownSections(t *testing.T) {
	t.Parallel()

	got := sortedKeys(loadPolicy(t))
	want := []string{"grants", "tagOwners", "tests"}

	if !slices.Equal(got, want) {
		t.Errorf("top-level keys of %s = %q, want exactly %q", tailnetPolicyFile, got, want)
	}
}

func TestTailnetPolicy_TagOwnersAreExact(t *testing.T) {
	t.Parallel()

	got := loadPolicy(t)["tagOwners"]
	want := map[string]any{
		agentTag:   []any{"autogroup:admin"},
		backendTag: []any{"autogroup:admin"},
	}

	if !reflect.DeepEqual(got, want) {
		t.Errorf("tagOwners = %v, want exactly %v (no tag may own a tag)", got, want)
	}
}

func TestTailnetPolicy_GrantsHaveOnlyKnownKeys(t *testing.T) {
	t.Parallel()

	grants := grantsOf(t, loadPolicy(t))
	if len(grants) == 0 {
		t.Fatalf("%s has no grants, want the agent's grant at least", tailnetPolicyFile)
	}

	want := []string{"dst", "ip", "src"}
	for i, grant := range grants {
		if got := sortedKeys(grant); !slices.Equal(got, want) {
			t.Errorf("grants[%d] keys = %q, want exactly %q", i, got, want)
		}
	}
}

func TestTailnetPolicy_AgentHasOnlyTheIngestGrant(t *testing.T) {
	t.Parallel()

	found := agentGrants(t, loadPolicy(t))
	if len(found) != 1 {
		t.Fatalf("%d grants have %s in src, want exactly 1", len(found), agentTag)
	}

	grant := found[0]
	checks := map[string][]string{
		"src": {agentTag},
		"dst": {backendTag},
		"ip":  {"tcp:8081"},
	}

	for key, want := range checks {
		if got := asStrings(t, grant[key], "agent grant "+key); !slices.Equal(got, want) {
			t.Errorf("agent grant %s = %q, want exactly %q", key, got, want)
		}
	}
}

func TestTailnetPolicy_SourcesAreExactlyTheKnownOnes(t *testing.T) {
	t.Parallel()

	seen := map[string]bool{}
	for _, grant := range grantsOf(t, loadPolicy(t)) {
		for _, source := range asStrings(t, grant["src"], "grant src") {
			seen[source] = true
		}
	}

	got := make([]string, 0, len(seen))
	for source := range seen {
		got = append(got, source)
	}

	sort.Strings(got)

	want := []string{"autogroup:member", agentTag}
	sort.Strings(want)

	if !slices.Equal(got, want) {
		t.Errorf("set of grant sources = %q, want exactly %q (any other form may match the server)", got, want)
	}
}

// backendReachViolations lists the grants, other than the agent's, whose destinations are not exactly
// autogroup:self: any other destination (the backend tag, autogroup:tagged, autogroup:danger-all, an
// address, a wildcard) may cover the tagged backend host.
func backendReachViolations(t *testing.T, policy map[string]any) []string {
	t.Helper()

	var violations []string
	for i, grant := range grantsOf(t, policy) {
		if slices.Contains(asStrings(t, grant["src"], "grant src"), agentTag) {
			continue
		}

		got := asStrings(t, grant["dst"], "grant dst")
		if !slices.Equal(got, []string{"autogroup:self"}) {
			violations = append(violations, fmt.Sprintf("grants[%d] has dst %q, want exactly [\"autogroup:self\"]: no grant but the agent's may reach the backend host", i, got))
		}
	}

	return violations
}

func TestTailnetPolicy_GrantsNothingOnTheBackendButIngest(t *testing.T) {
	t.Parallel()

	for _, violation := range backendReachViolations(t, loadPolicy(t)) {
		t.Error(violation)
	}
}

func TestTailnetPolicy_BackendReachGuardRefusesWideDestinations(t *testing.T) {
	t.Parallel()

	destinations := []string{
		`"autogroup:tagged"`,
		`"autogroup:danger-all"`,
		`"tag:vandox-backend"`,
		`"*"`,
		`"autogroup:internet"`,
		`"100.64.0.0/10"`,
		`"autogroup:self", "autogroup:tagged", "autogroup:danger-all"`,
	}

	for _, destination := range destinations {
		t.Run(destination, func(t *testing.T) {
			t.Parallel()

			text := `{"grants": [
				{"src": ["tag:vandox-agent"], "dst": ["tag:vandox-backend"], "ip": ["tcp:8081"]},
				{"src": ["autogroup:member"], "dst": [` + destination + `], "ip": ["*"]}
			]}`

			stripped, err := stripHuJSON(text)
			if err != nil {
				t.Fatalf("strip inline policy: %v", err)
			}

			value, err := decodeStrict(stripped)
			if err != nil {
				t.Fatalf("decode inline policy: %v", err)
			}

			if got := backendReachViolations(t, asObject(t, value, "inline policy")); len(got) != 1 {
				t.Errorf("violations for dst [%s] = %q, want exactly 1", destination, got)
			}
		})
	}
}

func TestTailnetPolicy_OptInLinesAreCommentedSSHOnly(t *testing.T) {
	t.Parallel()

	text := loadPolicyText(t)
	grants := grantsOf(t, loadPolicy(t))

	if got, want := strings.Count(text, `"dst"`), len(grants)+1; got != want {
		t.Errorf(`"dst" appears %d times in %s, want %d: one per grant and one in the commented SSH opt-in`, got, tailnetPolicyFile, want)
	}

	var optIns []string
	for _, line := range strings.Split(text, "\n") {
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "//") && strings.Contains(trimmed, `"dst"`) {
			optIns = append(optIns, trimmed)
		}
	}

	if len(optIns) != 1 {
		t.Fatalf("%d comment lines contain %q, want exactly 1 (the SSH opt-in): %q", len(optIns), `"dst"`, optIns)
	}

	optIn := optIns[0]
	if !strings.Contains(optIn, "tcp:22") || !strings.Contains(optIn, backendTag) {
		t.Errorf("opt-in line %q, want it to open tcp:22 on %s", optIn, backendTag)
	}

	for _, forbidden := range []string{"8080", "443"} {
		if strings.Contains(optIn, forbidden) {
			t.Errorf("opt-in line %q names %s, want SSH only and no line for the web port", optIn, forbidden)
		}
	}
}

func TestTailnetPolicy_TestsPinTheIngestPortOnly(t *testing.T) {
	t.Parallel()

	entries := policyList(t, loadPolicy(t), "tests")
	if len(entries) != 2 {
		t.Fatalf("tests has %d entries, want exactly 2", len(entries))
	}

	var tcpEntries, udpEntries []map[string]any
	for i, element := range entries {
		entry := asObject(t, element, fmt.Sprintf("tests[%d]", i))
		if _, hasProto := entry["proto"]; hasProto {
			udpEntries = append(udpEntries, entry)
		} else {
			tcpEntries = append(tcpEntries, entry)
		}
	}

	if len(tcpEntries) != 1 || len(udpEntries) != 1 {
		t.Fatalf("tests has %d entries without and %d with proto, want 1 and 1", len(tcpEntries), len(udpEntries))
	}

	wantTCP := map[string]any{
		"src":    agentTag,
		"accept": []any{backendTag + ":8081"},
		"deny":   []any{backendTag + ":22", backendTag + ":8080", backendTag + ":443"},
	}
	if !reflect.DeepEqual(tcpEntries[0], wantTCP) {
		t.Errorf("TCP tests entry = %v, want exactly %v", tcpEntries[0], wantTCP)
	}

	wantUDP := map[string]any{
		"src":   agentTag,
		"proto": "udp",
		"deny":  []any{backendTag + ":8081"},
	}
	if !reflect.DeepEqual(udpEntries[0], wantUDP) {
		t.Errorf("UDP tests entry = %v, want exactly %v", udpEntries[0], wantUDP)
	}
}

// sectionOf returns the part of a document from heading up to the next level-2 heading.
func sectionOf(t *testing.T, document, heading string) string {
	t.Helper()

	start := strings.Index(document, heading)
	if start < 0 {
		t.Fatalf("README has no %q heading", heading)
	}

	rest := document[start+len(heading):]
	if end := strings.Index(rest, "\n## "); end >= 0 {
		rest = rest[:end]
	}

	return heading + rest
}

// tailscaleSection returns the README section on Tailscale with LF line ends.
func tailscaleSection(t *testing.T) string {
	t.Helper()

	return sectionOf(t, readRepositoryText(t, agentReadmeFile), tailscaleHeading)
}

// collapse lower-cases a text and turns every whitespace run into one space.
func collapse(text string) string {
	return strings.ToLower(strings.Join(strings.Fields(text), " "))
}

// containsAll fails the test for each needle the text lacks.
func containsAll(t *testing.T, text, what string, needles ...string) {
	t.Helper()

	for _, needle := range needles {
		if !strings.Contains(text, needle) {
			t.Errorf("%s lacks %q, want it", what, needle)
		}
	}
}

func TestAgentReadme_TailscaleStepsReferThePolicyFile(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	containsAll(t, section, "the Tailscale section", "tailnet-policy.hujson")

	if strings.Contains(section, `"tagOwners"`) {
		t.Errorf(`the Tailscale section holds "tagOwners", want a reference to %s and no diverging copy`, tailnetPolicyFile)
	}
}

func TestAgentReadme_TailscaleStepsHoldNoWideGrant(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	for _, forbidden := range []string{`"grants"`, `"ip": ["*"]`} {
		if strings.Contains(section, forbidden) {
			t.Errorf("the Tailscale section holds %s, want no grant copy and no wide grant", forbidden)
		}
	}
}

func TestAgentReadme_TailscaleSettingsLineAndCheck(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	containsAll(t, section, "the Tailscale section", settingsLine, "tailscale debug prefs",
		"RunSSH", "RouteAll", "AdvertiseRoutes", "ExitNodeID", "CorpDNS", "RunWebClient", "AppConnector", "AdvertiseTags")
}

func TestAgentReadme_TailscaleChecksNameEveryProbe(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	containsAll(t, section, "the Tailscale section", "tailscale exit-node list", "tailscale status",
		"kb/1018", "kb/1019", "kb/1103", "/8080")

	var ipv6Probes int
	for _, line := range strings.Split(section, "\n") {
		if strings.Contains(line, "fd7a:115c:a1e0::") && !strings.Contains(line, "/48") {
			ipv6Probes++
		}
	}

	if ipv6Probes < 2 {
		t.Errorf("the Tailscale section has %d probe lines on the backend's IPv6 tailnet address, want 2 (web port and SSH)", ipv6Probes)
	}

	probe := -1
	offset := 0
	for _, line := range strings.SplitAfter(section, "\n") {
		if strings.Contains(line, "setsid") && strings.Contains(line, "--accept-routes=true") && strings.Contains(line, "--accept-routes=false") {
			probe = offset
		}

		offset += len(line)
	}

	if probe < 0 {
		t.Fatalf("the Tailscale section has no setsid line holding both --accept-routes=true and --accept-routes=false, want the detached, reverted probe in one command string")
	}

	if last := strings.LastIndex(section, "tailscale debug prefs"); last < probe {
		t.Errorf("the last `tailscale debug prefs` is at %d, before the accept-routes probe at %d, want the settings check after it", last, probe)
	}
}

func TestAgentReadme_TailscaleRebootCheck(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	reboot := strings.Index(section, "sudo reboot")
	if reboot < 0 {
		t.Fatalf("the Tailscale section has no `sudo reboot`, want the reboot check")
	}

	containsAll(t, section[reboot:], "the Tailscale section after `sudo reboot`",
		"tailscale status", "tailscale debug prefs", "/v1/batches", "systemctl is-active vandox-agent")
}

func TestAgentReadme_TailscaleDownResidual(t *testing.T) {
	t.Parallel()

	text := collapse(tailscaleSection(t))

	containsAll(t, text, "the Tailscale section", "default route", "token", "in clear", "#337")

	if stale := "every send fails as `connection failed`"; strings.Contains(text, stale) {
		t.Errorf("the Tailscale section still says %q, want the residual: a send follows the default route while Tailscale is down", stale)
	}
}

func TestAgentReadme_TailscaleKeyExpiry(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	containsAll(t, section, "the Tailscale section", "tailscale up --force-reauth --advertise-tags=tag:vandox-agent", "backend.url")
}

var tailscaleStepHeading = regexp.MustCompile(`(?m)^\d+\. \*\*`)

// numberedSteps splits a section into its numbered steps ("1. **Title**").
func numberedSteps(section string) []string {
	starts := tailscaleStepHeading.FindAllStringIndex(section, -1)

	var steps []string
	for i, start := range starts {
		end := len(section)
		if i+1 < len(starts) {
			end = starts[i+1][0]
		}

		steps = append(steps, section[start[0]:end])
	}

	return steps
}

func TestAgentReadme_TailscaleStepsAreMarkedNotYetRun(t *testing.T) {
	t.Parallel()

	steps := numberedSteps(tailscaleSection(t))

	markers := map[string]string{
		"S2 checks":   "tailscale exit-node list",
		"S4 settings": "tailscale debug prefs",
		"S5 reboot":   "sudo reboot",
		"S6 key":      "--force-reauth",
	}

	for name, marker := range markers {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			assertStepsNotYetRun(t, steps, marker)
		})
	}
}

// assertStepsNotYetRun requires at least one step to hold the marker and every such step to say "not yet run".
func assertStepsNotYetRun(t *testing.T, steps []string, marker string) {
	t.Helper()

	var holding []string
	for _, step := range steps {
		if strings.Contains(step, marker) {
			holding = append(holding, collapse(step))
		}
	}

	if len(holding) == 0 {
		t.Errorf("no numbered step holds %q, want one", marker)
	}

	for _, step := range holding {
		if !strings.Contains(step, "not yet run") {
			t.Errorf("a step holding %q lacks \"not yet run\", want its results marked as not run on the server", marker)
		}
	}
}

func TestAgentReadme_ChecksOnTheServerHaveRowsPerTailscaleCheck(t *testing.T) {
	t.Parallel()

	readme := readRepositoryText(t, agentReadmeFile)

	if strings.Contains(readme, "#<follow-up") {
		t.Errorf("the README still holds a #<follow-up placeholder, want the issue numbers #338 and #337")
	}

	var rows int
	for _, line := range strings.Split(sectionOf(t, readme, checksHeading), "\n") {
		if strings.HasPrefix(line, "|") && strings.Contains(collapse(line), "not yet run (issue #338)") {
			rows++
		}
	}

	if rows < 4 {
		t.Errorf("%q has %d rows marked \"not yet run (issue #338)\", want 4 (checks, settings, reboot, key expiry)", checksHeading, rows)
	}
}
