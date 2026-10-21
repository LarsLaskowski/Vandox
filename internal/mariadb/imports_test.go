package mariadb

import (
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"
)

const modulePath = "github.com/LarsLaskowski/Vandox/internal/"

// clientImports are the packages the wire client (client.go, packet.go, statement.go) may import.
var clientImports = []string{"bufio", "bytes", "context", "encoding/binary", "errors", "io", "net", "strconv", "syscall", "time"}

// collectorImports are the packages the collector and its sample and derived values may import: the standard library
// of the client plus what reading /proc, logging and the record model need, and the module's own packages.
var collectorImports = append(slices.Clone(clientImports),
	"cmp", "container/heap", "fmt", "io/fs", "log/slog", "math", "math/bits", "net/netip", "os", "slices", "sync", "unicode/utf8",
	modulePath+"connstats", modulePath+"model", modulePath+"procstats", modulePath+"scheduler", modulePath+"sysstats", modulePath+"wire")

// importsOfFile maps each non-test file to the imports it may have.
var importsOfFile = map[string][]string{
	"client.go":    clientImports,
	"packet.go":    clientImports,
	"statement.go": clientImports,
	"collector.go": collectorImports,
	"sample.go":    collectorImports,
	"derived.go":   collectorImports,
}

// allowedSelectorsOf maps a package to the only selectors of it the non-test files may use: the connection is only a
// net.Conn made by a net.Dialer, the only use of os is os.DirFS, and syscall only names error numbers.
var allowedSelectorsOf = map[string][]string{
	"net":     {"Conn", "Dialer", "Error"},
	"os":      {"DirFS"},
	"syscall": {"EAGAIN", "ECONNREFUSED", "ENOENT", "Errno"},
}

// forbiddenMethod reports the names that give access to descriptors or to the raw socket, whatever their receiver.
func forbiddenMethod(name string) bool {
	return name == "ReadMsgUnix" || name == "SyscallConn" || name == "File" || strings.HasPrefix(name, "ReadFrom")
}

// localNames returns the local name of every import of file that has an entry in allowedSelectorsOf.
func localNames(file *ast.File) (names map[string]string, violations []string) {
	names = map[string]string{}
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if _, restricted := allowedSelectorsOf[path]; err != nil || restricted == false {
			continue
		}
		local := path[strings.LastIndex(path, "/")+1:]
		if imp.Name != nil {
			local = imp.Name.Name
		}
		if local == "." {
			violations = append(violations, "dot import of "+path)
			continue
		}
		names[local] = path
	}
	return names, violations
}

// importViolations returns the imports of file that are not in allowed.
func importViolations(file *ast.File, allowed []string) []string {
	var violations []string
	for _, imp := range file.Imports {
		path, err := strconv.Unquote(imp.Path.Value)
		if err != nil || slices.Contains(allowed, path) == false {
			violations = append(violations, "imports "+imp.Path.Value+", want only "+strings.Join(allowed, " "))
		}
	}
	return violations
}

// selectorViolations returns the forbidden method names and the selectors of net, os and syscall that are not allowed.
func selectorViolations(file *ast.File) []string {
	names, violations := localNames(file)
	ast.Inspect(file, func(n ast.Node) bool {
		sel, ok := n.(*ast.SelectorExpr)
		if ok == false {
			return true
		}
		if forbiddenMethod(sel.Sel.Name) {
			violations = append(violations, sel.Sel.Name+" is not allowed")
		}
		if id, isIdent := sel.X.(*ast.Ident); isIdent {
			path, bound := names[id.Name]
			if bound && id.Obj == nil && slices.Contains(allowedSelectorsOf[path], sel.Sel.Name) == false {
				violations = append(violations, id.Name+"."+sel.Sel.Name+" is not allowed")
			}
		}
		return true
	})
	return violations
}

// dialViolations pins the only dial: a DialContext call (any receiver) inside UnixDialer whose network argument is the
// constant "unix"; any other DialContext or Dial call is a violation.
func dialViolations(file *ast.File) (violations []string, dials int) {
	for _, decl := range file.Decls {
		fn, isFunc := decl.(*ast.FuncDecl)
		if isFunc == false {
			continue
		}
		ast.Inspect(fn, func(n ast.Node) bool {
			call, isCall := n.(*ast.CallExpr)
			if isCall == false {
				return true
			}
			sel, isSel := call.Fun.(*ast.SelectorExpr)
			if isSel == false || strings.HasPrefix(sel.Sel.Name, "Dial") == false && strings.HasPrefix(sel.Sel.Name, "Listen") == false {
				return true
			}
			dials++
			lit, isLit := ast.Expr(nil), false
			if len(call.Args) == 3 {
				lit, isLit = call.Args[1], true
			}
			basic, isBasic := lit.(*ast.BasicLit)
			if sel.Sel.Name != "DialContext" || fn.Name.Name != "UnixDialer" || isLit == false || isBasic == false || basic.Value != `"unix"` {
				violations = append(violations, fn.Name.Name+": "+sel.Sel.Name+" is not the DialContext(ctx, \"unix\", path) of UnixDialer")
			}
			return true
		})
	}
	return violations, dials
}

func TestMariadb_ImportsOnlyAllowedPackages(t *testing.T) {
	files, err := filepath.Glob("*.go")
	if err != nil || len(files) == 0 {
		t.Fatalf("Glob(*.go) = %v, %v, want the package's files", files, err)
	}
	checked, dials := 0, 0
	for _, name := range files {
		if strings.HasSuffix(name, "_test.go") {
			continue
		}
		allowed, known := importsOfFile[name]
		if known == false {
			t.Errorf("%s: no import allow-list; add the file to importsOfFile", name)
			continue
		}
		checked++
		file, err := parser.ParseFile(token.NewFileSet(), name, nil, 0)
		if err != nil {
			t.Fatalf("parsing %s: %v", name, err)
		}
		dialErrors, n := dialViolations(file)
		dials += n
		for _, v := range slices.Concat(importViolations(file, allowed), selectorViolations(file), dialErrors) {
			t.Errorf("%s: %s", name, v)
		}
	}
	if checked != len(importsOfFile) {
		t.Errorf("checked %d production files, want %d", checked, len(importsOfFile))
	}
	if dials != 1 {
		t.Errorf("found %d dial or listen calls, want exactly the one DialContext of UnixDialer", dials)
	}
}
