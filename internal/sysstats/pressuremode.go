package sysstats

import "math/bits"

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
//
// The mode turns on when less than a tenth of the memory is available or avg10 reaches PressureOnAvg10. It turns off
// only when at least 15 % is available and avg10 is below PressureOffAvg10 or not available; otherwise it keeps its mode.
// The memory comparisons are exact: the products are formed in 128 bits.
func NextPressure(high bool, r PressureReading) bool {
	if lessThanProduct(r.MemAvailable, 10, r.MemTotal, 1) || (r.HasAvg10 && r.Avg10 >= PressureOnAvg10) {
		return true
	}
	calm := !r.HasAvg10 || r.Avg10 < PressureOffAvg10
	if calm && !lessThanProduct(r.MemAvailable, 20, r.MemTotal, 3) {
		return false
	}
	return high
}

// lessThanProduct reports whether a*b < c*d, exactly.
func lessThanProduct(a, b, c, d uint64) bool {
	hiL, loL := bits.Mul64(a, b)
	hiR, loR := bits.Mul64(c, d)
	return hiL < hiR || (hiL == hiR && loL < loR)
}
