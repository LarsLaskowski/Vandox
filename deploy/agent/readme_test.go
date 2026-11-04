package agent

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// errorLogSectionHeading is the README subsection that documents the grant for the error logs.
const errorLogSectionHeading = "### Error logs of Plesk and the web servers"

// unverifiedMarker marks a statement about the Plesk server that nobody has checked on one.
const unverifiedMarker = "(not verified on a Plesk server)"

// nextHeadingPattern finds the next heading of level 1 to 3.
var nextHeadingPattern = regexp.MustCompile(`(?m)^#{1,3} `)

// errorLogSectionOf returns the text from the heading to the next heading of the same or a higher level.
func errorLogSectionOf(text, heading string) (string, bool) {
	_, rest, found := strings.Cut(text, heading+"\n")
	if found == false {
		return "", false
	}

	if next := nextHeadingPattern.FindStringIndex(rest); next != nil {
		rest = rest[:next[0]]
	}

	return rest, true
}

// requireSubstrings reports every want the text lacks.
func requireSubstrings(t *testing.T, what, text string, wants []string) {
	t.Helper()

	for _, want := range wants {
		if strings.Contains(text, want) == false {
			t.Errorf("%s: section lacks %q, want it", what, want)
		}
	}
}

// requireMarkedTopic reports a paragraph of the topic without the unverified marker, or a topic no paragraph states.
func requireMarkedTopic(t *testing.T, name string, topic *regexp.Regexp, paragraphs []string) {
	t.Helper()

	matched := 0

	for _, paragraph := range paragraphs {
		p := strings.Join(strings.Fields(paragraph), " ")
		if topic.MatchString(p) == false {
			continue
		}

		matched++

		if strings.Contains(p, unverifiedMarker) == false {
			t.Errorf("%s: paragraph %q lacks %q, want the marker on each statement of fact 14", name, p, unverifiedMarker)
		}
	}

	if matched == 0 {
		t.Errorf("%s: no paragraph states it, want it with %q", name, unverifiedMarker)
	}
}

// requireSetfaclWithP reports every setfacl line without -P.
func requireSetfaclWithP(t *testing.T, section string) {
	t.Helper()

	for _, line := range strings.Split(section, "\n") {
		if strings.Contains(line, "setfacl") && strings.Contains(line, "setfacl -P") == false {
			t.Errorf("line %q uses setfacl without -P, want -P on every setfacl", line)
		}
	}
}

func TestReadme_ErrorLogGrantIsDocumentedAndMarkedUnverified(t *testing.T) {
	section, found := errorLogSectionOf(readRepositoryText(t, "deploy/agent/README.md"), errorLogSectionHeading)
	if found == false {
		t.Fatalf("README has no section %q, want it", errorLogSectionHeading)
	}

	flat := strings.Join(strings.Fields(section), " ")

	requireSubstrings(t, "the four log paths", flat, []string{
		"/var/log/nginx/error.log", "/var/log/apache2/error.log", "/var/log/plesk/panel.log", "/var/log/sw-cp-server/error_log",
	})
	requireSubstrings(t, "the commands of the procedure", flat, []string{
		"namei -l <dir>/<file>",
		"setfacl -P -m u:vandox-agent:x <element>",
		"setfacl -P -m u:vandox-agent:r <dir>/<file>",
		"setfacl -P -d -m u::rw,u:vandox-agent:r,g::r,o::- <dir>",
		"sudo -u vandox-agent head -c 0 <dir>/<file>",
	})
	requireSubstrings(t, "the signs and the data sent", flat, []string{
		"log file not readable", "link refused", "client IP addresses", "query strings", "shipped to the backend",
	})
	requireSubstrings(t, "the default ACL's reach", strings.ToLower(flat), []string{
		"every", "future", "subdirector", "umask", "u::rw", "create <mode>",
	})
	requireSetfaclWithP(t, section)

	if strings.Index(flat, "namei -l") > strings.Index(flat, "setfacl") {
		t.Errorf("section names setfacl before namei -l, want namei -l first")
	}

	paragraphs := strings.Split(section, "\n\n")
	requireMarkedTopic(t, "the owner and mode of the files",
		regexp.MustCompile(`(?i)\bowner|\bmode\b.*(panel\.log|error_log)|(panel\.log|error_log).*\bmode\b`), paragraphs)
	requireMarkedTopic(t, "the rotation by Plesk", regexp.MustCompile(`(?i)plesk rotat|rotated by plesk|rotates`), paragraphs)
	requireMarkedTopic(t, "plesk repair fs", regexp.MustCompile(`(?i)plesk repair fs`), paragraphs)
}

