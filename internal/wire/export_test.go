package wire

// CheckText exposes checkText to the external tests.
func CheckText(line []byte) error { return checkText(line) }
