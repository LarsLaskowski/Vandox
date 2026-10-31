package agent

import (
	"regexp"
	"strings"
	"testing"
)

// errorLogSectionHeading is the README subsection that documents the grant for the error logs.
const errorLogSectionHeading = "### Error logs of Plesk and the web servers"

// unverifiedMarker marks a statement about the Plesk server that nobody has checked on one.
const unverifiedMarker = "(not verified on a Plesk server)"

// errorLogSectionOf returns the text from the heading to the next heading of the same or a higher level.
func errorLogSectionOf(text, heading string) (string, bool) {
	_, rest, found := strings.Cut(text, heading+"\n")
	if found == false {
		return "", false
	}

	if next := regexp.MustCompile(`(?m)^#{1,3} `).FindStringIndex(rest); next != nil {
		rest = rest[:next[0]]
	}

	return rest, true
}

func TestReadme_ErrorLogGrantIsDocumentedAndMarkedUnverified(t *testing.T) {
	section, found := errorLogSectionOf(readRepositoryText(t, "deploy/agent/README.md"), errorLogSectionHeading)
	if found == false {
		t.Fatalf("README has no section %q, want it", errorLogSectionHeading)
	}

	flat := strings.Join(strings.Fields(section), " ")

	requireAll := func(what string, wants ...string) {
		t.Helper()

		for _, want := range wants {
			if strings.Contains(flat, want) == false {
				t.Errorf("%s: section lacks %q, want it", what, want)
			}
		}
	}

	requireAll("the four log paths",
		"/var/log/nginx/error.log", "/var/log/apache2/error.log", "/var/log/plesk/panel.log", "/var/log/sw-cp-server/error_log")
	requireAll("the commands of the procedure",
		"namei -l <dir>/<file>",
		"setfacl -P -m u:vandox-agent:x <element>",
		"setfacl -P -m u:vandox-agent:r <dir>/<file>",
		"setfacl -P -d -m u::rw,u:vandox-agent:r,g::r,o::- <dir>",
		"sudo -u vandox-agent head -c 0 <dir>/<file>")
	requireAll("the signs and the data sent", "log file not readable", "link refused", "client IP addresses", "query strings", "shipped to the backend")

	for _, line := range strings.Split(section, "\n") {
		if strings.Contains(line, "setfacl") && strings.Contains(line, "setfacl -P") == false {
			t.Errorf("line %q uses setfacl without -P, want -P on every setfacl", line)
		}
	}

	if strings.Index(flat, "namei -l") > strings.Index(flat, "setfacl") {
		t.Errorf("section names setfacl before namei -l, want namei -l first")
	}

	lower := strings.ToLower(flat)
	requireAll("the default ACL's reach", "every")
	for _, want := range []string{"future", "subdirector", "umask", "u::rw", "create <mode>"} {
		if strings.Contains(lower, want) == false {
			t.Errorf("default ACL's reach: section lacks %q, want it", want)
		}
	}

	paragraphs := strings.Split(section, "\n\n")
	for name, topic := range map[string]*regexp.Regexp{
		"the owner and mode of the files": regexp.MustCompile(`(?i)\bowner|\bmode\b.*(panel\.log|error_log)|(panel\.log|error_log).*\bmode\b`),
		"the rotation by Plesk":           regexp.MustCompile(`(?i)plesk rotat|rotated by plesk|rotates`),
		"plesk repair fs":                 regexp.MustCompile(`(?i)plesk repair fs`),
	} {
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
}