// networkAreaFile is the area document of the network and security model, relative to the repository root.
const networkAreaFile = "docs/areas/network-and-security-model.md"

// egressOutputFile is where the documented egress check writes its result on the server.
const egressOutputFile = "/root/vandox-egress.txt"

// egressMarker identifies the documented egress check among the README's fenced blocks.
const egressMarker = "systemctl stop tailscaled"

// startTailscaled is the call no failure of the egress check may skip.
const startTailscaled = "systemctl start tailscaled"

// missingTailscale0 is what `ip link show tailscale0` prints on standard error with exit status 1 when the device
// does not exist (iproute2 5.15 of Ubuntu 22.04).
const missingTailscale0 = `Device "tailscale0" does not exist.`

// staleResidualTexts are sentences of the residual that the binding replaces.
var staleResidualTexts = []string{
	"is not closed here",
	"is not closed; issue #337 adds a control",
	"a send to the backend's tailnet address follows the server's default route",
}

// egressCommands are the only commands the egress check may call, each by its bare name.
var egressCommands = []string{"setsid", "ip", "systemctl", "sleep"}

// requireNoStaleResidual reports every sentence of the replaced residual that text still holds.
func requireNoStaleResidual(t *testing.T, what, text string) {
	t.Helper()

	for _, stale := range staleResidualTexts {
		if strings.Contains(text, stale) {
			t.Errorf("%s still holds %q, want the binding to tailscale0 instead of the residual", what, stale)
		}
	}
}

// requireNotYetRunRow reports a missing table row of the checks section that names needle and says "not yet run".
func requireNotYetRunRow(t *testing.T, checks, needle string) {
	t.Helper()

	for _, line := range strings.Split(checks, "\n") {
		row := collapse(line)
		if strings.HasPrefix(row, "|") && strings.Contains(row, needle) && strings.Contains(row, "not yet run (issue #") {
			return
		}
	}

	t.Errorf("%q has no row naming %q marked \"not yet run (issue #<n>)\", want one", checksHeading, needle)
}

func TestReadme_DescribesTheTailnetDeviceBinding(t *testing.T) {
	t.Parallel()

	readme := readRepositoryText(t, agentReadmeFile)
	section := collapse(sectionOf(t, readme, tailscaleHeading))
	containsAll(t, section, "the Tailscale section", "tailscale0", "host name", "loopback")
	requireNoStaleResidual(t, "the Tailscale section", section)

	area := collapse(readRepositoryText(t, networkAreaFile))
	containsAll(t, area, "the network area", "tailscale0", "host name", "loopback")
	requireNoStaleResidual(t, "the network area", area)

	checks := sectionOf(t, readme, checksHeading)
	requireNotYetRunRow(t, checks, "ip route get 100.64.0.1")
	requireNotYetRunRow(t, checks, "ipegresspackets")
}

// fencedBlocks returns the non-empty lines of every fenced block of text, each line without its indentation.
func fencedBlocks(text string) [][]string {
	var blocks [][]string
	var current []string
	inside := false

	for _, line := range strings.Split(text, "\n") {
		trimmed := strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(trimmed, "```"):
			if inside {
				blocks = append(blocks, current)
			}
			inside, current = inside == false, nil
		case inside && trimmed != "":
			current = append(current, trimmed)
		}
	}

	return blocks
}

// egressLine returns the README's documented egress check: the one fenced block that holds egressMarker, which must
// be a single line, so pasting it at once and line by line is the same.
func egressLine(t *testing.T) string {
	t.Helper()

	var found [][]string
	for _, block := range fencedBlocks(readRepositoryText(t, agentReadmeFile)) {
		if strings.Contains(strings.Join(block, "\n"), egressMarker) {
			found = append(found, block)
		}
	}

	if len(found) != 1 {
		t.Fatalf("the README has %d fenced blocks holding %q, want exactly one: the egress check", len(found), egressMarker)
	}

	if len(found[0]) != 1 || strings.HasSuffix(found[0][0], "\\") {
		t.Fatalf("the egress check's block is %q, want one line without a continuation, so pasting it at once and line by line is the same", found[0])
	}

	return found[0][0]
}

// requireBareCommands reports a command of egressCommands the line lacks or calls by a path.
func requireBareCommands(t *testing.T, line string) {
	t.Helper()

	for _, name := range egressCommands {
		bare := regexp.MustCompile(`(^|[\s;&|("'])` + name + `\s`)
		if bare.MatchString(line) == false {
			t.Errorf("the egress check does not call %s by its bare name, want it called as %q", name, name)
		}

		if regexp.MustCompile(`/` + name + `\b`).MatchString(line) {
			t.Errorf("the egress check calls %s by a path, want only the bare name %q", name, name)
		}
	}

	if strings.Contains(line, egressOutputFile) == false {
		t.Fatalf("the egress check does not write %s, want its result there", egressOutputFile)
	}
}

