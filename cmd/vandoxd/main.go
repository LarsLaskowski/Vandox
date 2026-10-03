// Command vandoxd is the Vandox backend with web UI.
package main

import (
	"flag"
	"fmt"

	"github.com/LarsLaskowski/Vandox/internal/version"
)

func main() {
	showVersion := flag.Bool("version", false, "print version, commit and build date and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(version.String("vandoxd"))
		return
	}
	flag.Usage()
}
