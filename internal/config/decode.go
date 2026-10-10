package config

import (
	"bytes"
	"errors"
	"io"
	"reflect"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"go.yaml.in/yaml/v3"
)

const (
	parserReason     = "not valid YAML (syntax error or alias to an undefined anchor)"
	secretHint       = "; secrets are read only from environment variables, never from this file"
	maxShownKeyBytes = 31
)

// allowedTags are the resolved tags the decoder accepts. Everything else (custom tags, !!binary, !!set,
// !!merge, a %TAG re-mapped handle) is rejected on the resolved Node.Tag, never on the source text.
var allowedTags = map[string]bool{
	"!!str": true, "!!int": true, "!!bool": true, "!!float": true, "!!null": true,
	"!!timestamp": true, "!!map": true, "!!seq": true,
}

// decoder carries what the walk over the node tree needs.
type decoder struct {
	file  string
	lines map[string]int
}

// decodeStrict decodes the single YAML document in data into out (a non-nil pointer to a struct whose
// fields already hold the defaults) and returns the line of every key it set, by dotted path.
//
// It parses into a yaml.Node and walks the tree itself, so no yaml.v3 error text (which can quote the
// document) is ever passed on, and constructs the schema has no use for are rejected.
func decodeStrict(file string, data []byte, out any) (map[string]int, error) {
	d := &decoder{file: file, lines: map[string]int{}}
	rv := reflect.ValueOf(out)
	if rv.Kind() != reflect.Pointer || rv.IsNil() || rv.Elem().Kind() != reflect.Struct {
		return nil, &KeyError{File: file, Reason: "internal error: decode target must be a pointer to a struct"}
	}

	dec := yaml.NewDecoder(bytes.NewReader(data))
	var doc yaml.Node
	if err := dec.Decode(&doc); err != nil {
		if errors.Is(err, io.EOF) {
			return d.lines, nil
		}
		return nil, parserError(file, err)
	}
	var second yaml.Node
	switch err := dec.Decode(&second); {
	case err == nil:
		return nil, &KeyError{File: file, Line: second.Line, Reason: "a second YAML document is not supported"}
	case !errors.Is(err, io.EOF):
		return nil, parserError(file, err)
	}

	if len(doc.Content) == 0 {
		return d.lines, nil
	}
	root := doc.Content[0]
	if err := d.checkNode("", root); err != nil {
		return nil, err
	}
	if isNull(root) {
		return d.lines, nil
	}
	if root.Kind != yaml.MappingNode {
		return nil, &KeyError{File: file, Line: root.Line, Reason: "top level must be a mapping"}
	}
	if err := d.walkMapping("", root, rv.Elem()); err != nil {
		return nil, err
	}
	return d.lines, nil
}

// parserError turns an error of the YAML parser into a value-free *KeyError. Only the line is kept.
func parserError(file string, err error) *KeyError {
	line := 0
	if rest, ok := strings.CutPrefix(err.Error(), "yaml: line "); ok {
		end := 0
		for end < len(rest) && rest[end] >= '0' && rest[end] <= '9' {
			end++
		}
		if n, convErr := strconv.Atoi(rest[:end]); convErr == nil {
			line = n
		}
	}
	return &KeyError{File: file, Line: line, Reason: parserReason}
}

// isNull reports whether n is a null scalar.
func isNull(n *yaml.Node) bool {
	return n.Kind == yaml.ScalarNode && n.Tag == "!!null"
}

// checkNode applies the rules every node must satisfy: no anchor or alias, an allowed resolved tag, and
// a null tag only on a null spelling. key is the dotted path reported with the error.
func (d *decoder) checkNode(key string, n *yaml.Node) error {
	fail := func(reason string) error {
		return &KeyError{File: d.file, Line: n.Line, Key: key, Reason: reason}
	}
	switch {
	case n.Kind == yaml.AliasNode || n.Anchor != "":
		return fail("anchors and aliases are not supported")
	case !allowedTags[n.Tag]:
		return fail("unsupported tag")
	case isNull(n) && !nullSpelling(n.Value):
		return fail("null tag with a value")
	}
	return nil
}

// nullSpelling reports whether v is one of the null spellings of the YAML core schema.
func nullSpelling(v string) bool {
	switch v {
	case "", "~", "null", "Null", "NULL":
		return true
	}
	return false
}

// checkTree applies checkNode to n and everything below it.
func (d *decoder) checkTree(key string, n *yaml.Node) error {
	if err := d.checkNode(key, n); err != nil {
		return err
	}
	for _, c := range n.Content {
		if err := d.checkTree(key, c); err != nil {
			return err
		}
	}
	return nil
}

// walkMapping walks the entries of mapping, which fills the struct sv. prefix is the dotted path of the
// section ("" at the top level).
func (d *decoder) walkMapping(prefix string, mapping *yaml.Node, sv reflect.Value) error {
	fields := yamlFields(sv.Type())
	seen := map[string]bool{}
	for i := 0; i+1 < len(mapping.Content); i += 2 {
		k, v := mapping.Content[i], mapping.Content[i+1]
		if err := d.checkNode(prefix, k); err != nil {
			return err
		}
		if k.Kind != yaml.ScalarNode {
			return &KeyError{File: d.file, Line: k.Line, Key: prefix, Reason: "key must be a string"}
		}
		idx, known := fields[k.Value]
		if !known {
			return d.unknownKey(prefix, k)
		}
		path := joinPath(prefix, k.Value)
		if seen[k.Value] {
			return &KeyError{File: d.file, Line: k.Line, Key: path, Reason: "duplicate key"}
		}
		seen[k.Value] = true
		if err := d.walkField(path, k, v, sv.Field(idx)); err != nil {
			return err
		}
	}
	return nil
}

