// Package errorlogline parses the lines of the web server and panel error logs (Plesk panel, nginx, Apache) into syslog lines.
package errorlogline

import "github.com/LarsLaskowski/Vandox/internal/syslogline"

// Format names the layout of an error log.
type Format uint8

const (
	// PleskPanel is the layout of the Plesk panel log.
	PleskPanel Format = iota + 1
	// Nginx is the layout of the nginx error log.
	Nginx
	// Apache is the layout of the Apache error log.
	Apache
)

// Parse parses one line of an error log in the given format, resolving its time with clock.
func Parse(format Format, line []byte, clock *syslogline.Clock) syslogline.Line {
	return syslogline.Line{}
}
