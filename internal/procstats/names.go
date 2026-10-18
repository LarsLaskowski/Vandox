package procstats

import (
	"strings"
	"unicode/utf8"
)

// commandOf returns the command of a command line, the legacy derivation pinned by names.json: the text inside [...] when
// the whole command line is bracketed, else the part after the last slash of the first word without one trailing colon;
// when that is empty, the command line, and when that is empty too, fallback.
func commandOf(cmdline, fallback string) string {
	name := deriveName(cmdline)
	if name == "" {
		name = cmdline
		if name == "" {
			name = fallback
		}
	}
	return cutAtBoundary(name, maxTextBytes)
}

// deriveName derives the name of a process from its command line; it may be empty.
func deriveName(cmdline string) string {
	if len(cmdline) >= 2 && cmdline[0] == '[' && cmdline[len(cmdline)-1] == ']' {
		return cmdline[1 : len(cmdline)-1]
	}
	word, _, _ := strings.Cut(cmdline, " ")
	word = word[strings.LastIndexByte(word, '/')+1:]
	return strings.TrimSuffix(word, ":")
}

// commandFor returns commandOf with comm as the fallback, or UnnamedCommand when comm is empty, so a command is never empty.
func commandFor(cmdline, comm string) string {
	if comm == "" {
		comm = UnnamedCommand
	}
	return commandOf(cmdline, comm)
}

// programOf returns the default program of command: its first space-separated word, command itself when that is empty.
func programOf(command string) string {
	word, _, _ := strings.Cut(command, " ")
	if word == "" {
		return command
	}
	return word
}

// programFor returns the program of a process: the first matching rule, else programOf. A rule matches by the whole command
// or by a literal prefix of the command line; with AppendNextWord the word after the prefix is appended as "/word".
func programFor(rules []Rule, cmdline, command string) string {
	for i := range rules {
		r := &rules[i]
		if r.Program == "" {
			continue
		}
		switch {
		case r.Command != "" && command == r.Command:
			return r.Program
		case r.Command == "" && r.CmdlinePrefix != "" && strings.HasPrefix(cmdline, r.CmdlinePrefix):
			return appendWord(r, cmdline[len(r.CmdlinePrefix):])
		}
	}
	return programOf(command)
}

// appendWord returns the program of rule r for the command line text after the prefix: the program and, when the rule asks
// for it and the text starts with a word, a slash and that word, cut to maxTextBytes at a character boundary.
func appendWord(r *Rule, rest string) string {
	if !r.AppendNextWord {
		return r.Program
	}
	word, _, _ := strings.Cut(rest, " ")
	if word == "" {
		return r.Program
	}
	return cutAtBoundary(r.Program+"/"+word, maxTextBytes)
}

// cutAtBoundary returns s cut to at most limit bytes at a character boundary.
func cutAtBoundary(s string, limit int) string {
	if len(s) <= limit {
		return s
	}
	cut := limit
	for cut > 0 && !utf8.RuneStart(s[cut]) {
		cut--
	}
	return s[:cut]
}
