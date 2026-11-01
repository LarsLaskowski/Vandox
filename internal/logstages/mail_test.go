package logstages

import (
	"strings"
	"testing"
	"time"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

const (
	smtpd    = "postfix/smtpd"
	proxymap = "postfix/proxymap"
	dovecot  = "dovecot"

	gone          = "Server has gone away"
	mysqlGone     = "MySQL server has gone away"
	connectFailed = "Connect failed to database (mail): " + gone
	mysqlFailed   = "mysql(localhost): " + connectFailed
)

// keep marks a case whose line keeps the priority it had.
const keep uint8 = 255

type mailCase struct {
	program string
	message string
	// priority is the priority a marker sets, keep for a line that keeps the priority it had.
	priority uint8
	event    string
}

// mailCases are the cases of the C# MailLineClassifierTests that the Go port has to give the same result.
func mailCases() []mailCase {
	cases := []mailCase{
		// Postfix markers.
		{smtpd, "warning: hostname mx.example.net does not resolve to address 192.0.2.9", 4, ""},
		{smtpd, "error: open database /etc/postfix/x.db: No such file or directory", 3, ""},
		{smtpd, "fatal: the Postfix mail system is not running", 2, "postfix.fatal"},
		{smtpd, "panic: myfree: corrupt or unallocated memory block", 2, "postfix.panic"},
		{smtpd, "warning: ", 4, ""},
		{smtpd, "fatal: ", 2, "postfix.fatal"},
		{smtpd, "panic: warning: x", 2, "postfix.panic"},
		{smtpd, "connect from unknown[192.0.2.9]", keep, ""},
		{smtpd, "Warning: x", keep, ""},
		{smtpd, "FATAL: x", keep, ""},
		{smtpd, "Fatal: x", keep, ""},
		{smtpd, "fatal:x", keep, ""},
		{smtpd, " fatal: x", keep, ""},
		{smtpd, "x fatal: y", keep, ""},
		{smtpd, "info: x", keep, ""},
		{smtpd, "warning:", keep, ""},
		{smtpd, "", keep, ""},
		{smtpd, "warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <fatal: x>", 4, ""},
		{"postfix/qmgr", "fatal: x", 2, "postfix.fatal"},
		{"postfix/submission/smtpd", "fatal: x", 2, "postfix.fatal"},
		{"postfix/smtps/smtpd", "panic: x", 2, "postfix.panic"},
		{"postfix-out/smtp", "error: x", 3, ""},
		{"postfix/postfix-script", "fatal: x", 2, "postfix.fatal"},

		// Programs the classification leaves alone.
		{"psa-pc-remote", "Error: connect to database failed", keep, ""},
		{"spamd", "fatal: something went wrong", keep, ""},
		{"opendkim", "warning: key table not found", keep, ""},
		{"kernel", "Panic: not syncing", keep, ""},
		{"postfixadmin/x", "fatal: x", keep, ""},
		{"postfix", "warning: x", keep, ""},
		{"postfix/", "error: x", keep, ""},
		{"postfix/submission/", "error: x", keep, ""},
		{"postfix-out", "error: x", keep, ""},
		{"postfixadmin", "error: x", keep, ""},
		{"Postfix/smtpd", "panic: x", keep, ""},
		{"mypostfix/smtpd", "panic: x", keep, ""},
		{"dovecot-lda", "Fatal: x", keep, ""},
		{"dovecot/imap", "Panic: x", keep, ""},
		{"Dovecot", "Panic: x", keep, ""},
		{"imap-login", "Error: x", keep, ""},
		{"spamd", "warning: connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)", keep, ""},
		{"", "fatal: x", keep, ""},

		// Dovecot markers at the start and after the first segment.
		{dovecot, "auth: Debug: Loading modules from directory: /usr/lib/dovecot/modules/auth", 7, ""},
		{dovecot, "master: Warning: Killed with signal 15 (by pid=1 uid=0 code=kill)", 4, ""},
		{dovecot, "master: Error: service(imap): child 2301 killed with signal 9", 3, ""},
		{dovecot, "auth: Fatal: master: service(auth): child 2290 returned error 89", 2, "dovecot.fatal"},
		{dovecot, "imap(alice@example.org)<2301><AbCdEfGh>: Panic: file mail-index.c: line 42 (mail_index_open): assertion failed: (index != NULL)", 2, "dovecot.panic"},
		{dovecot, "Fatal: Dovecot is already running", 2, "dovecot.fatal"},
		{dovecot, "Fatal: Panic: x", 2, "dovecot.fatal"},
		{dovecot, "Panic: Fatal: x", 2, "dovecot.panic"},
		{dovecot, "Debug: x", 7, ""},
		{dovecot, "Warning: ", 4, ""},
		{dovecot, "imap-login: Login: user=<alice@example.org>, method=PLAIN, rip=198.51.100.7", keep, ""},
		{dovecot, "imap-login: Disconnected (auth failed, 1 attempts in 2 secs): user=<x: Panic: y>, rip=192.0.2.9", keep, ""},
		{dovecot, "imap-login: Disconnected: Panic: x", keep, ""},
		{dovecot, "a b: Panic: x", keep, ""},
		{dovecot, ": Panic: x", keep, ""},
		{dovecot, "auth: panic: x", keep, ""},
		{dovecot, "auth: PANIC: x", keep, ""},
		{dovecot, "fatal: x", keep, ""},
		{dovecot, "auth: Panic:x", keep, ""},
		{dovecot, "master: Info: x", keep, ""},
		{dovecot, "Info: x", keep, ""},
		{dovecot, "auth: info: x", keep, ""},
		{dovecot, "", keep, ""},

		// Database messages of Postfix.
		{proxymap, "warning: connect to mysql server localhost: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)", 4, "postfix.database_unavailable"},
		{proxymap, "warning: mysql:/etc/postfix/mysql-virtual.cf: query failed: Lost connection to MySQL server during query", 4, "postfix.database_unavailable"},
		{proxymap, "error: connect to mysql server localhost: Lost connection to MySQL server at 'reading initial communication packet'", 3, "postfix.database_unavailable"},
		{proxymap, "warning: connect to mysql server unix:/var/run/mysqld/mysqld.sock: Can't connect to local MySQL server through socket '/var/run/mysqld/mysqld.sock' (2)", 4, "postfix.database_unavailable"},
		{proxymap, "warning: connect to mysql server inet:db.example.org:3306: Can't connect to MySQL server on 'db.example.org' (111)", 4, "postfix.database_unavailable"},
		{proxymap, "warning: connect to mysql server localhost: Access denied for user 'postfix'@'localhost'", 4, ""},
		{proxymap, "warning: mysql:/etc/postfix/x.cf: query failed: You have an error in your SQL syntax", 4, ""},
		{proxymap, "warning: connect to pgsql server localhost: could not connect", 4, ""},
		{proxymap, "warning: connect to mysql server localhost", 4, ""},
		{smtpd, "warning: Illegal address syntax from unknown[192.0.2.9] in MAIL command: <" + mysqlGone + ">", 4, ""},
		{smtpd, "warning: x connect to mysql server h: Can't connect to MySQL server on 'h' (111)", 4, ""},
		{proxymap, "warning: connect to mysql server localhost: can't connect to MySQL server", 4, ""},
		{proxymap, "connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)", keep, ""},
		{smtpd, "warning: mysql:/etc/postfix/x.cf lookup error for \"a: query failed: " + mysqlGone + "@x\"", 4, ""},
		{"postfix/trivial-rewrite", "warning: mysql:/etc/postfix/x.cf 'domain' pattern match failed for 'a: query failed: " + mysqlGone + "@x'", 4, ""},
		{proxymap, "warning: mysql:: query failed: " + mysqlGone, 4, ""},
		{proxymap, "warning: mysql:/etc/postfix/x.cf: query failed:" + mysqlGone, 4, ""},
		{proxymap, "warning: connect to mysql server : " + mysqlGone, 4, ""},
		{proxymap, "warning: connect to mysql server a b: Can't connect to MySQL server on 'h' (111)", 4, ""},
		{proxymap, "warning: connect to mysql server localhost: Can't connect to a MySQL server", 4, ""},
		{proxymap, "warning: connect to mysql server localhost: Can’t connect to MySQL server on 'h' (111)", 4, ""},
		{proxymap, "warning: connect to mysql server localhost:  " + mysqlGone, 4, ""},
		{proxymap, "warning: connect to mysql server localhost: mysql server has gone away", 4, ""},
		{proxymap, "warning: mysql:/etc/postfix/x.cf: query failed: lost connection to MySQL server", 4, ""},
		{proxymap, "error: mysql:/etc/postfix/x.cf: query failed: Lost connection", 3, ""},

		// Database messages of Dovecot's auth-worker.
		{dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2) - waiting for 1 seconds before retry", 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Warning: sqlpool(mysql): Query failed, retrying: " + mysqlGone, 4, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Error: mysql: Connect failed to database (mail): Can't connect to local server through socket '/run/mysqld/mysqld.sock' (2)", 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Error: mysql(/run/mysqld/mysqld.sock): " + connectFailed, 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Error: sqlpool(mysql): " + mysqlFailed, 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Error: sqlpool(mysql): mysql: " + mysqlFailed, 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Warning: mysql(localhost): Query failed, retrying: " + mysqlGone, 4, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(0): Error: " + mysqlFailed, 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(99999999999999999999): Error: " + mysqlFailed, 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(2290): Error: mysql(localhost): Connect failed to database (mail): Lost connection to server", 3, "dovecot.database_unavailable"},
		{dovecot, "auth-worker(1): Error: user x: Connect failed to database (d): Can't connect to MySQL server on 'h' (111)", 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost): Connect failed to database (mail): Access denied for user 'mail'", 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost): Connect failed to database (mail", 3, ""},
		{dovecot, "auth-worker(1): Error: Query failed, aborting: SELECT password FROM users", 3, ""},
		{dovecot, "auth-worker(1): " + mysqlFailed, keep, ""},
		{dovecot, "auth-worker(1): Debug: mysql(localhost): Query failed, retrying: " + gone, 7, ""},
		{dovecot, "auth-worker(1): Info: mysql(localhost): " + connectFailed, keep, ""},
		{dovecot, "lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a: Query failed, retrying: " + mysqlGone + "@x>: failed to store into mailbox 'INBOX': Quota exceeded (mailbox for user is full)", 3, ""},
		{dovecot, "lmtp(alice@example.org)<2301><AbCdEfGh>: Error: sieve: msgid=<a@x>: " + connectFailed, 3, ""},
		{dovecot, "auth: Error: sql(a,1.2.3.4): " + connectFailed, 3, ""},
		{dovecot, "imap(alice@example.org)<2301><AbCdEfGh>: Error: " + mysqlFailed, 3, ""},
		{dovecot, "Error: " + mysqlFailed, 3, ""},
		{dovecot, "auth-worker: Error: " + mysqlFailed, 3, ""},
		{dovecot, "auth-worker(): Error: " + mysqlFailed, 3, ""},
		{dovecot, "auth-worker(12a): Error: " + mysqlFailed, 3, ""},
		{dovecot, "xauth-worker(1): Error: " + mysqlFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: sqlpool(mysql): mysql: mysql: " + mysqlFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(local host): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(a)b): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: sqlpool(): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: pgsql(localhost): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: Mysql(localhost): " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql : " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost) " + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost):" + connectFailed, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost): Connect failed to database (mail):" + gone, 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost): Connect failed to database (mail): server has gone away", 3, ""},
		{dovecot, "auth-worker(1): Warning: mysql(localhost): Query failed, retrying:" + mysqlGone, 4, ""},
		{dovecot, "auth-worker(1): Warning: mysql(localhost): Query failed: " + mysqlGone, 4, ""},
		{dovecot, "auth-worker(1): Warning: mysql(localhost): query failed, retrying: " + mysqlGone, 4, ""},

		// A fatal or panic marker wins over the database message after it.
		{proxymap, "fatal: connect to mysql server localhost: Can't connect to MySQL server on 'h' (111)", 2, "postfix.fatal"},
		{proxymap, "panic: mysql:/etc/postfix/x.cf: query failed: " + mysqlGone, 2, "postfix.panic"},
		{dovecot, "auth-worker(1): Panic: sqlpool(mysql): Query failed, retrying: " + mysqlGone, 2, "dovecot.panic"},
		{dovecot, "auth-worker(1): Fatal: " + mysqlFailed, 2, "dovecot.fatal"},

		// Odd, cut and empty messages.
		{smtpd, ":", keep, ""},
		{smtpd, "warning: connect to mysql server ", 4, ""},
		{smtpd, "warning: connect to mysql server :", 4, ""},
		{smtpd, "warning: connect to mysql server a:", 4, ""},
		{smtpd, "warning: mysql:", 4, ""},
		{smtpd, "warning: mysql:a: query failed:", 4, ""},
		{dovecot, ": ", keep, ""},
		{dovecot, "Warning", keep, ""},
		{dovecot, "auth-worker(", keep, ""},
		{dovecot, "auth-worker(1", keep, ""},
		{dovecot, "auth-worker(1): ", keep, ""},
		{dovecot, "auth-worker(1): Error: ", 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(", 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost", 3, ""},
		{dovecot, "auth-worker(1): Error: mysql(localhost)", 3, ""},
		{dovecot, "auth-worker(1): Error: sqlpool(mysql): mysql: mysql: mysql", 3, ""},
	}

	return cases
}

