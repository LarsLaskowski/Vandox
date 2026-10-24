package procstats

import (
	"maps"
	"strconv"
	"strings"
	"testing"
	"testing/fstest"
	"time"
	"unicode/utf8"
)

func TestReadPasswd(t *testing.T) {
	tests := []struct {
		name string
		data string
		want map[uint32]string
		fail bool
	}{
		{
			name: "captured file",
			data: readEtcFixture(t, "passwd"),
			want: map[uint32]string{
				0: "root", 1: "daemon", 2: "bin", 3: "sys", 4: "sync", 5: "games", 6: "man", 7: "lp", 8: "mail", 9: "news", 10: "uucp",
				13: "proxy", 33: "www-data", 34: "backup", 38: "list", 39: "irc", 42: "_apt", 65534: "nobody",
			},
		},
		{"duplicate UID, the first line wins", "a:x:5:5::/:/bin/sh\nb:x:5:5::/:/bin/sh\n", map[uint32]string{5: "a"}, false},
		{"three fields are enough", "a:x:7", map[uint32]string{7: "a"}, false},
		{"two fields are skipped", "a:x\nb:x:8:8\n", map[uint32]string{8: "b"}, false},
		{"empty name is skipped", ":x:8:8\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"UID that is not a number is skipped", "a:x:abc:1\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"empty UID is skipped", "a:x::1\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"UID with a sign is skipped", "a:x:+5:1\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"UID of 11 digits is skipped", "a:x:12345678901:1\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"UID of 10 digits within 32 bits", "a:x:4294967295:1\n", map[uint32]string{4294967295: "a"}, false},
		{"UID of 10 digits beyond 32 bits is skipped", "a:x:4294967296:1\nb:x:9:9\n", map[uint32]string{9: "b"}, false},
		{"empty file", "", map[uint32]string{}, false},
		{"empty lines", "\n\na:x:1:1\n\n", map[uint32]string{1: "a"}, false},
		{"name of 300 bytes is cut to 256", strings.Repeat("n", 300) + ":x:3:3\n", map[uint32]string{3: strings.Repeat("n", 256)}, false},
		{"line of 4096 bytes", "a:x:4:" + strings.Repeat("g", 4096-len("a:x:4:")) + "\n", map[uint32]string{4: "a"}, false},
		{"line of 4097 bytes", "a:x:4:" + strings.Repeat("g", 4097-len("a:x:4:")) + "\nb:x:9:9\n", nil, true},
		{"file larger than 1 MiB", strings.Repeat("a:x:1:1::/:/bin/sh\n", (1<<20)/19+10), nil, true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			etc := fstest.MapFS{"passwd": &fstest.MapFile{Data: []byte(tt.data)}}

			got, err := ReadUsers(etc)

			if (err != nil) != tt.fail {
				t.Fatalf("ReadUsers() error = %v, want an error: %v", err, tt.fail)
			}
			if !tt.fail && !maps.Equal(got, tt.want) {
				t.Errorf("ReadUsers() = %v, want %v", got, tt.want)
			}
		})
	}
}

func TestReadPasswd_FileMissing(t *testing.T) {
	if _, err := ReadUsers(fstest.MapFS{}); err == nil {
		t.Errorf("ReadUsers(empty /etc) error = nil, want an error")
	}
}

func TestReadPasswd_FileOfExactlyOneMiB(t *testing.T) {
	data := strings.Repeat("a:x:1:1::/:/bin/sh\n", 100) + strings.Repeat(strings.Repeat("#", 1023)+"\n", 1024)
	data = data[:1<<20]
	if len(data) != 1<<20 {
		t.Fatalf("test setup: %d bytes, want exactly 1 MiB", len(data))
	}

	got, err := ReadUsers(fstest.MapFS{"passwd": &fstest.MapFile{Data: []byte(data)}})

	if err != nil || got[1] != "a" {
		t.Errorf("ReadUsers(1 MiB) = %v, %v, want UID 1 named a and no error", got[1], err)
	}
}

func TestReadPasswd_InvalidUTF8InANameIsReplaced(t *testing.T) {
	got, err := ReadUsers(fstest.MapFS{"passwd": &fstest.MapFile{Data: []byte("a\xffb:x:6:6\n" + strings.Repeat("\xff", 300) + ":x:7:7\n")}})

	if err != nil {
		t.Fatalf("ReadUsers() error = %v, want nil", err)
	}
	if got[6] != "a\uFFFDb" {
		t.Errorf("name of UID 6 = %q, want %q", got[6], "a\uFFFDb")
	}
	if n := got[7]; n == "" || len(n) > 256 || !utf8.ValidString(n) {
		t.Errorf("name of UID 7 = %d bytes, valid UTF-8 %v, want 1 to 256 bytes of valid UTF-8", len(n), utf8.ValidString(n))
	}
}

func TestReadUsers_GivesTheProcessSnapshotsNames(t *testing.T) {
	tr := newTree(t)
	users, err := ReadUsers(tr.etc)
	if err != nil {
		t.Fatalf("ReadUsers() error = %v, want nil", err)
	}
	c, _ := newCollector(tr, nil)

	snap := snapshotOf(t, c, time.Unix(fixtureBtime+100, 0), false)

	for pid, uid := range map[int32]uint32{pidFpmMaster: 0, pidFpmChild: 33} {
		want := UserName(users, uid)
		if p := processOf(snap, pid); p == nil || p.User != want || want == strconv.FormatUint(uint64(uid), 10) {
			t.Errorf("process %d user = %+v, want %q, the name ReadUsers and UserName give for UID %d", pid, p, want, uid)
		}
	}
}

func TestUserName_DecimalForAnUnknownUID(t *testing.T) {
	users := map[uint32]string{0: "root"}
	tests := []struct {
		name  string
		users map[uint32]string
		uid   uint32
		want  string
	}{
		{"known UID", users, 0, "root"},
		{"unknown UID", users, 1000, "1000"},
		{"largest UID", users, 4294967295, "4294967295"},
		{"nil map", nil, 0, "0"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := UserName(tt.users, tt.uid); got != tt.want {
				t.Errorf("UserName(%v, %d) = %q, want %q", tt.users, tt.uid, got, tt.want)
			}
		})
	}
}
