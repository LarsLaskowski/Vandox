package model_test

import (
	"math"
	"strconv"
	"strings"
	"testing"

	"github.com/LarsLaskowski/Vandox/internal/model"
)

func validMetric() *model.MetricPoint {
	return &model.MetricPoint{
		Name:   "node_filesystem_free_bytes",
		Value:  1536.5,
		Unit:   "bytes",
		Labels: map[string]string{"mount": "/var", "device": "sda1"},
	}
}

func TestMetricPoint_Kind(t *testing.T) {
	if got := validMetric().Kind(); got != model.KindMetric {
		t.Errorf("Kind() = %q, want %q", got, model.KindMetric)
	}
}

func TestMetricPoint_Validate_Valid(t *testing.T) {
	labels32 := make(map[string]string)
	for i := range model.MaxLabels {
		labels32["k"+strconv.Itoa(i)] = "v"
	}
	tests := []struct {
		name string
		p    *model.MetricPoint
	}{
		{"fully populated", validMetric()},
		{"minimal", &model.MetricPoint{Name: "cpu", Value: 0}},
		{"negative value", &model.MetricPoint{Name: "temp", Value: -12.5}},
		{"name of max length", &model.MetricPoint{Name: strings.Repeat("a", model.MaxNameBytes)}},
		{"max labels", &model.MetricPoint{Name: "cpu", Labels: labels32}},
		{"label value of max short text", &model.MetricPoint{Name: "cpu", Labels: map[string]string{"mount": strings.Repeat("v", model.MaxShortTextBytes)}}},
		{"name with allowed punctuation", &model.MetricPoint{Name: "a0._:/@+-x"}},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) { requireValid(t, tc.p) })
	}
}

func TestMetricPoint_Validate_Invalid(t *testing.T) {
	labels33 := make(map[string]string)
	for i := range model.MaxLabels + 1 {
		labels33["k"+strconv.Itoa(i)] = "v"
	}
	set := func(k, v string) func(p *model.MetricPoint) {
		return func(p *model.MetricPoint) { p.Labels = map[string]string{k: v} }
	}
	cases := []invalidCase[*model.MetricPoint]{
		{"name empty", func(p *model.MetricPoint) { p.Name = "" }, "name"},
		{"name starts with dash", func(p *model.MetricPoint) { p.Name = "-x" }, "name"},
		{"name with space", func(p *model.MetricPoint) { p.Name = "a b" }, "name"},
		{"name too long", func(p *model.MetricPoint) { p.Name = strings.Repeat("a", model.MaxNameBytes+1) }, "name"},
		{"value NaN", func(p *model.MetricPoint) { p.Value = math.NaN() }, "value"},
		{"value positive infinity", func(p *model.MetricPoint) { p.Value = math.Inf(1) }, "value"},
		{"value negative infinity", func(p *model.MetricPoint) { p.Value = math.Inf(-1) }, "value"},
		{"unit starts with dash", func(p *model.MetricPoint) { p.Unit = "-x" }, "unit"},
		{"unit too long", func(p *model.MetricPoint) { p.Unit = strings.Repeat("u", model.MaxNameBytes+1) }, "unit"},
		{"too many labels", func(p *model.MetricPoint) { p.Labels = labels33 }, "labels"},
		{"label key with newline", set("a\nb", "v"), `labels["a\nb"]`},
		{"label key too long", set(strings.Repeat("k", 200), "v"), "labels[" + strconv.Quote(strings.Repeat("k", 128)) + "...]"},
		{"label key empty", set("", "v"), `labels[""]`},
		{"label value too long", set("mount", strings.Repeat("v", model.MaxShortTextBytes+1)), `labels["mount"]`},
	}
	runInvalid(t, validMetric, cases)
}

func TestMetricPoint_Validate_LabelKeysDifferingOnlyInCase(t *testing.T) {
	const reason = "duplicate key ignoring case"
	cases := []struct {
		name   string
		labels map[string]string
		field  string
	}{
		{"Mount and mount", map[string]string{"Mount": "a", "mount": "b"}, `labels["mount"]`},
		{"mixed case in the middle of three keys", map[string]string{"a": "1", "B": "2", "b": "3"}, `labels["b"]`},
		{"three spellings name the second one in byte order", map[string]string{"MOUNT": "1", "Mount": "2", "mount": "3"}, `labels["Mount"]`},
		{"other keys do not hide the duplicate", map[string]string{"device": "sda1", "Device": "sda2", "mount": "/var"}, `labels["device"]`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := validMetric()
			p.Labels = tc.labels
			fe := fieldErrorOf(t, p.Validate(), tc.field)
			if fe.Reason != reason {
				t.Errorf("Validate() reason = %q, want %q", fe.Reason, reason)
			}
		})
	}
}

func TestMetricPoint_Validate_LabelKeysDifferingInMoreThanCase(t *testing.T) {
	cases := []struct {
		name   string
		labels map[string]string
	}{
		{"one key longer", map[string]string{"mount": "a", "mounts": "b"}},
		{"different letters", map[string]string{"a": "1", "b": "2"}},
		{"upper and lower case of different words", map[string]string{"Mount": "a", "device": "b"}},
		{"underscore and dash", map[string]string{"a_b": "1", "a-b": "2"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := validMetric()
			p.Labels = tc.labels
			requireValid(t, p)
		})
	}
}
