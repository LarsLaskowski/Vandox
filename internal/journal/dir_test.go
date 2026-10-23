package journal

import (
	"slices"
	"strconv"
	"testing"
	"testing/fstest"
)

func TestListAllFiles_TakesSystemAndUserFiles(t *testing.T) {
	file := &fstest.MapFile{Data: []byte("x")}
	fsys := fstest.MapFS{
		// the system files ListFiles takes
		"system.journal":        file,
		"system@a-1-2.journal":  file,
		"system@a-1-2.journal~": file,
		// the user files: one to ten digits, then .journal, @<id>.journal or @<id>.journal~
		"user-1000.journal":                     file,
		"user-1.journal":                        file,
		"user-4294967295.journal":               file,
		"user-1000@b-1-2.journal":               file,
		"user-1000@b-1-2.journal~":              file,
		"user-10000@0000000000000000-1.journal": file,
		// names that are neither
		"user-.journal":             file,
		"user-12345678901.journal":  file,
		"user-1a.journal":           file,
		"user-a.journal":            file,
		"user-1000.journal~":        file,
		"user-1000.txt":             file,
		"user-1000.journal.bak":     file,
		"user-1000@b-1-2.journal.x": file,
		"users-1000.journal":        file,
		"User-1000.journal":         file,
		"user1000.journal":          file,
		"user-1000":                 file,
		"system.journal~":           file,
		"sub/user-1000.journal":     file,
		"sub/system.journal":        file,
		"user-1001.journal/x":       file, // a directory with a journal name
	}

	got, err := ListAllFiles(fsys)

	want := []string{
		"system.journal", "system@a-1-2.journal", "system@a-1-2.journal~",
		"user-1.journal", "user-1000.journal", "user-1000@b-1-2.journal", "user-1000@b-1-2.journal~",
		"user-10000@0000000000000000-1.journal", "user-4294967295.journal",
	}
	slices.Sort(want)
	if err != nil || !slices.Equal(got, want) {
		t.Errorf("ListAllFiles() = %v, %v, want %v sorted and no error", got, err, want)
	}
	system, err := ListFiles(fsys)
	if err != nil || !slices.Equal(system, []string{"system.journal", "system@a-1-2.journal", "system@a-1-2.journal~"}) {
		t.Errorf("ListFiles() = %v, %v, want only the system files, as before", system, err)
	}
}

func TestListAllFiles_ReadsAtMost1024FilesInChunks(t *testing.T) {
	fsys := fstest.MapFS{}
	for i := 0; i < 600; i++ {
		fsys["system@"+strconv.Itoa(1000+i)+".journal"] = &fstest.MapFile{Data: []byte("x")}
		fsys["user-"+strconv.Itoa(1000+i)+".journal"] = &fstest.MapFile{Data: []byte("x")}
	}

	got, err := ListAllFiles(fsys)

	if err != nil || len(got) != 1024 {
		t.Errorf("ListAllFiles() = %d files, %v, want 1024 and no error", len(got), err)
	}
	if !slices.IsSorted(got) {
		t.Error("ListAllFiles() is not sorted, want sorted names")
	}
}