func TestClassifyMail(t *testing.T) {
	for _, tc := range mailCases() {
		for _, before := range []*uint8{nil, u8(6), u8(3)} {
			name := tc.program + " " + tc.message
			if len(name) > 60 {
				name = name[:60]
			}

			t.Run(name+" before="+prioString(before), func(t *testing.T) { checkMailCase(t, tc, before) })
		}
	}
}

func checkMailCase(t *testing.T, tc mailCase, before *uint8) {
	t.Helper()

	line := &model.LogLine{Log: "mail.log", Host: "mail-1", Program: tc.program, PID: 2210, Priority: before, Message: tc.message, Truncated: true}

	ClassifyMail(line)

	want := before
	if tc.priority != keep {
		want = u8(tc.priority)
	}

	if prioString(line.Priority) != prioString(want) {
		t.Errorf("ClassifyMail(%q, %q).Priority = %s, want %s", tc.program, tc.message, prioString(line.Priority), prioString(want))
	}

	if line.Event != tc.event {
		t.Errorf("ClassifyMail(%q, %q).Event = %q, want %q", tc.program, tc.message, line.Event, tc.event)
	}

	if line.Message != tc.message || line.Program != tc.program || line.Log != "mail.log" || line.Host != "mail-1" || line.PID != 2210 || !line.Truncated {
		t.Errorf("ClassifyMail changed more than priority and event: %+v", *line)
	}
}