// egressCase is one behavior of the stubbed commands under the egress check.
type egressCase struct {
	name string
	// setPropertyStatus and stopStatus are the exit statuses of `systemctl set-property` and `systemctl stop`.
	setPropertyStatus, stopStatus string
	// first and second are what the first and every later `systemctl show` prints; "" prints nothing.
	first, second string
	// lost is true when a count is not a number, so the output must name the failure.
	lost bool
}

// egressRun is what one run of the egress check left: the stub calls in order, the output file and standard error.
type egressRun struct {
	calls  []string
	output string
	stderr string
}

// systemctlStub models systemctl for the egress check: set-property and stop exit with the case's status (1 with
// systemd's message on failure), show prints the case's counts as `systemctl show -P IPEgressPackets` prints a value.
const systemctlStub = `#!/bin/bash
printf '%s\n' "systemctl $*" >>LOG
case " $* " in
*" set-property "*)
  if [[ SETPROP != 0 ]]; then printf '%s\n' 'Failed to set unit properties on vandox-agent.service: Unit vandox-agent.service not found.' >&2; fi
  exit SETPROP ;;
*" stop "*)
  if [[ STOP != 0 ]]; then printf '%s\n' 'Failed to stop tailscaled.service: Access denied' >&2; fi
  exit STOP ;;
*" show "*)
  n=0
  if [[ -f COUNTER ]]; then n=$(<COUNTER); fi
  n=$((n + 1))
  printf '%s' "$n" >COUNTER
  if [[ $n == 1 ]]; then v=FIRST; else v=SECOND; fi
  if [[ -n $v ]]; then printf '%s\n' "$v"; fi ;;
esac
exit 0
`

// ipStub models `ip link show tailscale0` without the device; every other call succeeds silently.
const ipStub = `#!/bin/bash
printf '%s\n' "ip $*" >>LOG
if [[ "$*" == "link show tailscale0" ]]; then printf '%s\n' 'MISSING' >&2; exit 1; fi
exit 0
`

// sleepStub returns at once; setsidStub runs its command in the foreground, so the run ends with the check.
const (
	sleepStub = `#!/bin/bash
printf '%s\n' "sleep $*" >>LOG
exit 0
`
	setsidStub = `#!/bin/bash
printf '%s\n' "setsid" >>LOG
while [[ $1 == -* ]]; do shift; done
if [[ $1 == bash ]]; then shift; exec /bin/bash "$@"; fi
exec "$@"
`
)

// writeEgressStubs writes the four stubs of the case into dir/stubs and returns the stub directory and the call log.
func writeEgressStubs(t *testing.T, dir string, tc egressCase) (string, string) {
	t.Helper()

	stubs := filepath.Join(dir, "stubs")
	if err := os.Mkdir(stubs, 0o755); err != nil {
		t.Fatalf("mkdir %s: %v", stubs, err)
	}

	log := filepath.Join(dir, "calls.log")
	replacer := strings.NewReplacer(
		"LOG", shellQuote(log), "COUNTER", shellQuote(filepath.Join(dir, "show.count")),
		"SETPROP", tc.setPropertyStatus, "STOP", tc.stopStatus,
		"FIRST", shellQuote(tc.first), "SECOND", shellQuote(tc.second), "MISSING", missingTailscale0,
	)

	for name, text := range map[string]string{"systemctl": systemctlStub, "ip": ipStub, "sleep": sleepStub, "setsid": setsidStub} {
		if err := os.WriteFile(filepath.Join(stubs, name), []byte(replacer.Replace(text)), 0o755); err != nil {
			t.Fatalf("write stub %s: %v", name, err)
		}
	}

	return stubs, log
}

