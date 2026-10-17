package sysstats

// Thresholds of memory pressure mode: the mode turns on at PressureOnAvg10 and off below PressureOffAvg10.
const (
	PressureOnAvg10  = 10.0
	PressureOffAvg10 = 5.0
)

// PressureReading is what the decision about memory pressure mode reads.
type PressureReading struct {
	MemAvailable, MemTotal uint64  // bytes
	Avg10                  float64 // memory some avg10
	HasAvg10               bool
}

// NextPressure returns whether memory pressure mode is on after r, given that it was on before when high is true.
func NextPressure(high bool, r PressureReading) bool {
	return false
}
