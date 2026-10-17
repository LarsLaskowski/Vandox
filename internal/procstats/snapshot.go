package procstats

import "github.com/LarsLaskowski/Vandox/internal/model"

// checkProcessEntry validates and encodes p alone and returns its encoded size.
func checkProcessEntry(p *model.ProcessSample) (size int, err error) {
	return 0, errNotImplemented
}

// checkProgramEntry validates and encodes a alone and returns its encoded size.
func checkProgramEntry(a *model.ProgramAggregate) (size int, err error) {
	return 0, errNotImplemented
}

// fillSnapshot keeps entries within budget bytes (programs first, at most budget/2); complete is false when one was left out.
func fillSnapshot(procs []model.ProcessSample, progs []model.ProgramAggregate, budget int) (snap model.ProcessSnapshot, complete bool) {
	return model.ProcessSnapshot{}, false
}
