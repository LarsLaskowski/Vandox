package procstats

// parseCmdline turns the raw content of /proc/<pid>/cmdline into text; more tells that the file holds more than raw.
func parseCmdline(raw []byte, more bool) (text string, truncated bool) {
	return "", false
}

// cleanText makes s valid UTF-8 without control characters and cuts it to at most limit bytes at a character boundary.
func cleanText(s string, limit int) (text string, cut bool) {
	return "", false
}