func TestClassifyMail_ALongMessageIsStillClassified(t *testing.T) {
	postfix := &model.LogLine{Program: proxymap, Message: "warning: connect to mysql server localhost: Lost connection to MySQL server" + strings.Repeat("x", 16000)}
	dovecotLine := &model.LogLine{Program: dovecot, Message: "auth-worker(1): Warning: sqlpool(mysql): Query failed, retrying: Server has gone away" + strings.Repeat("y", 16000)}
	farAway := &model.LogLine{Program: dovecot, Message: "auth-worker(1): Warning: sqlpool(mysql): Query failed, retrying: " + strings.Repeat("z", 16000) + gone}

	ClassifyMail(postfix)
	ClassifyMail(dovecotLine)
	ClassifyMail(farAway)

	if postfix.Event != "postfix.database_unavailable" {
		t.Errorf("Postfix, phrase at the start of the client error: Event = %q, want postfix.database_unavailable", postfix.Event)
	}

	if dovecotLine.Event != "dovecot.database_unavailable" {
		t.Errorf("Dovecot, phrase at the start of the client error: Event = %q, want dovecot.database_unavailable", dovecotLine.Event)
	}

	if farAway.Event != "" {
		t.Errorf("phrase not at the start of the client error: Event = %q, want none", farAway.Event)
	}
}

