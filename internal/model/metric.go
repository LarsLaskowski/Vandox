package model

import (
	"errors"
)

// MetricPoint is one numeric measurement.
type MetricPoint struct {
	Name   string            `json:"name"`
	Value  float64           `json:"value"`
	Unit   string            `json:"unit,omitempty"`
	Labels map[string]string `json:"labels,omitempty"`
}

// Kind returns KindMetric.
func (p *MetricPoint) Kind() Kind { return "" }

// Validate checks the payload.
func (p *MetricPoint) Validate() error { return errors.New("not implemented") }
