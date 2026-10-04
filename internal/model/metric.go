package model

// MetricPoint is one numeric measurement.
type MetricPoint struct {
	Name   string            `json:"name"`
	Value  float64           `json:"value"`
	Unit   string            `json:"unit,omitempty"`
	Labels map[string]string `json:"labels,omitempty"`
}

// Kind returns KindMetric.
func (p *MetricPoint) Kind() Kind { return KindMetric }

// Validate checks the payload.
func (p *MetricPoint) Validate() error {
	if p == nil {
		return nilReceiver()
	}
	if err := checkName("name", p.Name); err != nil {
		return err
	}
	if err := checkFinite("value", p.Value); err != nil {
		return err
	}
	if err := checkOptionalName("unit", p.Unit); err != nil {
		return err
	}
	if len(p.Labels) > MaxLabels {
		return invalid("labels", "too many entries")
	}
	for _, k := range sortedKeys(p.Labels) {
		path := keyed("labels", k)
		if err := checkName(path, k); err != nil {
			return err
		}
		if err := checkShort(path, p.Labels[k]); err != nil {
			return err
		}
	}
	return nil
}
