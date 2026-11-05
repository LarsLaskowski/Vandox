package agent

import (
	"regexp"
	"strings"
	"testing"
)

const (
	// agentReadmeFile is the agent's README, relative to the repository root.
	agentReadmeFile = "deploy/agent/README.md"

	// tailscaleHeading opens the README section the procedure lives in.
	tailscaleHeading = "## Tailscale on the monitored server"

	// checksHeading opens the README section with the check results.
	checksHeading = "## Checks on the server"

	// agentTag and backendTag are the two tags of the tailnet policy.
	agentTag   = "tag:vandox-agent"
	backendTag = "tag:vandox-backend"

	// settingsLine is the one `tailscale set` line of the join step.
	settingsLine = "tailscale set --ssh=false --accept-routes=false --advertise-routes= --advertise-exit-node=false --exit-node= --advertise-connector=false --webclient=false --accept-dns=false"
)

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

// policyStep returns step 2 of the Tailscale section, the one that describes the tailnet policy.
func policyStep(t *testing.T) string {
	t.Helper()

	steps := numberedSteps(tailscaleSection(t))
	if len(steps) < 2 {
		t.Fatalf("the Tailscale section has %d numbered steps, want at least 2", len(steps))
	}

	return steps[1]
}

// policySettings is the list of console settings in step 2, word for word: any rule added to it, or any widened one,
// changes the text and fails the test.
const policySettings = `- **Tags:** ` + "`tag:vandox-agent` and `tag:vandox-backend`, both owned by `autogroup:admin`. No tag may own a tag." + `
- **The agent's only rule:** source ` + "`tag:vandox-agent`, destination `tag:vandox-backend`, protocol TCP, port `8081` (the backend's" + `
ingest port; use yours if it differs). Choose TCP explicitly: a port without a protocol allows every protocol on that port, and
all TCP ports allow every port of the backend host.
- **Your own devices:** a rule from ` + "`autogroup:member` to `autogroup:self` on all ports, which keeps them reaching each other. A" + `
tagged host is no longer part of its user's ` + "`autogroup:self`." + `
- **Nothing else.** The tailnet's default rule is gone, and there is no rule for the backend host's web port. SSH from the tailnet
admins to the backend host is an opt-in: a rule from ` + "`autogroup:admin` to `tag:vandox-backend` on TCP port `22` and nothing else."

// forbiddenRules are the items of step 2's list of rules that must never be added; removing one fails the test.
var forbiddenRules = []string{
	"not `*`, not `autogroup:tagged`, not `tag:vandox-agent` with another destination",
	"not an address, a cidr, a host alias or an ipset that covers it",
	"no `autoapprovers` entry, no `ssh` rule and no grant to a subnet or to `autogroup:internet` with such a source",
	"no tag as a tag owner",
	"`autogroup:tagged` or `autogroup:danger-all` as destination",
	"no rule but the agent's one may have `tag:vandox-agent` as source",
}

// betweenMarkers returns the part of text after start and before end, or fails the test when one is missing.
func betweenMarkers(t *testing.T, text, start, end string) string {
	t.Helper()

	from := strings.Index(text, start)
	if from < 0 {
		t.Fatalf("step 2 lacks %q, want it", start)
	}

	rest := text[from+len(start):]
	to := strings.Index(rest, end)
	if to < 0 {
		t.Fatalf("step 2 lacks %q after %q, want it", end, start)
	}

	return rest[:to]
}

func TestAgentReadme_TailscalePolicyStepNamesTheConsoleSettings(t *testing.T) {
	t.Parallel()

	got := collapse(betweenMarkers(t, policyStep(t), "The policy holds exactly this:", "On the production tailnet"))
	if want := collapse(policySettings); got != want {
		t.Errorf("the settings list of step 2 = %q, want exactly %q", got, want)
	}
}

func TestAgentReadme_TailscalePolicyStepKeepsTheForbiddenRules(t *testing.T) {
	t.Parallel()

	step := collapse(betweenMarkers(t, tailscaleSection(t), "Rules that must never be added:", "\n3. **"))

	containsAll(t, step, "the forbidden rules of step 2", forbiddenRules...)
}

func TestAgentReadme_TailscalePolicyStepHoldsNoPolicyFile(t *testing.T) {
	t.Parallel()

	section := tailscaleSection(t)

	for _, forbidden := range []string{"tailnet-policy.hujson", `"grants"`, `"tagOwners"`, `"ip": ["*"]`, "tcp:*"} {
		if strings.Contains(section, forbidden) {
			t.Errorf("the Tailscale section holds %s, want the console settings in words and no policy file or wide grant", forbidden)
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

func TestAgentReadme_TailscaleHasNoRebootStep(t *testing.T) {
	t.Parallel()

	if section := tailscaleSection(t); strings.Contains(section, "sudo reboot") {
		t.Errorf("the Tailscale section holds `sudo reboot`, want no reboot check for Tailscale")
	}
}

// TestAgentReadme_TailscaleDownFailureModes holds the two failure modes of a send while Tailscale is not up (plan of
// issue #337, fact 13): `connection failed` without tailscale0, a timeout while tailscaled runs without routes, and
// the records kept in the spool meanwhile.
func TestAgentReadme_TailscaleDownFailureModes(t *testing.T) {
	t.Parallel()

	text := collapse(tailscaleSection(t))

	containsAll(t, text, "the Tailscale section", "connection failed", "timeout", "spool")

	if stale := "every send fails as `connection failed`"; strings.Contains(text, stale) {
		t.Errorf("the Tailscale section still says %q, want both failure modes: connection failed without tailscale0, a timeout while tailscaled has no routes", stale)
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

func TestAgentReadme_TailscaleIngestProbeWaitsForTheBackendUpgrade(t *testing.T) {
	t.Parallel()

	var holding []string
	for _, step := range numberedSteps(tailscaleSection(t)) {
		if strings.Contains(step, "tailscale exit-node list") {
			holding = append(holding, collapse(step))
		}
	}

	if len(holding) != 1 {
		t.Fatalf("%d numbered steps hold `tailscale exit-node list`, want 1", len(holding))
	}

	containsAll(t, holding[0], "the step with the probes", "not yet run", "upgraded to v0.2.0")
}

func TestAgentReadme_ChecksOnTheServerHoldNoOpenTailscaleIssue(t *testing.T) {
	t.Parallel()

	readme := readRepositoryText(t, agentReadmeFile)

	if strings.Contains(readme, "#<follow-up") {
		t.Errorf("the README still holds a #<follow-up placeholder, want the issue numbers")
	}

	var probes int
	for _, line := range strings.Split(sectionOf(t, readme, checksHeading), "\n") {
		row := collapse(line)
		if !strings.HasPrefix(row, "| tailscale") {
			continue
		}

		if strings.Contains(row, "#375") || strings.Contains(row, "after a reboot") {
			t.Errorf("%q holds the Tailscale row %q, want neither the policy file's open check nor a reboot row", checksHeading, line)
		}

		if strings.HasPrefix(row, "| tailscale checks |") {
			probes++
			containsAll(t, row, "the row of the Tailscale checks", "upgraded to v0.2.0")
		}
	}

	if probes != 1 {
		t.Errorf("%q has %d rows of the Tailscale checks, want 1", checksHeading, probes)
	}
}
