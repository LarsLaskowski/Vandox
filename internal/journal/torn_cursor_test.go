package journal

import (
	"reflect"
	"testing"
)

// tornOf calls Cursor.Torn by reflection, so that the suite builds before the method exists.
func tornOf(t *testing.T, c *Cursor) bool {
	t.Helper()
	m := reflect.ValueOf(c).MethodByName("Torn")
	if !m.IsValid() || m.Type().NumIn() != 0 || m.Type().NumOut() != 1 || m.Type().Out(0).Kind() != reflect.Bool {
		t.Fatalf("Cursor has no method Torn() bool")
	}
	return m.Call(nil)[0].Bool()
}

// drainToEnd reads the kernel list of b to its end and returns the cursor and the number of entries read.
func drainToEnd(t *testing.T, b []byte, setAside bool) (*Cursor, int) {
	t.Helper()
	f := openBytes(t, b, setAside)
	c, err := f.Seek(kernelList(t, f), 0)
	if err != nil {
		t.Fatalf("Seek() error = %v, want nil", err)
	}
	seen, stop := drain(f, &c)
	if stop != nil {
		t.Fatalf("Next() error = %v, want the list to end without an error", stop)
	}
	return &c, len(seen)
}

func TestFile_TornTailIsEndOfFile_CursorTorn(t *testing.T) {
	cases := []struct {
		name     string
		setAside bool
		into     int
	}{
		{"archived file cut inside an entry", false, 20},
		{"archived file cut between entries", false, 0},
		{"set-aside file cut inside an entry", true, 20},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			o := testOptions()
			o.online = c.setAside
			full, cut, _ := tornFile(10, 7, c.into, o)

			cur, n := drainToEnd(t, cut, c.setAside)

			if n != 7 || !tornOf(t, cur) {
				t.Errorf("cut file: read %d entries, Torn() = %v, want 7 and true", n, tornOf(t, cur))
			}
			whole, nAll := drainToEnd(t, full, c.setAside)
			if nAll != 10 || tornOf(t, whole) {
				t.Errorf("whole file: read %d entries, Torn() = %v, want 10 and false (the normal end of a list)", nAll, tornOf(t, whole))
			}
		})
	}
}
