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

// Texts of the fixed statements, as the captures hold them.
const (
	textStatus       = "SHOW GLOBAL STATUS"
	textVariables    = "SHOW GLOBAL VARIABLES"
	textProcesslist  = "SHOW FULL PROCESSLIST"
	textCurrentUser  = "SELECT CURRENT_USER()"
	textPrivileges   = "SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%" + User + "%'"
	cellNameLimit    = 64
	cellValueLimit   = 1025
	cellAccountLimit = 1024
)

// text returns the statement text.
func (s statement) text() string {
	switch s {
	case globalStatus:
		return textStatus
	case globalVariables:
		return textVariables
	case fullProcesslist:
		return textProcesslist
	case currentUser:
		return textCurrentUser
	case userPrivileges:
		return textPrivileges
	}
	return ""
}

// step returns the name of the statement for error texts.
func (s statement) step() string {
	switch s {
	case globalStatus:
		return "status"
	case globalVariables:
		return "variables"
	case fullProcesslist:
		return "processlist"
	case currentUser, userPrivileges:
		return "account"
	}
	return ""
}

// columns returns the expected columns.
func (s statement) columns() []column {
	switch s {
	case globalStatus, globalVariables:
		return []column{{name: "Variable_name", limit: cellNameLimit, cut: true}, {name: "Value", limit: cellValueLimit, cut: true}}
	case fullProcesslist:
		return []column{
			{name: "Id", limit: 20}, {name: "User", limit: 512, cut: true}, {name: "Host", limit: 512, cut: true},
			{name: "db", limit: 256, cut: true}, {name: "Command", limit: 32, cut: true}, {name: "Time", limit: 20},
			{name: "State", limit: 256, cut: true}, {name: "Info", limit: 16384, cut: true}, {name: "Progress", limit: 64},
		}
	case currentUser:
		return []column{{name: "CURRENT_USER()", limit: cellAccountLimit, cut: true}}
	case userPrivileges:
		return []column{{name: "PRIVILEGE_TYPE", limit: cellNameLimit, cut: true}}
	}
	return nil
}
