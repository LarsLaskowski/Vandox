package agent

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// deferralPhrases are the ways a document defers a check to the release or to #45 (collapsed, lower case).
var deferralPhrases = []string{
	"until v0.2.0 is released",
	"before the release (#45)",
	"no release has the ingest api",
	"has **not yet run** there",
	"upgraded to v0.2.0",
}

// recordedRows are the rows of Checks on the server whose recorded v0.0.0-rc.0 result stays (collapsed prefixes).
var recordedRows = []string{"| transmission to the backend |", "| tailscale checks |"}

// withoutRecordedRows returns the README without the two recorded rows.
func withoutRecordedRows(readme string) string {
	var kept []string
	for _, line := range strings.Split(readme, "\n") {
		row := collapse(line)
		if strings.HasPrefix(row, recordedRows[0]) || strings.HasPrefix(row, recordedRows[1]) {
			continue
		}

		kept = append(kept, line)
	}

	return strings.Join(kept, "\n")
}

// installSteps10And11 returns steps 10 and 11 of INSTALL.md, up to the next level-2 heading.
func installSteps10And11(t *testing.T) string {
	t.Helper()

	install := readRepositoryText(t, installFile)

	start := strings.Index(install, "## 10. ")
	step11 := strings.Index(install, "## 11. ")
	if start < 0 || step11 < start {
		t.Fatalf("%s lacks steps 10 and 11 in order", installFile)
	}

	rest := install[step11+len("## 11. "):]
	if end := strings.Index(rest, "\n## "); end >= 0 {
		rest = rest[:end]
	}

	return install[start:step11+len("## 11. ")] + rest
}

// installWithoutSettledParagraphs drops the paragraphs of INSTALL.md steps 10 and 11 that keep the recorded
// "no release has the ingest API" statement and name the v0.2.0-rc.1 run as having settled it.
func installWithoutSettledParagraphs(steps string) string {
	var kept []string
	for _, paragraph := range strings.Split(steps, "\n\n") {
		text := collapse(paragraph)
		if strings.Contains(text, "no release has the ingest api") && strings.Contains(text, "v0.2.0-rc.1") {
			continue
		}

		kept = append(kept, paragraph)
	}

	return strings.Join(kept, "\n\n")
}

func TestReleaseDocs_DeferNoCheckToV020_AC4(t *testing.T) {
	t.Parallel()

	areas, err := filepath.Glob(filepath.Join("..", "..", "docs", "areas", "*.md"))
	if err != nil || len(areas) == 0 {
		t.Fatalf("no area documents found (%v)", err)
	}

	sort.Strings(areas)

	documents := map[string]string{
		"INSTALL.md steps 10 and 11": installWithoutSettledParagraphs(installSteps10And11(t)),
		agentReadmeFile:              withoutRecordedRows(readRepositoryText(t, agentReadmeFile)),
		"README.md":                  readRepositoryText(t, "README.md"),
	}

	for _, area := range areas {
		data, readErr := os.ReadFile(area)
		if readErr != nil {
			t.Fatalf("read %s: %v", area, readErr)
		}

		documents["docs/areas/"+filepath.Base(area)] = normalizeLineEnds(string(data))
	}

	for name, text := range documents {
		collapsed := collapse(text)

		for _, phrase := range deferralPhrases {
			if strings.Contains(collapsed, phrase) {
				t.Errorf("AC4 %s holds %q, want no check deferred to the release or #45 but the parallel run", name, phrase)
			}
		}
	}
}
