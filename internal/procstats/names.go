package procstats

// commandOf returns the command of a command line, the legacy derivation pinned by names.json.
func commandOf(cmdline, fallback string) string {
	return ""
}

// commandFor returns commandOf with comm as the fallback, or UnnamedCommand when comm is empty.
func commandFor(cmdline, comm string) string {
	return ""
}

// programOf returns the default program of command.
func programOf(command string) string {
	return ""
}

// programFor returns the program of a process: the first matching rule, else programOf.
func programFor(rules []Rule, cmdline, command string) string {
	return ""
}
