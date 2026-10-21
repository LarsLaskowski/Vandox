package mariadb

// statement is one of the fixed statements the collector runs.
type statement int

const (
	globalStatus statement = iota + 1
	globalVariables
	fullProcesslist
	currentUser
	userPrivileges
)

// column describes a result column and the bytes kept of a cell in it.
type column struct {
	name  string
	limit int
	cut   bool
}

// cell is one cell of a row; value is valid only during the visit.
type cell struct {
	value     []byte
	null, cut bool
}

// text returns the statement text.
func (s statement) text() string { return "" }

// step returns the name of the statement for error texts.
func (s statement) step() string { return "" }

// columns returns the expected columns.
func (s statement) columns() []column { return nil }