// hostileLines returns lines that make a naive scan do repeated work, each of the given length.
func hostileLines(length int) []*model.LogLine {
	return []*model.LogLine{
		{Program: proxymap, Message: "warning: connect to mysql server " + strings.Repeat("a", length)},
		{Program: proxymap, Message: "warning: mysql:" + strings.Repeat("a: ", length/3)},
		{Program: dovecot, Message: "auth-worker(1): Error: " + strings.Repeat("mysql: ", length/7)},
		{Program: dovecot, Message: strings.Repeat("a: ", length/3) + "Panic: x"},
		{Program: smtpd, Message: strings.Repeat("warning: ", length/9)},
	}
}

// bestOf returns the shortest of three runs of classifying the lines the given number of times.
func bestOf(lines []*model.LogLine, iterations int) time.Duration {
	best := time.Duration(1<<63 - 1)

	for run := 0; run < 3; run++ {
		start := time.Now()

		for i := 0; i < iterations; i++ {
			for _, l := range lines {
				ClassifyMail(l)
			}
		}

		if d := time.Since(start); d < best {
			best = d
		}
	}

	return best
}

func TestClassifyMail_TimeGrowsLinearlyWithTheLengthOfTheMessage(t *testing.T) {
	const (
		baseLength = 2048
		factor     = 8
		iterations = 200
	)

	small := hostileLines(baseLength)
	large := hostileLines(baseLength * factor)

	bestOf(small, 1)
	bestOf(large, 1)

	smallTime := bestOf(small, iterations)
	largeTime := bestOf(large, iterations)

	ratio := float64(largeTime) / float64(max(smallTime, 1))

	// A linear scan gives about 8, a quadratic one about 64; the limit is at least three times the ratio measured.
	if ratio > 30 {
		t.Errorf("a message of %d bytes takes %.1f times as long as one of %d bytes, want at most 30 (linear is about %d)", baseLength*factor, ratio, baseLength, factor)
	}
}
