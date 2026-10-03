// Command vandox-agent runs on the monitored Linux server and ships data to the backend.
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
		fmt.Println(version.String("vandox-agent"))
		return
	}
	flag.Usage()
}
