package sysstats_test

import (
	"math"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/sysstats"
)

func TestNextPressure(t *testing.T) {
	const total = 1000
	reading := func(available uint64, avg10 float64, has bool) sysstats.PressureReading {
		return sysstats.PressureReading{MemAvailable: available, MemTotal: total, Avg10: avg10, HasAvg10: has}
	}
	tests := []struct {
		name   string
		r      sysstats.PressureReading
		before bool
		want   bool
	}{
		{"exactly 10 percent available is not low (off)", reading(100, 0, true), false, false},
		{"exactly 10 percent available keeps the mode on", reading(100, 0, true), true, true},
		{"just below 10 percent available turns on", reading(99, 0, true), false, true},
		{"just below 10 percent available stays on", reading(99, 0, true), true, true},
		{"avg10 of 10.00 turns on", reading(900, 10.00, true), false, true},
		{"avg10 above 10.00 turns on", reading(900, 55.5, true), false, true},
		{"avg10 of 9.99 does not turn on", reading(900, 9.99, true), false, false},
		{"avg10 of 9.99 keeps the mode on", reading(900, 9.99, true), true, true},
		{"between 10 and 15 percent keeps off", reading(120, 0, true), false, false},
		{"between 10 and 15 percent keeps on", reading(120, 0, true), true, true},
		{"just below 15 percent keeps on", reading(149, 0, true), true, true},
		{"exactly 15 percent available turns off", reading(150, 0, true), true, false},
		{"above 15 percent available stays off", reading(150, 0, true), false, false},
		{"avg10 of 5.00 keeps on", reading(500, 5.00, true), true, true},
		{"avg10 of 4.99 turns off", reading(500, 4.99, true), true, false},
		{"avg10 between 5 and 10 keeps off", reading(500, 7, true), false, false},
		{"avg10 between 5 and 10 keeps on", reading(500, 7, true), true, true},
		{"memory still low keeps on despite a calm avg10", reading(50, 0, true), true, true},
		{"pressure unavailable and plenty of memory turns off", reading(500, 0, false), true, false},
		{"pressure unavailable and plenty of memory stays off", reading(500, 0, false), false, false},
		{"pressure unavailable and low memory turns on", reading(99, 0, false), false, true},
		{"pressure unavailable in the band keeps on", reading(120, 0, false), true, true},
		{"an avg10 that is not available is ignored", reading(500, 99, false), false, false},
		{"zero available turns on", reading(0, 0, true), false, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := sysstats.NextPressure(tt.before, tt.r); got != tt.want {
				t.Errorf("NextPressure(%v, %+v) = %v, want %v", tt.before, tt.r, got, tt.want)
			}
		})
	}
}

func TestNextPressure_ProductsDoNotOverflow(t *testing.T) {
	huge := sysstats.PressureReading{MemAvailable: 1 << 63, MemTotal: math.MaxUint64}
	maxed := sysstats.PressureReading{MemAvailable: math.MaxUint64, MemTotal: math.MaxUint64}
	nearTenth := sysstats.PressureReading{MemAvailable: math.MaxUint64/10 - 1, MemTotal: math.MaxUint64}

	tests := []struct {
		name   string
		r      sysstats.PressureReading
		before bool
		want   bool
	}{
		{"half of a huge total is not low", huge, false, false},
		{"half of a huge total turns off", huge, true, false},
		{"everything available of a huge total turns off", maxed, true, false},
		{"just below a tenth of a huge total turns on", nearTenth, false, true},
		{"a tenth of a huge total keeps the mode on", sysstats.PressureReading{MemAvailable: math.MaxUint64 / 10, MemTotal: math.MaxUint64 - math.MaxUint64%10}, true, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := sysstats.NextPressure(tt.before, tt.r); got != tt.want {
				t.Errorf("NextPressure(%v, %+v) = %v, want %v", tt.before, tt.r, got, tt.want)
			}
		})
	}
}