// walkField handles the value v of the known key k, whose target is field.
func (d *decoder) walkField(path string, k, v *yaml.Node, field reflect.Value) error {
	if err := d.checkNode(path, v); err != nil {
		return err
	}
	if field.Kind() == reflect.Struct {
		return d.walkSection(path, k, v, field)
	}
	return d.setLeaf(path, k, v, field)
}

// walkSection handles the value of a key that names a section. A null value keeps the defaults.
func (d *decoder) walkSection(path string, k, v *yaml.Node, field reflect.Value) error {
	if isNull(v) {
		return nil
	}
	if v.Kind != yaml.MappingNode {
		return &KeyError{File: d.file, Line: k.Line, Key: path, Reason: "must be a section (a mapping of keys)"}
	}
	return d.walkMapping(path, v, field)
}

// setLeaf stores the value v of the key k into field.
func (d *decoder) setLeaf(path string, k, v *yaml.Node, field reflect.Value) error {
	fail := func(reason string) error {
		return &KeyError{File: d.file, Line: k.Line, Key: path, Reason: reason}
	}
	if isNull(v) {
		return fail("has no value")
	}
	if !leafKind(field.Type()) {
		return fail("unsupported option type")
	}
	if field.Kind() == reflect.String {
		if v.Kind != yaml.ScalarNode {
			return fail("invalid value, want " + describe(field.Type()))
		}
		if !cleanString(v.Value) {
			return fail("must not contain control, format or line-separating characters")
		}
		field.SetString(v.Value)
		d.lines[path] = k.Line
		return nil
	}
	return d.setOther(path, k, v, field)
}

// setOther stores a non-string leaf. The yaml.v3 error text is discarded, as it quotes the value.
func (d *decoder) setOther(path string, k, v *yaml.Node, field reflect.Value) error {
	fail := func(reason string) error {
		return &KeyError{File: d.file, Line: k.Line, Key: path, Reason: reason}
	}
	if err := d.checkTree(path, v); err != nil {
		return err
	}
	if v.Decode(field.Addr().Interface()) != nil {
		return fail("invalid value, want " + describe(field.Type()))
	}
	if field.Kind() == reflect.Slice && field.Type().Elem().Kind() == reflect.String {
		for i := 0; i < field.Len(); i++ {
			if !cleanString(field.Index(i).String()) {
				return fail("must not contain control, format or line-separating characters")
			}
		}
	}
	d.lines[path] = k.Line
	return nil
}

// unknownKey reports the unknown key k below prefix. The name is shown only when it is safe to show.
func (d *decoder) unknownKey(prefix string, k *yaml.Node) error {
	hint := ""
	lower := strings.ToLower(k.Value)
	for _, word := range []string{"token", "password", "secret"} {
		if strings.Contains(lower, word) {
			hint = secretHint
			break
		}
	}
	if safeName(k.Value) {
		return &KeyError{File: d.file, Line: k.Line, Key: joinPath(prefix, k.Value), Reason: "unknown key" + hint}
	}
	return &KeyError{
		File: d.file, Line: k.Line, Key: prefix,
		Reason: "unknown key (name not shown: only 1 to 31 characters of [A-Za-z0-9_-] are shown)" + hint,
	}
}

// safeName reports whether an unknown key name may be echoed: 1 to 31 bytes of [A-Za-z0-9_-]. That is
// shorter than an agent token, and it excludes control characters, dots and the characters of secret formats.
func safeName(s string) bool {
	if len(s) < 1 || len(s) > maxShownKeyBytes {
		return false
	}
	for i := 0; i < len(s); i++ {
		c := s[i]
		ok := c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-'
		if !ok {
			return false
		}
	}
	return true
}

// cleanString reports whether s is valid UTF-8 without characters of the categories Cc, Cf, Zl and Zp.
func cleanString(s string) bool {
	if !utf8.ValidString(s) {
		return false
	}
	for _, r := range s {
		if unicode.In(r, unicode.Cc, unicode.Cf, unicode.Zl, unicode.Zp) {
			return false
		}
	}
	return true
}

// yamlFields maps the explicit yaml tag names of the struct type t to their field index.
func yamlFields(t reflect.Type) map[string]int {
	fields := make(map[string]int, t.NumField())
	for i := 0; i < t.NumField(); i++ {
		name, _, _ := strings.Cut(t.Field(i).Tag.Get("yaml"), ",")
		if name == "" || name == "-" {
			continue
		}
		fields[name] = i
	}
	return fields
}

// leafKind reports whether t is a kind the decoder can store a scalar or a list in.
func leafKind(t reflect.Type) bool {
	switch t.Kind() {
	case reflect.String, reflect.Bool, reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64,
		reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64,
		reflect.Float32, reflect.Float64, reflect.Slice:
		return true
	}
	return false
}

// describe names the YAML form wanted for t.
func describe(t reflect.Type) string {
	if t == reflect.TypeOf(time.Duration(0)) {
		return "a duration such as 5s"
	}
	switch t.Kind() {
	case reflect.String:
		return "a string"
	case reflect.Bool:
		return "true or false"
	case reflect.Float32, reflect.Float64:
		return "a number"
	case reflect.Slice:
		return "a list"
	}
	return "an integer"
}

// joinPath appends name to the dotted path prefix.
func joinPath(prefix, name string) string {
	if prefix == "" {
		return name
	}
	return prefix + "." + name
}
