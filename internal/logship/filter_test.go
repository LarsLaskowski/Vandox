package logship_test

import (
	"regexp"
	"slices"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/logship"
)

// filterEntries are the journal entries the filter cases are applied to, with the sequence numbers 1 to 9.
func filterEntries() []map[string][]byte {
	return []map[string][]byte{
		jentry(1, "disk full", "PRIORITY", "3"),
		jentry(2, "slow request", "PRIORITY", "4"),
		jentry(3, "request ok", "PRIORITY", "6"),
		jentry(4, "imap-login: Login: user=<a>", "SYSLOG_IDENTIFIER", "dovecot"),
		jentry(5, "imap-login: Disconnected", "SYSLOG_IDENTIFIER", "dovecot"),
		jentry(6, "delivered", "SYSLOG_IDENTIFIER", "dovecot-lda"),
		jentry(7, "audit line", "PRIORITY", ""),
		jentry(8, "debug detail", "PRIORITY", "7", "SYSLOG_IDENTIFIER", "cron"),
		jentry(9, "Request LOGIN noted", "PRIORITY", "5"),
	}
}

func TestFilter_MinPriorityAndDropRules(t *testing.T) {
	everything := []string{"disk full", "slow request", "request ok", "imap-login: Login: user=<a>", "imap-login: Disconnected", "delivered", "audit line", "debug detail", "Request LOGIN noted"}
	without := func(drop ...string) []string {
		var out []string
		for _, m := range everything {
			if !slices.Contains(drop, m) {
				out = append(out, m)
			}
		}
		return out
	}
	tests := []struct {
		name  string
		min   uint8
		rules []logship.Rule
		want  []string
	}{
		{"info drops debug only", 6, nil, without("debug detail")},
		{"debug drops nothing", 7, nil, everything},
		{"warning drops notice, info and debug and keeps a line without a priority", 4, nil, []string{"disk full", "slow request", "audit line"}},
		{"emerg keeps only a line without a priority", 0, nil, []string{"audit line"}},
		{"a program alone is compared whole", 7, []logship.Rule{{Program: "dovecot"}}, without("imap-login: Login: user=<a>", "imap-login: Disconnected")},
		{"a program alone, case sensitive", 7, []logship.Rule{{Program: "Dovecot"}}, everything},
		{"a priority alone drops that priority and less severe ones", 7, []logship.Rule{{Priority: prio(4)}}, []string{"disk full", "audit line"}},
		{"a priority never matches a line without one", 7, []logship.Rule{{Priority: prio(7)}}, without("debug detail")},
		{"a message alone is searched anywhere", 7, []logship.Rule{{Message: regexp.MustCompile(`login`)}}, without("imap-login: Login: user=<a>", "imap-login: Disconnected")},
		{"a message is case sensitive", 7, []logship.Rule{{Message: regexp.MustCompile(`LOGIN`)}}, without("Request LOGIN noted")},
		{"an anchored message", 7, []logship.Rule{{Message: regexp.MustCompile(`^imap-login: Login: `)}}, without("imap-login: Login: user=<a>")},
		{"a message in the middle of a line", 7, []logship.Rule{{Message: regexp.MustCompile(`Login: user`)}}, without("imap-login: Login: user=<a>")},
		{"program and message", 7, []logship.Rule{{Program: "dovecot", Message: regexp.MustCompile(`^imap-login: Login: `)}}, without("imap-login: Login: user=<a>")},
		{"program and message that do not match together", 7, []logship.Rule{{Program: "dovecot-lda", Message: regexp.MustCompile(`^imap-login`)}}, everything},
		{"program and priority", 7, []logship.Rule{{Program: "app", Priority: prio(5)}}, without("request ok", "Request LOGIN noted")},
		{"all four conditions", 7, []logship.Rule{{Log: "journal", Program: "dovecot", Priority: prio(6), Message: regexp.MustCompile(`Disconnected`)}}, without("imap-login: Disconnected")},
		{"all four conditions, one of them wrong", 7, []logship.Rule{{Log: "journal", Program: "cron", Priority: prio(6), Message: regexp.MustCompile(`Disconnected`)}}, everything},
		{"a log that is a configured path does not match the journal", 7, []logship.Rule{{Log: "/var/log/mail.log"}}, everything},
		{"two rules drop what either matches", 7, []logship.Rule{{Program: "dovecot-lda"}, {Message: regexp.MustCompile(`disk`)}}, without("delivered", "disk full")},
		{"a rule without a condition drops nothing", 7, []logship.Rule{{}}, everything},
		{"the minimum and a rule together", 4, []logship.Rule{{Message: regexp.MustCompile(`slow`)}}, []string{"disk full", "audit line"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := newWorld(t)
			w.online(seqID(1), filterEntries())
			w.position(seqID(1), 0)
			w.cfg.MinPriority = tt.min
			w.cfg.Drop = tt.rules
			w.start()

			recs := w.drain(5)

			requireMessages(t, "shipped lines", messages(recs), tt.want)
			requireNoGaps(t, "the runs", recs)
		})
	}
}

func TestFilter_AppliesToFileLines(t *testing.T) {
	tests := []struct {
		name  string
		min   uint8
		rules func(path string) []logship.Rule
		want  []string
	}{
		{"the minimum never drops a line without a priority", 0, func(string) []logship.Rule { return nil }, []string{"file 3", "file 4"}},
		{"a rule for the journal leaves the file alone", 7, func(string) []logship.Rule { return []logship.Rule{{Log: "journal"}} }, []string{"file 3", "file 4"}},
		{"a rule for the file drops its lines", 7, func(path string) []logship.Rule { return []logship.Rule{{Log: path}} }, nil},
		{"a rule for the file and a message", 7, func(path string) []logship.Rule {
			return []logship.Rule{{Log: path, Message: regexp.MustCompile(`3$`)}}
		}, []string{"file 4"}},
		{"a priority never matches a line without one", 7, func(path string) []logship.Rule {
			return []logship.Rule{{Log: path, Priority: prio(0)}, {Priority: prio(7)}}
		}, []string{"file 3", "file 4"}},
		{"a program of the file", 7, func(string) []logship.Rule { return []logship.Rule{{Program: "app"}} }, nil},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			w := mailWorld(t)
			w.cfg.MinPriority = tt.min
			w.cfg.Drop = tt.rules(w.logPath("mail.log"))
			w.start()
			w.step()
			w.appendLog("mail.log", fileLines(3, 4))

			recs := w.drain(5)

			requireMessages(t, "shipped lines", fileMsgs(recs), tt.want)
			requireNoGaps(t, "the runs", recs)
		})
	}
}