// runEgressLine runs line with /bin/bash, PATH holding the case's stubs alone and the output file in a scratch
// directory.
func runEgressLine(t *testing.T, line string, tc egressCase) egressRun {
	t.Helper()

	dir := t.TempDir()
	stubs, log := writeEgressStubs(t, dir, tc)
	output := filepath.Join(dir, "vandox-egress.txt")

	ctx, cancel := context.WithTimeout(context.Background(), testDeadline)
	defer cancel()

	cmd := exec.CommandContext(ctx, "/bin/bash", "-c", strings.ReplaceAll(line, egressOutputFile, output))
	cmd.Dir = dir
	cmd.Env = []string{"PATH=" + stubs, "LC_ALL=C", "HOME=" + dir}

	var stderr bytes.Buffer
	cmd.Stderr = &stderr

	err := cmd.Run()
	if ctx.Err() != nil {
		t.Fatalf("the egress check did not finish within %s", testDeadline)
	}

	var exitErr *exec.ExitError
	if err != nil && errors.As(err, &exitErr) == false {
		t.Fatalf("run the egress check: %v", err)
	}

	calls, _ := os.ReadFile(log)
	written, _ := os.ReadFile(output)

	return egressRun{calls: strings.Split(strings.TrimSpace(string(calls)), "\n"), output: string(written), stderr: stderr.String()}
}

// lastIndexOf returns the index of the last call that starts with prefix, or -1.
func lastIndexOf(calls []string, prefix string) int {
	for i := len(calls) - 1; i >= 0; i-- {
		if strings.HasPrefix(calls[i], prefix) {
			return i
		}
	}

	return -1
}

// requireTailscaledStartedLast reports a run whose calls do not start tailscaled after its last stop, or that call
// anything after the start but switching the IP accounting off again.
func requireTailscaledStartedLast(t *testing.T, run egressRun) {
	t.Helper()

	start := lastIndexOf(run.calls, startTailscaled)
	if start < 0 {
		t.Fatalf("calls %q lack %q, want tailscaled started again whatever failed", run.calls, startTailscaled)
	}

	if stop := lastIndexOf(run.calls, egressMarker); stop < 0 || stop > start {
		t.Errorf("calls %q: last %q at %d, %q at %d, want the stop first and the start after it", run.calls, egressMarker, stop, startTailscaled, start)
	}

	for _, call := range run.calls[start+1:] {
		if strings.HasPrefix(call, "systemctl set-property") == false || strings.Contains(call, "IPAccounting=no") == false {
			t.Errorf("calls %q: %q follows %q, want the start last but for switching IPAccounting off", run.calls, call, startTailscaled)
		}
	}

	for _, where := range []string{run.output, run.stderr} {
		if strings.Contains(where, "command not found") {
			t.Errorf("the egress check called a command outside %v: %q", egressCommands, where)
		}
	}
}

// requireMeasuredInOrder reports a successful run whose calls do not switch accounting on, stop tailscaled, read the
// count, wait 60 s and read it again in that order, or whose output lacks both counts and the link line.
func requireMeasuredInOrder(t *testing.T, run egressRun, tc egressCase) {
	t.Helper()

	order := []string{
		"systemctl set-property --runtime vandox-agent IPAccounting=yes", egressMarker, "systemctl show", "sleep 60", "systemctl show",
	}
	next := 0
	for _, call := range run.calls {
		if next < len(order) && strings.HasPrefix(call, order[next]) {
			next++
		}
	}

	if next != len(order) {
		t.Errorf("calls %q, want in this order %q", run.calls, order)
	}

	if lastIndexOf(run.calls, "ip link show tailscale0") < 0 {
		t.Errorf("calls %q lack \"ip link show tailscale0\", want the link line", run.calls)
	}

	for _, want := range []string{tc.first, tc.second, missingTailscale0} {
		if strings.Contains(run.output, want) == false {
			t.Errorf("output %q lacks %q, want both counts and the link line", run.output, want)
		}
	}
}

func TestReadme_EgressCheckStartsTailscaleOnEveryFailure(t *testing.T) {
	t.Parallel()

	line := egressLine(t)
	requireBareCommands(t, line)

	cases := []egressCase{
		{name: "every step succeeds", setPropertyStatus: "0", stopStatus: "0", first: "4711", second: "4713"},
		{name: "set-property fails", setPropertyStatus: "1", stopStatus: "0", first: "4711", second: "4711"},
		{name: "stop fails", setPropertyStatus: "0", stopStatus: "1", first: "4711", second: "4711"},
		{name: "show prints nothing", setPropertyStatus: "0", stopStatus: "0", lost: true},
		{name: "show prints not set", setPropertyStatus: "0", stopStatus: "0", first: "[not set]", second: "[not set]", lost: true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			run := runEgressLine(t, line, tc)
			requireTailscaledStartedLast(t, run)

			switch {
			case tc.lost:
				if strings.Contains(strings.ToLower(run.output), "fail") == false {
					t.Errorf("output %q does not name the failure, want a line with \"fail\" when a count is not a number", run.output)
				}
			case tc.setPropertyStatus == "0" && tc.stopStatus == "0":
				requireMeasuredInOrder(t, run, tc)
			}
		})
	}
}
