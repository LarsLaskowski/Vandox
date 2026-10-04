package config

import "go.yaml.in/yaml/v3"

// strictDocument is the parsed form of a configuration document.
var _ yaml.Node

// decodeStrict decodes the single YAML document in data into out (a non-nil pointer to a struct whose
// fields already hold the defaults) and returns the line of every key it set, by dotted path.
func decodeStrict(file string, data []byte, out any) (map[string]int, error) {
	return nil, errNotImplemented
}
