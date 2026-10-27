package agent

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"syscall"
	"testing"
)

const (
	binaryName    = "vandox-agent-linux-amd64"
	sumsName      = "SHA256SUMS"
	maxSumsBytes  = 4096
	maxBinaryByte = 67108864

	checksumMismatch = "install.sh: checksum mismatch, nothing installed"
	releaseVersion   = "v1.2.3"
)

// fakeAccounts models getent, id, useradd, usermod, groupadd and passwd -S over two text files. It copies the
// output shapes of those tools as measured in this repository's test container (getent passwd and group,
// id -u, -g, -gn, -Gn and the default line, passwd -S "name L|P|NP date ..."), and the option sets the plan's
// ensure_user needs (--system, --gid, --groups, --shell, --no-create-home, --home-dir, --lock, --append).
const fakeAccounts = `set -u
db=$ACCOUNTS_DB
users=$db/users
groups=$db/groups
tool=$1; shift

find_user() { awk -F'|' -v n="$1" '$1==n{print; f=1} END{exit !f}' "$users"; }
group_gid() { awk -F'|' -v n="$1" '$1==n{print $2; f=1} END{exit !f}' "$groups"; }
gid_group() { awk -F'|' -v g="$1" '$2==g{print $1; f=1} END{exit !f}' "$groups"; }
parse() { IFS='|' read -r u_name u_uid u_pg u_supp u_home u_shell u_lock <<<"$1"; }
put_user() {
    awk -F'|' -v n="$1" '$1!=n' "$users" >"$users.new"
    printf '%s|%s|%s|%s|%s|%s|%s\n' "$@" >>"$users.new"
    mv "$users.new" "$users"
}
next_id() { echo $((900 + $(wc -l <"$users") + $(wc -l <"$groups") + 1)); }
resolve_group() {
    if [[ $1 =~ ^[0-9]+$ ]]; then gid_group "$1"; return; fi
    group_gid "$1" >/dev/null && echo "$1"
}
check_groups() {
    local g
    IFS=',' read -ra list <<<"$1"
    for g in "${list[@]}"; do
        [[ -z $g ]] && continue
        group_gid "$g" >/dev/null || { echo "$tool: group '$g' does not exist" >&2; exit 6; }
    done
}

case $tool in
getent)
    case $1 in
    passwd)
        line=$(find_user "${2:-}") || exit 2
        parse "$line"
        echo "$u_name:x:$u_uid:$(group_gid "$u_pg"):$u_name:$u_home:$u_shell"
        ;;
    group)
        gid=$(group_gid "${2:-}") || exit 2
        members=$(awk -F'|' -v g="$2" '{n=split($4,a,","); for(i=1;i<=n;i++) if(a[i]==g) m=m (m==""?"":",") $1} END{print m}' "$users")
        echo "$2:x:$gid:$members"
        ;;
    *) exit 2 ;;
    esac
    ;;
id)
    flags=; name=
    for a in "$@"; do
        case $a in -*) flags+=${a#-} ;; *) name=$a ;; esac
    done
    [[ -n $name ]] || name=root
    line=$(find_user "$name") || { echo "id: '$name': no such user" >&2; exit 1; }
    parse "$line"
    pgid=$(group_gid "$u_pg")
    ids=("$pgid"); names=("$u_pg")
    IFS=',' read -ra sg <<<"$u_supp"
    for g in "${sg[@]}"; do
        [[ -n $g && $g != "$u_pg" ]] || continue
        ids+=("$(group_gid "$g")"); names+=("$g")
    done
    if [[ $flags == *G* ]]; then
        if [[ $flags == *n* ]]; then echo "${names[*]}"; else echo "${ids[*]}"; fi
    elif [[ $flags == *g* ]]; then
        if [[ $flags == *n* ]]; then echo "$u_pg"; else echo "$pgid"; fi
    elif [[ $flags == *u* ]]; then
        if [[ $flags == *n* ]]; then echo "$u_name"; else echo "$u_uid"; fi
    else
        out="uid=$u_uid($u_name) gid=$pgid($u_pg) groups="
        for i in "${!ids[@]}"; do out+="${ids[$i]}(${names[$i]}),"; done
        echo "${out%,}"
    fi
    ;;
groupadd)
    name=
    for a in "$@"; do case $a in -*) ;; *) name=$a ;; esac; done
    group_gid "$name" >/dev/null && { echo "groupadd: group '$name' already exists" >&2; exit 9; }
    echo "$name|$(next_id)" >>"$groups"
    ;;
useradd)
    grp=; supp=; shell=/bin/sh; home=; nogroup=0; name=
    while (($#)); do
        case $1 in
        -g|--gid) grp=$2; shift 2 ;;
        --gid=*) grp=${1#*=}; shift ;;
        -G|--groups) supp=$2; shift 2 ;;
        --groups=*) supp=${1#*=}; shift ;;
        -s|--shell) shell=$2; shift 2 ;;
        --shell=*) shell=${1#*=}; shift ;;
        -d|--home-dir) home=$2; shift 2 ;;
        --home-dir=*) home=${1#*=}; shift ;;
        -c|--comment) shift 2 ;;
        -N|--no-user-group) nogroup=1; shift ;;
        -*) shift ;;
        *) name=$1; shift ;;
        esac
    done
    find_user "$name" >/dev/null && { echo "useradd: user '$name' already exists" >&2; exit 9; }
    if [[ -z $grp ]]; then
        if ((nogroup)); then grp=users; else grp=$name; group_gid "$name" >/dev/null || echo "$name|$(next_id)" >>"$groups"; fi
    fi
    pg=$(resolve_group "$grp") || { echo "useradd: group '$grp' does not exist" >&2; exit 6; }
    check_groups "$supp"
    put_user "$name" "$(next_id)" "$pg" "$supp" "${home:-/home/$name}" "$shell" L
    ;;
usermod)
    shell=; grp=; supp=; haveg=0; append=0; lock=; name=
    while (($#)); do
        case $1 in
        -s|--shell) shell=$2; shift 2 ;;
        --shell=*) shell=${1#*=}; shift ;;
        -g|--gid) grp=$2; shift 2 ;;
        --gid=*) grp=${1#*=}; shift ;;
        -G|--groups) supp=$2; haveg=1; shift 2 ;;
        --groups=*) supp=${1#*=}; haveg=1; shift ;;
        -a|--append) append=1; shift ;;
        -L|--lock) lock=L; shift ;;
        -U|--unlock) lock=P; shift ;;
        -d|--home|-c|--comment) shift 2 ;;
        -*) shift ;;
        *) name=$1; shift ;;
        esac
    done
    line=$(find_user "$name") || { echo "usermod: user '$name' does not exist" >&2; exit 6; }
    parse "$line"
    [[ -n $shell ]] && u_shell=$shell
    [[ -n $lock ]] && u_lock=$lock
    if [[ -n $grp ]]; then u_pg=$(resolve_group "$grp") || { echo "usermod: group '$grp' does not exist" >&2; exit 6; }; fi
    if ((haveg)); then
        check_groups "$supp"
        if ((append)); then u_supp="${u_supp:+$u_supp,}$supp"; else u_supp=$supp; fi
    fi
    put_user "$u_name" "$u_uid" "$u_pg" "$u_supp" "$u_home" "$u_shell" "$u_lock"
    ;;
passwd)
    mode=; name=
    for a in "$@"; do
        case $a in
        -S|--status) mode=S ;;
        -l|--lock) mode=L ;;
        -u|--unlock) mode=P ;;
        -d|--delete) mode=NP ;;
        -*) ;;
        *) name=$a ;;
        esac
    done
    line=$(find_user "$name") || { echo "passwd: user '$name' does not exist" >&2; exit 1; }
    parse "$line"
    if [[ $mode == S ]]; then
        echo "$u_name $u_lock 2026-01-01 0 99999 7 -1"
    else
        put_user "$u_name" "$u_uid" "$u_pg" "$u_supp" "$u_home" "$u_shell" "$mode"
    fi
    ;;
esac
`

// fakeStat answers the owner formats of stat from the owners file (path|user|uid; root for any other path,
// as the script runs as root) and passes every other format to the real stat.
const fakeStat = `fmt=; files=()
while (($#)); do
    case $1 in
    -c) fmt=$2; shift 2 ;;
    --format=*|--printf=*) fmt=${1#*=}; shift ;;
    -L|--dereference) shift ;;
    --) shift; files+=("$@"); break ;;
    *) files+=("$1"); shift ;;
    esac
done
for f in "${files[@]}"; do
    user=root; uid=0
    entry=$(awk -F'|' -v p="$f" '$1==p{print $2 "|" $3}' "$STUB_LOG/owners" 2>/dev/null | tail -n 1)
    if [[ -n $entry ]]; then user=${entry%|*}; uid=${entry#*|}; fi
    f2=${fmt//\%U/$user}; f2=${f2//\%u/$uid}; f2=${f2//\%G/$user}; f2=${f2//\%g/$uid}
    REALSTAT -c "$f2" -- "$f" || exit
done`

// fakeChown records the new owner of every path it is given in the owners file the fake stat reads; it
// never changes a real file (the tests do not run as root).
const fakeChown = `owner=; paths=()
for a in "$@"; do
    case $a in
    -*) ;;
    *) if [[ -z $owner ]]; then owner=$a; else paths+=("$a"); fi ;;
    esac
done
u=${owner%%:*}
case $u in root) uid=0 ;; vandox-agent) uid=998 ;; *) uid=1234 ;; esac
for p in "${paths[@]}"; do printf '%s|%s|%s\n' "$p" "$u" "$uid" >>"$STUB_LOG/owners"; done`

// accounts installs the fake account tools over the given user and group rows.
func (h *harness) accounts(users, groups []string) {
	h.t.Helper()

	dbDir := filepath.Join(h.dir, "accounts")
	if err := os.Mkdir(dbDir, 0o755); err != nil {
		h.t.Fatalf("mkdir accounts: %v", err)
	}

	script := h.writeFile("fakeaccounts.sh", fakeAccounts)

	for name, rows := range map[string][]string{"users": users, "groups": groups} {
		text := strings.Join(rows, "\n")
		if text != "" {
			text += "\n"
		}

		if err := os.WriteFile(filepath.Join(dbDir, name), []byte(text), 0o644); err != nil {
			h.t.Fatalf("write %s: %v", name, err)
		}
	}

	h.setEnv("ACCOUNTS_DB="+dbDir, "FAKE_ACCOUNTS="+script)

	for _, tool := range []string{"getent", "id", "useradd", "usermod", "groupadd", "passwd"} {
		h.stub(tool, `exec /bin/bash "$FAKE_ACCOUNTS" `+tool+` "$@"`)
	}
}

// userRow returns the stored row of a fake user, split into its seven fields.
func (h *harness) userRow(name string) []string {
	h.t.Helper()

	data, err := os.ReadFile(filepath.Join(h.dir, "accounts", "users"))
	if err != nil {
		h.t.Fatalf("read users: %v", err)
	}

	for _, line := range strings.Split(string(data), "\n") {
		fields := strings.Split(line, "|")
		if len(fields) == 7 && fields[0] == name {
			return fields
		}
	}

	return nil
}

// hasGroup reports whether the fake group database holds the group.
func (h *harness) hasGroup(name string) bool {
	h.t.Helper()

	data, err := os.ReadFile(filepath.Join(h.dir, "accounts", "groups"))
	if err != nil {
		h.t.Fatalf("read groups: %v", err)
	}

	return strings.Contains("\n"+string(data), "\n"+name+"|")
}

// owners installs the fake stat and chown, with the given owner rows (path|user|uid).
func (h *harness) owners(rows ...string) {
	h.t.Helper()

	realStat, err := exec.LookPath("stat")
	if err != nil {
		h.t.Fatalf("stat not found: %v", err)
	}

	text := strings.Join(rows, "\n")
	if text != "" {
		text += "\n"
	}

	if err := os.WriteFile(filepath.Join(h.logs, "owners"), []byte(text), 0o644); err != nil {
		h.t.Fatalf("write owners: %v", err)
	}

	h.stub("stat", strings.ReplaceAll(fakeStat, "REALSTAT", shellQuote(realStat)))
	h.stub("chown", fakeChown)
}

// sha256Hex returns the lower-case hex SHA-256 of data.
func sha256Hex(data []byte) string {
	sum := sha256.Sum256(data)

	return hex.EncodeToString(sum[:])
}

// sumsLine is the one well-formed SHA256SUMS line of a binary.
func sumsLine(binary []byte) string {
	return sha256Hex(binary) + "  " + binaryName + "\n"
}

// markerBinary is a stand-in agent: it records that it ran and answers -version with a version line.
func (h *harness) markerBinary(version string) []byte {
	marker := filepath.Join(h.dir, "ran")

	return []byte("#!/bin/bash\nprintf ran >" + shellQuote(marker) + "\n" +
		"if [[ ${1:-} == -version ]]; then echo 'vandox-agent " + version + " (commit abc1234, built 2026-01-01)'; fi\n")
}

// ran reports whether the stand-in agent ran.
func (h *harness) ran() bool {
	_, err := os.Stat(filepath.Join(h.dir, "ran"))

	return err == nil
}

// release writes SHA256SUMS and the binary to a source directory and returns it.
func (h *harness) release(sums string, binary []byte) string {
	h.t.Helper()

	src := filepath.Join(h.dir, "src")
	if err := os.MkdirAll(src, 0o755); err != nil {
		h.t.Fatalf("mkdir src: %v", err)
	}

	h.writeFile("src/"+sumsName, sums)

	if err := os.WriteFile(filepath.Join(src, binaryName), binary, 0o644); err != nil {
		h.t.Fatalf("write binary: %v", err)
	}

	return src
}

// workDir creates the work directory of a download (mode 0711) and returns it.
func (h *harness) workDir() string {
	h.t.Helper()

	work := filepath.Join(h.dir, "work")
	if err := os.Mkdir(work, 0o700); err != nil {
		h.t.Fatalf("mkdir work: %v", err)
	}

	if err := os.Chmod(work, 0o711); err != nil {
		h.t.Fatalf("chmod work: %v", err)
	}

	return work
}

// curlServing installs a curl stub that copies the file named by the last URL segment from src to the -o
// file, and exits 22 for the file named failName.
func (h *harness) curlServing(src, failName string) {
	h.t.Helper()

	h.stub("curl", `out=; url=
while (($#)); do
    case $1 in
    -o|--output) out=$2; shift 2 ;;
    http*) url=$1; shift ;;
    *) shift ;;
    esac
done
name=${url##*/}
if [[ $name == "$CURL_FAIL" ]]; then exit 22; fi
cp -- "$CURL_SRC/$name" "$out"`)
	h.setEnv("CURL_SRC="+src, "CURL_FAIL="+failName)
}

// setprivStub installs a setpriv stand-in that drops its options and runs the command (the tests do not
// run as root).
func (h *harness) setprivStub() {
	h.t.Helper()

	h.stub("setpriv", `while [[ ${1:-} == --* ]]; do shift; done
exec "$@"`)
}

// sparseFile creates a file of the given size without writing it.
func sparseFile(t *testing.T, path string, size int64) {
	t.Helper()

	f, err := os.Create(path)
	if err != nil {
		t.Fatalf("create %s: %v", path, err)
	}

	if err := f.Truncate(size); err != nil {
		t.Fatalf("truncate %s: %v", path, err)
	}

	if err := f.Close(); err != nil {
		t.Fatalf("close %s: %v", path, err)
	}
}

// TestRenderConfig_SetsAgentIDAndURL checks that the two lines of the example are replaced whole and every
// other line is byte-identical, for values with the characters a sed replacement would trip over.
func TestRenderConfig_SetsAgentIDAndURL(t *testing.T) {
	t.Parallel()

	const (
		idLine  = "agent_id: web-1"
		urlLine = "  url: http://100.64.0.1:8081"
	)

	oddExample := "  # leading spaces \\ and a backslash, %s and *\n\n-n\n  trailing  \n# agent_id: web-1\n" +
		idLine + "\nagent_id: web-1 # keep\n" + urlLine + "x\nbackend:\n" + urlLine + "\n" + asciiTab + "tabbed\n"

	shipped, err := os.ReadFile(shippedFile(t, "agent.yaml"))
	if err != nil {
		t.Fatalf("read agent.yaml: %v", err)
	}

	examples := map[string]string{"shipped example": string(shipped), "odd lines": oddExample}
	values := []struct{ id, url string }{
		{"web-1", "http://100.64.0.1:8081"},
		{"a.b_c-d", "http://[fd7a:115c:a1e0::1]:8081/"},
		{"X", "https://backend.tail1234.ts.net/"},
		{"srv.1", "http://h:1/"},
	}

	for exName, example := range examples {
		for _, v := range values {
			t.Run(exName+" "+v.id, func(t *testing.T) {
				t.Parallel()

				h := newHarness(t)
				examplePath := h.writeFile("example.yaml", example)
				out := filepath.Join(h.dir, "out.yaml")

				got := h.run(`render_config "$1" "$2" "$3" "$4"`, "", examplePath, v.id, v.url, out)
				expectResult(t, "render_config", got, 0, "", "")

				want := renderExpected(t, example, v.id, v.url)

				data, err := os.ReadFile(out)
				if err != nil {
					t.Fatalf("render_config wrote no %s: %v", out, err)
				}

				if string(data) != want {
					t.Errorf("rendered config = %q, want %q", data, want)
				}
			})
		}
	}
}

// renderExpected replaces the two lines of the example the way render_config must.
func renderExpected(t *testing.T, example, id, url string) string {
	t.Helper()

	lines := strings.Split(example, "\n")
	idCount, urlCount := 0, 0

	for i, line := range lines {
		switch line {
		case "agent_id: web-1":
			lines[i] = "agent_id: " + id
			idCount++
		case "  url: http://100.64.0.1:8081":
			lines[i] = "  url: " + url
			urlCount++
		}
	}

	if idCount < 1 || urlCount < 1 {
		t.Fatalf("example lacks the lines to replace (agent_id %d, url %d)", idCount, urlCount)
	}

	return strings.Join(lines, "\n")
}

// userCase is one state of the account database before ensure_user and the lines it must print.
type userCase struct {
	name   string
	users  []string
	groups []string
	want   []string // output lines, compared as a set
	home   string   // a path the output must name
}

const (
	groupsBase   = "adm|4\nsystemd-journal|190\nusers|100"
	conformUser  = "vandox-agent|998|vandox-agent|adm,systemd-journal|/nonexistent|/usr/sbin/nologin|L"
	userLinePref = "install.sh: user vandox-agent: "
)

// userCases are the states E13 and S1 distinguish.
func userCases() []userCase {
	base := strings.Split(groupsBase, "\n")
	withGroup := append(slices.Clone(base), "vandox-agent|998")
	user := func(pg, supp, home, shell, lock string) []string {
		return []string{strings.Join([]string{"vandox-agent", "998", pg, supp, home, shell, lock}, "|")}
	}

	return []userCase{
		{
			name: "no user and no group", groups: base,
			want: []string{"install.sh: group vandox-agent created", userLinePref + "created"},
		},
		{name: "user in the S1 state", users: []string{conformUser}, groups: withGroup},
		{
			name:  "user in the S1 state with the groups in another order",
			users: user("vandox-agent", "systemd-journal,adm", "/nonexistent", "/usr/sbin/nologin", "L"), groups: withGroup,
		},
		{
			name:  "login shell",
			users: user("vandox-agent", "adm,systemd-journal", "/nonexistent", "/bin/bash", "L"), groups: withGroup,
			want: []string{userLinePref + "shell set to /usr/sbin/nologin"},
		},
		{
			name:  "password not locked",
			users: user("vandox-agent", "adm,systemd-journal", "/nonexistent", "/usr/sbin/nologin", "P"), groups: withGroup,
			want: []string{userLinePref + "password locked"},
		},
		{
			name:  "empty password",
			users: user("vandox-agent", "adm,systemd-journal", "/nonexistent", "/usr/sbin/nologin", "NP"), groups: withGroup,
			want: []string{userLinePref + "password locked"},
		},
		{
			name:  "another primary group",
			users: user("users", "adm,systemd-journal", "/nonexistent", "/usr/sbin/nologin", "L"), groups: withGroup,
			want: []string{userLinePref + "primary group set to vandox-agent"},
		},
		{
			name:  "a group missing",
			users: user("vandox-agent", "adm", "/nonexistent", "/usr/sbin/nologin", "L"), groups: withGroup,
			want: []string{userLinePref + "groups set to adm,systemd-journal"},
		},
		{
			name:  "an extra group",
			users: user("vandox-agent", "adm,systemd-journal,users", "/nonexistent", "/usr/sbin/nologin", "L"), groups: withGroup,
			want: []string{userLinePref + "groups set to adm,systemd-journal"},
		},
		{
			name:  "no supplementary group",
			users: user("vandox-agent", "", "/nonexistent", "/usr/sbin/nologin", "L"), groups: withGroup,
			want: []string{userLinePref + "groups set to adm,systemd-journal"},
		},
		{
			name:  "user without its group",
			users: user("users", "adm,systemd-journal", "/nonexistent", "/usr/sbin/nologin", "L"), groups: base,
			want: []string{"install.sh: group vandox-agent created", userLinePref + "primary group set to vandox-agent"},
		},
		{
			name:  "every setting wrong",
			users: user("users", "users", "/nonexistent", "/bin/bash", "P"), groups: withGroup,
			want: []string{
				userLinePref + "shell set to /usr/sbin/nologin", userLinePref + "password locked",
				userLinePref + "primary group set to vandox-agent", userLinePref + "groups set to adm,systemd-journal",
			},
		},
		{
			name:  "user with a home directory",
			users: user("vandox-agent", "adm,systemd-journal", "/home/vandox-agent", "/usr/sbin/nologin", "L"), groups: withGroup,
			home: "/home/vandox-agent",
		},
	}
}

// TestEnsureUser_ReportsEachChange runs ensure_user over the states of E13 with the fake account tools and
// compares the printed lines and the resulting account.
func TestEnsureUser_ReportsEachChange(t *testing.T) {
	t.Parallel()

	for _, tc := range userCases() {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.accounts(tc.users, tc.groups)

			got := h.run("ensure_user", "")

			if got.code != 0 || got.stderr != "" {
				t.Errorf("ensure_user: exit status %d, standard error %q, want 0 and empty", got.code, got.stderr)
			}

			assertUserOutput(t, tc, got.stdout)
			assertUserState(t, h, tc)
		})
	}
}

// assertUserOutput compares the lines ensure_user printed with the wanted set; a line naming the home
// directory is required when the case has one and is not compared otherwise.
func assertUserOutput(t *testing.T, tc userCase, stdout string) {
	t.Helper()

	var lines []string
	if stdout != "" {
		lines = strings.Split(strings.TrimSuffix(stdout, "\n"), "\n")
	}

	if tc.home != "" {
		if strings.Contains(stdout, tc.home) == false {
			t.Errorf("ensure_user output %q does not name the home directory %s", stdout, tc.home)
		}

		lines = slices.DeleteFunc(lines, func(l string) bool { return strings.Contains(l, tc.home) })
	}

	slices.Sort(lines)

	want := slices.Clone(tc.want)
	slices.Sort(want)

	if slices.Equal(lines, want) == false {
		t.Errorf("ensure_user printed %q, want %q", lines, want)
	}
}

// assertUserState checks the account after ensure_user: S1's shell, lock, primary and supplementary groups.
func assertUserState(t *testing.T, h *harness, tc userCase) {
	t.Helper()

	row := h.userRow("vandox-agent")
	if row == nil {
		t.Fatalf("user vandox-agent does not exist after ensure_user")
	}

	supp := strings.Split(row[3], ",")
	slices.Sort(supp)

	if row[5] != "/usr/sbin/nologin" || row[6] != "L" || row[2] != "vandox-agent" ||
		slices.Equal(supp, []string{"adm", "systemd-journal"}) == false {
		t.Errorf("account after ensure_user = %q, want shell /usr/sbin/nologin, locked, group vandox-agent, groups adm,systemd-journal", row)
	}

	if h.hasGroup("vandox-agent") == false {
		t.Errorf("group vandox-agent does not exist after ensure_user")
	}

	if tc.home != "" && row[4] != tc.home {
		t.Errorf("home directory after ensure_user = %q, want %q kept", row[4], tc.home)
	}

	if len(tc.want) == 0 && tc.home == "" {
		assertNoAccountChange(t, h)
	}
}

// assertNoAccountChange fails when a tool that changes accounts ran.
func assertNoAccountChange(t *testing.T, h *harness) {
	t.Helper()

	for _, tool := range []string{"useradd", "usermod", "groupadd"} {
		if calls := h.calls(tool); len(calls) != 0 {
			t.Errorf("%s calls = %q, want none for an account that is already right", tool, calls)
		}
	}

	for _, call := range h.calls("passwd") {
		if strings.Contains(call, "[-S]") == false && strings.Contains(call, "[--status]") == false {
			t.Errorf("passwd call %q changes an account that is already right", call)
		}
	}
}

// TestCheckoutWarning_OnlyForAnotherTag checks S3: only a checkout on another tag is warned about.
func TestCheckoutWarning_OnlyForAnotherTag(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name string
		tag  string
		want string
	}{
		{"same tag", "v1.2.3", ""},
		{"other tag", "v1.2.0", "install.sh: WARNING checkout is v1.2.0, not v1.2.3\n"},
		{"release candidate of the version", "v1.2.3-rc.1", "install.sh: WARNING checkout is v1.2.3-rc.1, not v1.2.3\n"},
		{"no tag", "", ""},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(`checkout_warning "$1" "$2"`, "", tc.tag, releaseVersion)

			expectResult(t, "checkout_warning", got, 0, tc.want, "")
		})
	}

	t.Run("git exiting 128 gives no output", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		h.stub("git", `echo "fatal: detected dubious ownership in repository" >&2; exit 128`)

		got := h.run(`checkout_warning "" "$1"`, "", releaseVersion)

		expectResult(t, "checkout_warning with a failing git", got, 0, "", "")
	})
}

// TestNeedsToken_OnlyWithoutTokenFile checks S3: the token is needed exactly when its file is missing.
func TestNeedsToken_OnlyWithoutTokenFile(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name  string
		setup func(t *testing.T, path string)
		want  string
	}{
		{"missing", func(*testing.T, string) {}, "0"},
		{"present", func(t *testing.T, path string) { writeBytes(t, path, "secret\n") }, "1"},
		{"present and empty", func(t *testing.T, path string) { writeBytes(t, path, "") }, "1"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			path := filepath.Join(h.dir, "agent-token")
			tc.setup(t, path)

			got := h.run(`rc=0; needs_token "$1" || rc=$?; echo "$rc"`, "", path)

			expectResult(t, "needs_token", got, 0, tc.want+"\n", "")
		})
	}
}

// writeBytes writes a file with the given content.
func writeBytes(t *testing.T, path, content string) {
	t.Helper()

	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("write %s: %v", path, err)
	}
}

// TestDropInWarning_ForEachDropInDirectory checks that each existing drop-in directory is named once.
func TestDropInWarning_ForEachDropInDirectory(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name  string
		first bool
		other bool
	}{
		{"neither", false, false},
		{"first", true, false},
		{"second", false, true},
		{"both", true, true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			first := filepath.Join(h.dir, "etc.d")
			other := filepath.Join(h.dir, "run.d")

			var want strings.Builder

			for _, d := range []struct {
				path    string
				present bool
			}{{first, tc.first}, {other, tc.other}} {
				if d.present == false {
					continue
				}

				if err := os.Mkdir(d.path, 0o755); err != nil {
					t.Fatalf("mkdir %s: %v", d.path, err)
				}

				want.WriteString("install.sh: WARNING drop-in directory " + d.path + " changes the unit; check it\n")
			}

			got := h.run(`dropin_warning "$1" "$2"`, "", first, other)

			expectResult(t, "dropin_warning", got, 0, want.String(), "")
		})
	}
}

// TestVerifyChecksum_AcceptsOnlyTheSingleMatchingLine checks E2: exactly one well-formed line for the binary.
func TestVerifyChecksum_AcceptsOnlyTheSingleMatchingLine(t *testing.T) {
	t.Parallel()

	binary := []byte("the binary\n")
	good := sumsLine(binary)
	other := sha256Hex([]byte("another binary"))

	cases := []struct {
		name string
		sums *string
		ok   bool
	}{
		{"match", &good, true},
		{"other hash", ptr(other + "  " + binaryName + "\n"), false},
		{"two lines", ptr(good + good), false},
		{"match and a line of another file", ptr(good + other + "  other-file\n"), false},
		{"other name", ptr(sha256Hex(binary) + "  vandox-agent-linux-arm64\n"), false},
		{"upper-case hex", ptr(strings.ToUpper(sha256Hex(binary)) + "  " + binaryName + "\n"), false},
		{"one space", ptr(sha256Hex(binary) + " " + binaryName + "\n"), false},
		{"binary-mode marker", ptr(sha256Hex(binary) + " *" + binaryName + "\n"), false},
		{"trailing text", ptr(sha256Hex(binary) + "  " + binaryName + " extra\n"), false},
		{"leading space", ptr(" " + good), false},
		{"carriage return", ptr(sha256Hex(binary) + "  " + binaryName + asciiCR + "\n"), false},
		{"63 hex digits", ptr(sha256Hex(binary)[1:] + "  " + binaryName + "\n"), false},
		{"empty", ptr(""), false},
		{"missing file", nil, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.passthrough("sha256sum")

			work := h.workDir()
			writeBytes(t, filepath.Join(work, binaryName), string(binary))

			if tc.sums != nil {
				writeBytes(t, filepath.Join(work, sumsName), *tc.sums)
			}

			got := h.run(`verify_checksum "$1"`, "", work)

			if tc.ok {
				expectResult(t, "verify_checksum", got, 0, "", "")

				return
			}

			expectResult(t, "verify_checksum", got, 1, "", checksumMismatch+"\n")

			if mode := fileMode(t, filepath.Join(work, binaryName)); mode&0o111 != 0 {
				t.Errorf("binary mode after a refused checksum = %o, want it not executable", mode)
			}
		})
	}
}

// ptr returns a pointer to s.
func ptr(s string) *string {
	return &s
}

// TestVerifyChecksum_LeavesTheBinaryExecutableByOthers checks that a match makes the binary 0755 and keeps
// the work directory 0711, so the unprivileged version check can run it.
func TestVerifyChecksum_LeavesTheBinaryExecutableByOthers(t *testing.T) {
	t.Parallel()

	h := newHarness(t)
	h.passthrough("sha256sum")

	binary := []byte("the binary\n")
	work := h.workDir()

	if err := os.WriteFile(filepath.Join(work, binaryName), binary, 0o600); err != nil {
		t.Fatalf("write binary: %v", err)
	}

	writeBytes(t, filepath.Join(work, sumsName), sumsLine(binary))

	got := h.run(`verify_checksum "$1"`, "", work)
	expectResult(t, "verify_checksum", got, 0, "", "")

	if mode := fileMode(t, filepath.Join(work, binaryName)); mode != 0o755 {
		t.Errorf("binary mode after a match = %o, want 755", mode)
	}

	if mode := fileMode(t, work); mode != 0o711 {
		t.Errorf("work directory mode after a match = %o, want 711", mode)
	}
}

// neverRunsCase is one SHA256SUMS content for the chain of fetch, verify and version check.
type neverRunsCase struct {
	name  string
	sums  func(binary []byte) string
	match bool
}

// TestFetchRelease_NeverRunsABinaryThatFailsTheChecksum runs fetch, verify and version check in turn, from
// a directory and from a download: a binary that does not match is never run, a matching one is (the control).
func TestFetchRelease_NeverRunsABinaryThatFailsTheChecksum(t *testing.T) {
	t.Parallel()

	cases := []neverRunsCase{
		{"matching checksum (control)", sumsLine, true},
		{"other hash", func([]byte) string { return sha256Hex([]byte("x")) + "  " + binaryName + "\n" }, false},
		{"two lines", func(b []byte) string { return sumsLine(b) + sumsLine(b) }, false},
	}

	for _, source := range []string{"from", "download"} {
		for _, tc := range cases {
			t.Run(source+" "+tc.name, func(t *testing.T) {
				t.Parallel()

				runNeverRuns(t, source, tc)
			})
		}
	}
}

// runNeverRuns runs the chain for one case.
func runNeverRuns(t *testing.T, source string, tc neverRunsCase) {
	t.Helper()

	h := newHarness(t)
	h.passthrough("sha256sum")
	h.setprivStub()

	binary := h.markerBinary(releaseVersion)
	src := h.release(tc.sums(binary), binary)
	work := h.workDir()
	from := ""

	if source == "from" {
		from = src
	} else {
		h.curlServing(src, "")
	}

	got := h.run(`fetch_release "$1" "$2" "$3"; verify_checksum "$3"; check_binary_version "$3/`+binaryName+`" "$1"`,
		"", releaseVersion, from, work)

	if tc.match {
		expectResult(t, "fetch, verify and check", got, 0, "", "")

		if h.ran() == false {
			t.Errorf("the verified binary was never run by the version check")
		}

		return
	}

	expectResult(t, "fetch, verify and check", got, 1, "", checksumMismatch+"\n")

	if h.ran() {
		t.Errorf("a binary that fails the checksum was run")
	}
}

// TestFetchRelease_ReportsAMissingFile checks E3: a failed download names the file, exit 1.
func TestFetchRelease_ReportsAMissingFile(t *testing.T) {
	t.Parallel()

	for _, name := range []string{sumsName, binaryName} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)

			binary := []byte("b")
			src := h.release(sumsLine(binary), binary)
			work := h.workDir()
			h.curlServing(src, name)

			got := h.run(`fetch_release "$1" "" "$2"`, "", releaseVersion, work)

			expectResult(t, "fetch_release", got, 1, "", "install.sh: download failed: "+name+"\n")
		})
	}
}

// TestDownload_UsesHTTPSOnly checks the arguments of both curl calls (plan.md, Approach, install.sh order 5).
func TestDownload_UsesHTTPSOnly(t *testing.T) {
	t.Parallel()

	h := newHarness(t)

	binary := []byte("b")
	src := h.release(sumsLine(binary), binary)
	work := h.workDir()
	h.curlServing(src, "")

	got := h.run(`fetch_release "$1" "" "$2"`, "", releaseVersion, work)
	expectResult(t, "fetch_release", got, 0, "", "")

	calls := h.calls("curl")
	if len(calls) != 2 {
		t.Fatalf("curl calls = %q, want one for %s and one for %s", calls, sumsName, binaryName)
	}

	base := "https://github.com/LarsLaskowski/Vandox/releases/download/" + releaseVersion + "/"

	for _, file := range []struct {
		name  string
		limit string
	}{{sumsName, "4096"}, {binaryName, "67108864"}} {
		call := findCall(calls, base+file.name+"]")
		if call == "" {
			t.Errorf("no curl call for %s%s in %q", base, file.name, calls)

			continue
		}

		for _, want := range []string{
			"[--proto][=https]", "[--proto-redir][=https]", "[--fail]", "[--location]", "[--silent]", "[--show-error]",
			"[--max-time][300]", "[--max-filesize][" + file.limit + "]", "[-o][" + filepath.Join(work, file.name) + "]",
		} {
			if strings.Contains(call, want) == false {
				t.Errorf("curl call for %s = %s, want it to hold %s", file.name, call, want)
			}
		}

		if strings.Count(call, "[http") != 1 {
			t.Errorf("curl call for %s = %s, want exactly one URL", file.name, call)
		}
	}
}

// findCall returns the recorded call that ends with suffix.
func findCall(calls []string, suffix string) string {
	for _, c := range calls {
		if strings.HasSuffix(c, suffix) {
			return c
		}
	}

	return ""
}

// TestVerifyChecksum_RefusesOversizeOrNonRegularFiles checks the bounds and the link checks of
// verify_checksum on both paths (fact 18: a curl that ignores --max-filesize must not matter).
func TestVerifyChecksum_RefusesOversizeOrNonRegularFiles(t *testing.T) {
	t.Parallel()

	t.Run("download of an oversize SHA256SUMS", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		h.passthrough("sha256sum")

		binary := []byte("b")
		sums := sumsLine(binary) + strings.Repeat("#", maxSumsBytes+1-len(sumsLine(binary)))
		src := h.release(sums, binary)
		h.curlServing(src, "")

		got := h.run(`fetch_release "$1" "" "$2"; verify_checksum "$2"`, "", releaseVersion, h.workDir())

		expectResult(t, "download then verify_checksum", got, 1, "", checksumMismatch+"\n")
		assertNotHashed(t, h)
	})

	t.Run("download of an oversize binary", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		h.passthrough("sha256sum")

		src := h.release(sumsLine([]byte("b")), []byte("b"))
		sparseFile(t, filepath.Join(src, binaryName), maxBinaryByte+1)
		h.curlServing(src, "")

		got := h.run(`fetch_release "$1" "" "$2"; verify_checksum "$2"`, "", releaseVersion, h.workDir())

		expectResult(t, "download then verify_checksum", got, 1, "", checksumMismatch+"\n")
		assertNotHashed(t, h)
	})

	for _, tc := range []struct{ name, file string }{{"oversize SHA256SUMS", sumsName}, {"oversize binary", binaryName}} {
		t.Run("--from with an "+tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.passthrough("sha256sum")

			binary := []byte("b")
			src := h.release(sumsLine(binary), binary)
			size := int64(maxBinaryByte + 1)

			if tc.file == sumsName {
				size = maxSumsBytes + 1
			}

			sparseFile(t, filepath.Join(src, tc.file), size)

			got := h.run(`fetch_release "$1" "$2" "$3"; verify_checksum "$3"`, "", releaseVersion, src, h.workDir())

			if got.code != 1 || got.stdout != "" ||
				(got.stderr != checksumMismatch+"\n" && got.stderr != "install.sh: download failed: "+tc.file+"\n") {
				t.Errorf("fetch_release and verify_checksum = (%d, %q, %q), want exit 1 and the E2 line or the E3 line of %s",
					got.code, got.stdout, got.stderr, tc.file)
			}

			assertNotHashed(t, h)
		})
	}

	verifyDirect(t)
}

// assertNotHashed fails when sha256sum ran.
func assertNotHashed(t *testing.T, h *harness) {
	t.Helper()

	if calls := h.calls("sha256sum"); len(calls) != 0 {
		t.Errorf("sha256sum ran %d times (%q), want it never run on a file that is refused first", len(calls), calls)
	}
}

// verifyDirect runs verify_checksum on prepared work directories: oversize, links, directories and the bound.
func verifyDirect(t *testing.T) {
	t.Helper()

	type workSetup func(t *testing.T, work string)

	valid := func(t *testing.T, work string) {
		t.Helper()

		binary := []byte("b")
		writeBytes(t, filepath.Join(work, binaryName), string(binary))
		writeBytes(t, filepath.Join(work, sumsName), sumsLine(binary))
	}

	replace := func(name string, prepare func(t *testing.T, path, work string)) workSetup {
		return func(t *testing.T, work string) {
			t.Helper()
			valid(t, work)

			path := filepath.Join(work, name)
			if err := os.Remove(path); err != nil {
				t.Fatalf("remove %s: %v", path, err)
			}

			prepare(t, path, work)
		}
	}

	linkTo := func(t *testing.T, path, work string) {
		t.Helper()

		target := filepath.Join(filepath.Dir(work), "linktarget-"+filepath.Base(path))
		writeBytes(t, target, "b")

		if err := os.Symlink(target, path); err != nil {
			t.Fatalf("symlink %s: %v", path, err)
		}
	}

	dirAt := func(t *testing.T, path, _ string) {
		t.Helper()

		if err := os.Mkdir(path, 0o755); err != nil {
			t.Fatalf("mkdir %s: %v", path, err)
		}
	}

	cases := []struct {
		name  string
		setup workSetup
	}{
		{"SHA256SUMS of 4097 bytes", replace(sumsName, func(t *testing.T, path, _ string) {
			t.Helper()
			writeBytes(t, path, sumsLine([]byte("b"))+strings.Repeat("#", maxSumsBytes+1-len(sumsLine([]byte("b")))))
		})},
		{"binary of 67108865 bytes", replace(binaryName, func(t *testing.T, path, _ string) {
			t.Helper()
			sparseFile(t, path, maxBinaryByte+1)
		})},
		{"SHA256SUMS a link", replace(sumsName, func(t *testing.T, path, work string) {
			t.Helper()
			linkTo(t, path, work)
		})},
		{"binary a link", replace(binaryName, func(t *testing.T, path, work string) {
			t.Helper()
			linkTo(t, path, work)
		})},
		{"SHA256SUMS a directory", replace(sumsName, dirAt)},
		{"binary a directory", replace(binaryName, dirAt)},
	}

	for _, tc := range cases {
		t.Run("work directory with "+tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.passthrough("sha256sum")

			work := h.workDir()
			tc.setup(t, work)

			got := h.run(`verify_checksum "$1"`, "", work)

			expectResult(t, "verify_checksum", got, 1, "", checksumMismatch+"\n")
			assertNotHashed(t, h)
		})
	}

	t.Run("binary of exactly 67108864 bytes passes the bound", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		h.passthrough("sha256sum")

		work := h.workDir()
		sparseFile(t, filepath.Join(work, binaryName), maxBinaryByte)

		hash := sha256.New()
		chunk := make([]byte, 1<<20)

		for range maxBinaryByte >> 20 {
			hash.Write(chunk)
		}

		writeBytes(t, filepath.Join(work, sumsName), hex.EncodeToString(hash.Sum(nil))+"  "+binaryName+"\n")

		got := h.run(`verify_checksum "$1"`, "", work)

		expectResult(t, "verify_checksum", got, 0, "", "")

		if len(h.calls("sha256sum")) == 0 {
			t.Errorf("sha256sum never ran for a binary at the bound")
		}
	})
}

// badSources makes one kind of unusable source file at path.
var badSources = map[string]func(t *testing.T, h *harness, path string){
	"link": func(t *testing.T, h *harness, path string) {
		t.Helper()

		target := filepath.Join(h.dir, "valid-target")
		writeBytes(t, target, "b")

		if err := os.Symlink(target, path); err != nil {
			t.Fatalf("symlink: %v", err)
		}
	},
	"directory": func(t *testing.T, _ *harness, path string) {
		t.Helper()

		if err := os.Mkdir(path, 0o755); err != nil {
			t.Fatalf("mkdir: %v", err)
		}
	},
	"FIFO": func(t *testing.T, _ *harness, path string) {
		t.Helper()

		if err := syscall.Mkfifo(path, 0o600); err != nil {
			t.Fatalf("mkfifo: %v", err)
		}
	},
	"missing": func(*testing.T, *harness, string) {},
}

// TestFetchRelease_RefusesALinkOrNonRegularSourceInFrom checks E3 for --from: a source that is a link, a
// directory, a FIFO or missing stops the script with the line of that file and copies nothing.
func TestFetchRelease_RefusesALinkOrNonRegularSourceInFrom(t *testing.T) {
	t.Parallel()

	for _, file := range []string{sumsName, binaryName} {
		for kind, prepare := range badSources {
			t.Run(file+" "+kind, func(t *testing.T) {
				t.Parallel()

				runBadSource(t, file, prepare)
			})
		}
	}
}

// runBadSource replaces one source file by an unusable one and runs fetch_release.
func runBadSource(t *testing.T, file string, prepare func(t *testing.T, h *harness, path string)) {
	t.Helper()

	h := newHarness(t)
	h.passthrough("sha256sum")

	binary := []byte("b")
	src := h.release(sumsLine(binary), binary)
	bad := filepath.Join(src, file)

	if err := os.Remove(bad); err != nil {
		t.Fatalf("remove %s: %v", bad, err)
	}

	prepare(t, h, bad)

	work := h.workDir()
	got := h.run(`fetch_release "$1" "$2" "$3"`, "", releaseVersion, src, work)

	expectResult(t, "fetch_release", got, 1, "", "install.sh: download failed: "+file+"\n")

	entries, err := os.ReadDir(work)
	if err != nil {
		t.Fatalf("read work directory: %v", err)
	}

	if len(entries) != 0 {
		t.Errorf("work directory holds %d entries after a refused source, want nothing copied", len(entries))
	}
}

// TestCheckBinaryVersion_RefusesAnotherVersion checks E3: the verified binary must name the version, run
// unprivileged with setpriv.
func TestCheckBinaryVersion_RefusesAnotherVersion(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name   string
		output string
		exit   int
		ok     bool
	}{
		{"the version", "vandox-agent v1.2.3 (commit abc1234, built 2026-01-01)", 0, true},
		{"another patch version", "vandox-agent v1.2.4 (commit abc1234, built 2026-01-01)", 0, false},
		{"a longer version with the same prefix", "vandox-agent v1.2.30 (commit abc1234, built 2026-01-01)", 0, false},
		{"a release candidate", "vandox-agent v1.2.3-rc.1 (commit abc1234, built 2026-01-01)", 0, false},
		{"no version line", "something else", 0, false},
		{"empty output", "", 0, false},
		{"the version but a failing binary", "vandox-agent v1.2.3 (commit abc1234, built 2026-01-01)", 3, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.setprivStub()

			bin := filepath.Join(h.dir, "agent")
			script := "#!/bin/bash\nif [[ ${1:-} == -version ]]; then echo " + shellQuote(tc.output) + "; exit " + fmt.Sprint(tc.exit) + "; fi\nexit 9\n"

			if err := os.WriteFile(bin, []byte(script), 0o755); err != nil {
				t.Fatalf("write binary: %v", err)
			}

			got := h.run(`check_binary_version "$1" "$2"`, "", bin, releaseVersion)

			if tc.ok {
				expectResult(t, "check_binary_version", got, 0, "", "")
			} else {
				expectResult(t, "check_binary_version", got, 1, "", "install.sh: binary is not "+releaseVersion+"\n")
			}

			want := "[--reuid=nobody][--regid=nogroup][--clear-groups][--no-new-privs][" + bin + "][-version]"
			if calls := h.calls("setpriv"); len(calls) != 1 || calls[0] != want {
				t.Errorf("setpriv calls = %q, want exactly %s", calls, want)
			}
		})
	}
}

// usageCase is one argument list of parse_args and what it must print.
type usageCase struct {
	name  string
	args  []string
	lines []string // lines standard error must hold, in this order
}

// TestInstall_UsageErrorsExitTwo checks E4 and E6 for the argument parser and the three value validators.
func TestInstall_UsageErrorsExitTwo(t *testing.T) {
	t.Parallel()

	t.Run("parse_args", func(t *testing.T) { t.Parallel(); runParseArgsCases(t) })
	t.Run("validate_version", func(t *testing.T) { t.Parallel(); runVersionCases(t) })
	t.Run("validate_agent_id", func(t *testing.T) { t.Parallel(); runAgentIDCases(t) })
	t.Run("validate_backend_url", func(t *testing.T) { t.Parallel(); runBackendURLCases(t) })
}

// runParseArgsCases checks refused argument lists and the globals of an accepted one.
func runParseArgsCases(t *testing.T) {
	t.Helper()

	tokenLine := "install.sh: the token is read from standard input, never from an option"
	agentLine := "install.sh: --agent-id is not a valid agent ID"
	urlLine := "install.sh: --backend-url is not http(s)://host[:port]"

	cases := []usageCase{
		{"no arguments", nil, []string{usageLine}},
		{"no version", []string{"--agent-id", "web-1"}, []string{usageLine}},
		{"version without a value", []string{"--version"}, []string{usageLine}},
		{"version without the patch", []string{"--version", "v1.2"}, []string{usageLine}},
		{"version without the v", []string{"--version", "1.2.3"}, []string{usageLine}},
		{"version with a suffix other than rc", []string{"--version", "v1.2.3-beta"}, []string{usageLine}},
		{"release candidate without a number", []string{"--version", "v1.2.3-rc"}, []string{usageLine}},
		{"version with a trailing newline", []string{"--version", "v1.2.3\n"}, []string{usageLine}},
		{"token as an option", []string{"--version", "v1.2.3", "--token", "x"}, []string{tokenLine, usageLine}},
		{"token as an option with equals", []string{"--version", "v1.2.3", "--token=x"}, []string{tokenLine, usageLine}},
		{"token option without a value", []string{"--version", "v1.2.3", "--token"}, []string{tokenLine, usageLine}},
		{"positional argument", []string{"--version", "v1.2.3", "extra"}, []string{usageLine}},
		{"unknown option", []string{"--version", "v1.2.3", "--bogus"}, []string{usageLine}},
		{"from without a value", []string{"--version", "v1.2.3", "--from"}, []string{usageLine}},
		{"agent-id without a value", []string{"--version", "v1.2.3", "--agent-id"}, []string{usageLine}},
		{"backend-url without a value", []string{"--version", "v1.2.3", "--backend-url"}, []string{usageLine}},
		{"invalid agent ID", []string{"--version", "v1.2.3", "--agent-id", "a b", "--backend-url", "http://h"}, []string{agentLine}},
		{"invalid backend URL", []string{"--version", "v1.2.3", "--agent-id", "web-1", "--backend-url", "ftp://h"}, []string{urlLine}},
	}

	script := `parse_args "$@"; [[ -z "${AGENT_ID:-}" ]] || validate_agent_id "$AGENT_ID"; ` +
		`[[ -z "${BACKEND_URL:-}" ]] || validate_backend_url "$BACKEND_URL"`

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(script, "", tc.args...)

			if got.code != 2 || got.stdout != "" {
				t.Errorf("parse_args %q: exit status %d, standard output %q, want 2 and empty (standard error %q)", tc.args, got.code, got.stdout, got.stderr)
			}

			assertLinesInOrder(t, got.stderr, tc.lines)
		})
	}

	t.Run("accepted arguments set the globals", func(t *testing.T) { t.Parallel(); runParseArgsAccepted(t) })
}

// assertLinesInOrder checks that every wanted line occurs as a whole line of text, in order.
func assertLinesInOrder(t *testing.T, text string, want []string) {
	t.Helper()

	lines := strings.Split(text, "\n")
	pos := 0

	for _, w := range want {
		idx := slices.Index(lines[pos:], w)
		if idx < 0 {
			t.Errorf("standard error %q lacks the line %q (in order)", text, w)

			return
		}

		pos += idx + 1
	}
}

// runParseArgsAccepted checks the globals an accepted argument list sets.
func runParseArgsAccepted(t *testing.T) {
	t.Helper()

	show := `parse_args "$@"; printf '%s|%s|%s|%s\n' "$VERSION" "${AGENT_ID:-}" "${BACKEND_URL:-}" "${FROM_DIR:-}"; printf '%s\n' "${NO_MARIADB:-}" >&2`

	h := newHarness(t)

	got := h.run(show, "", "--version", "v1.2.3-rc.1", "--agent-id", "web-1", "--backend-url", "http://100.64.0.1:8081", "--from", "/some/dir")

	if got.code != 0 || got.stdout != "v1.2.3-rc.1|web-1|http://100.64.0.1:8081|/some/dir\n" {
		t.Errorf("parse_args globals = (%d, %q, stderr %q), want 0 and the given values", got.code, got.stdout, got.stderr)
	}

	without := h.run(show, "", "--version", "v10.20.30")
	if without.code != 0 || without.stdout != "v10.20.30|||\n" {
		t.Errorf("parse_args of the version alone = (%d, %q, stderr %q), want 0 and v10.20.30 with empty others", without.code, without.stdout, without.stderr)
	}

	with := h.run(show, "", "--version", "v10.20.30", "--no-mariadb")
	if with.code != 0 || with.stderr == without.stderr {
		t.Errorf("--no-mariadb left NO_MARIADB = %q (exit %d), want a value other than the default %q", with.stderr, with.code, without.stderr)
	}
}

// runVersionCases checks validate_version directly.
func runVersionCases(t *testing.T) {
	t.Helper()

	for _, v := range []string{"v1.2.3", "v0.0.1", "v10.20.30", "v1.2.3-rc.1", "v1.2.3-rc.12"} {
		h := newHarness(t)
		expectResult(t, "validate_version "+v, h.run(`validate_version "$1"`, "", v), 0, "", "")
	}

	for _, v := range []string{"", "v1.2", "1.2.3", "v1.2.3-beta", "v1.2.3-rc", "v1.2.3-rc.", "v1.2.3.4", "vx.y.z", "v1.2.3 ", " v1.2.3", "v1.2.3\n", "v1.2.3-rc.1\n", "v1.2.3/../x"} {
		h := newHarness(t)

		got := h.run(`validate_version "$1"`, "", v)
		if got.code != 2 || got.stdout != "" {
			t.Errorf("validate_version %q: exit status %d, standard output %q, want 2 and empty", v, got.code, got.stdout)
		}
	}
}

// runAgentIDCases checks validate_agent_id.
func runAgentIDCases(t *testing.T) {
	t.Helper()

	for _, v := range []string{"web-1", "a", "1web", "A.b_c-d", strings.Repeat("a", 64)} {
		h := newHarness(t)
		expectResult(t, "validate_agent_id "+v, h.run(`validate_agent_id "$1"`, "", v), 0, "", "")
	}

	refused := []string{"", "-a", ".a", "_a", strings.Repeat("a", 65), "a b", "a:b", "a#b", "a\nb", "web-1\n", "a/b", "a'b", "a\"b", "a$b", "é1"}
	for _, v := range refused {
		h := newHarness(t)

		got := h.run(`validate_agent_id "$1"`, "", v)
		expectResult(t, fmt.Sprintf("validate_agent_id %q", v), got, 2, "", "install.sh: --agent-id is not a valid agent ID\n")
	}
}

// runBackendURLCases checks validate_backend_url.
func runBackendURLCases(t *testing.T) {
	t.Helper()

	label63 := strings.Repeat("a", 63)
	name253 := strings.Join([]string{label63, label63, label63, strings.Repeat("a", 61)}, ".")
	name254 := strings.Join([]string{label63, label63, label63, strings.Repeat("a", 62)}, ".")

	accepted := []string{
		"http://100.64.0.1:8081", "https://backend.tail1234.ts.net", "http://[fd7a:115c:a1e0::1]:8081/", "http://h",
		"http://h:65535", "https://h/", "http://a.b-c.d", "http://1.2.3.4", "http://" + label63, "http://" + name253,
		"http://[::1]:8081",
	}
	refused := []string{
		"ftp://h", "http://h/x", "http://u@h", "http://h:0", "http://h:65536", `"http://h"`, "http://h #", "http://h ", " http://h",
		"http://h\n", "http://h_x", "http://[fd7a::1", "http://1.2.3.4.5", "", "http://", "http://:8081", "http://h:", "http://h:08081",
		"http://256.1.1.1", "http://-h", "http://h-", "http://h..x", "http://[g::1]", "http://[1]", "http://" + label63 + "a",
		"http://" + name254, "http://h?x=1", "http://h#x", "http://h'x", "http://h:80/\n", "HTTP2://h", "http://h:8081//",
	}

	for _, v := range accepted {
		h := newHarness(t)
		expectResult(t, "validate_backend_url "+v, h.run(`validate_backend_url "$1"`, "", v), 0, "", "")
	}

	for _, v := range refused {
		h := newHarness(t)

		got := h.run(`validate_backend_url "$1"`, "", v)
		expectResult(t, fmt.Sprintf("validate_backend_url %q", v), got, 2, "", "install.sh: --backend-url is not http(s)://host[:port]\n")
	}
}

// TestValidateToken_EnforcesTheSecretRules checks E5: 32 to 4096 bytes of 0x21 to 0x7E, in the C locale,
// with the rule named and the token never echoed.
func TestValidateToken_EnforcesTheSecretRules(t *testing.T) {
	t.Parallel()

	tok := func(n int) string { return strings.Repeat("x", n) }
	rule := func(r string) string { return "install.sh: token rejected: " + r + "\n" }

	const (
		fewer   = "fewer than 32 characters"
		more    = "more than 4096 characters"
		outside = "a character outside 0x21-0x7E"
	)

	cases := []struct {
		name  string
		token string
		want  string // empty for accepted
	}{
		{"31 bytes", tok(31), fewer},
		{"32 bytes", tok(32), ""},
		{"4096 bytes", tok(4096), ""},
		{"4097 bytes", tok(4097), more},
		{"empty", "", fewer},
		{"every printable character", "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~", ""},
		{"backslashes", strings.Repeat(asciiBackslash, 32), ""},
		{"leading space", " " + tok(32), outside},
		{"trailing space", tok(32) + " ", outside},
		{"space inside", tok(16) + " " + tok(16), outside},
		{"tab inside", tok(16) + asciiTab + tok(16), outside},
		{"newline inside", tok(16) + "\n" + tok(16), outside},
		{"DEL", tok(16) + string(rune(0x7f)) + tok(16), outside},
		{"UTF-8 letter", tok(32) + string([]byte{0xc3, 0xa9}), outside},
		{"escape character", tok(32) + asciiESC, outside},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.setEnv("CANDIDATE=" + tc.token)

			got := h.run(`validate_token "$CANDIDATE"`, "")

			if tc.want == "" {
				expectResult(t, "validate_token", got, 0, "", "")

				return
			}

			expectResult(t, "validate_token", got, 1, "", rule(tc.want))
		})
	}
}

// TestReadToken_KeepsEveryByteOfTheLine checks that read_token takes exactly one line of standard input
// with its spaces and backslashes, so the secret rules see them.
func TestReadToken_KeepsEveryByteOfTheLine(t *testing.T) {
	t.Parallel()

	long := strings.Repeat("a", 32)

	cases := []struct {
		name  string
		input string
		want  string
	}{
		{"plain line", long + "\n", long},
		{"leading and trailing space", " " + long + " \n", " " + long + " "},
		{"backslashes", long + asciiBackslash + "n" + asciiBackslash + "\n", long + asciiBackslash + "n" + asciiBackslash},
		{"only the first line", long + "\nsecond\n", long},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(`read_token; printf '[%s]' "$TOKEN" >"$1"`, tc.input, filepath.Join(h.dir, "token.out"))

			if got.code != 0 {
				t.Fatalf("read_token: exit status %d (stderr %q)", got.code, got.stderr)
			}

			data, err := os.ReadFile(filepath.Join(h.dir, "token.out"))
			if err != nil {
				t.Fatalf("read_token set no TOKEN: %v", err)
			}

			if string(data) != "["+tc.want+"]" {
				t.Errorf("TOKEN after read_token = %q, want [%s]", data, tc.want)
			}

			if strings.Contains(got.stdout+got.stderr, long) {
				t.Errorf("read_token echoed the token: stdout %q, stderr %q", got.stdout, got.stderr)
			}
		})
	}
}

// TestTokenPath_NeverReachesAChildProcess runs read_token, validate_token and write_token with every
// external command a stub that records its arguments and environment: the token is in none of them, nor in
// the output, and the file holds the token and a newline with mode 0600.
func TestTokenPath_NeverReachesAChildProcess(t *testing.T) {
	t.Parallel()

	const token = "Zq7!Kp3#Wm9$Xr5%Tv2&Yb8*Nd4(Lc6)Hj1"

	h := newHarness(t)

	for _, name := range []string{"mktemp", "mv", "chmod"} {
		h.passthrough(name)
	}

	for _, name := range []string{
		"awk", "basename", "cat", "chown", "cp", "cut", "date", "dirname", "env", "find", "grep", "head", "ln", "ls", "mkdir",
		"od", "readlink", "realpath", "rm", "sed", "sleep", "sort", "tail", "tee", "touch", "tr", "truncate", "uniq", "wc",
		"install", "id", "getent", "sha256sum", "curl", "git", "stat", "xxd", "base64", "printf", "echo", "tput", "stty",
	} {
		h.stub(name, "exit 0")
	}

	targetDir := filepath.Join(h.dir, "etc-vandox")
	if err := os.Mkdir(targetDir, 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	target := filepath.Join(targetDir, "agent-token")

	script := "PATH=" + shellQuote(h.stubs) + `; umask 077; read_token && validate_token "$TOKEN" && write_token "$1"`
	got := h.run(script, token+"\n", target)

	if got.code != 0 {
		t.Fatalf("token path: exit status %d (stdout %q, stderr %q)", got.code, got.stdout, got.stderr)
	}

	if strings.Contains(got.stdout+got.stderr, token) {
		t.Errorf("the token reached the output: stdout %q, stderr %q", got.stdout, got.stderr)
	}

	if logs := h.allLogs(); strings.Contains(logs, token) {
		t.Errorf("the token reached a child process (arguments or environment):\n%s", logs)
	}

	data, err := os.ReadFile(target)
	if err != nil {
		t.Fatalf("write_token wrote no %s: %v", target, err)
	}

	if string(data) != token+"\n" {
		t.Errorf("token file = %q, want the token and one newline", data)
	}

	if mode := fileMode(t, target); mode != 0o600 {
		t.Errorf("token file mode = %o, want 600", mode)
	}

	entries, err := os.ReadDir(targetDir)
	if err != nil || len(entries) != 1 {
		t.Errorf("directory of the token holds %d entries (%v), want only the token file", len(entries), err)
	}
}

// spoolTree is one state of the spool directory tree for ensure_spool_directory.
type spoolTree struct {
	name   string
	setup  func(t *testing.T, h *harness, parent, spool string)
	refuse string // "parent", "spool" or "" for accepted
}

// TestEnsureSpoolDirectory_RefusesALinkOrAnotherOwner checks E6 and the creation rules, without root: the
// fake stat reports the owner (root unless the owners file says otherwise) and the fake chown records it.
func TestEnsureSpoolDirectory_RefusesALinkOrAnotherOwner(t *testing.T) {
	t.Parallel()

	mkdir := func(t *testing.T, path string, mode os.FileMode) {
		t.Helper()

		if err := os.Mkdir(path, 0o700); err != nil {
			t.Fatalf("mkdir %s: %v", path, err)
		}

		if err := os.Chmod(path, mode); err != nil {
			t.Fatalf("chmod %s: %v", path, err)
		}
	}

	owner := func(h *harness, path, who string, uid int) {
		appendOwner(h, path, who, uid)
	}

	cases := []spoolTree{
		{"parent and spool missing", func(*testing.T, *harness, string, string) {}, ""},
		{"parent right, spool missing", func(t *testing.T, _ *harness, parent, _ string) { mkdir(t, parent, 0o755) }, ""},
		{"both right", func(t *testing.T, h *harness, parent, spool string) {
			mkdir(t, parent, 0o755)
			mkdir(t, spool, 0o700)
			owner(h, spool, "vandox-agent", 998)
		}, ""},
		{"spool a link", func(t *testing.T, h *harness, parent, spool string) {
			mkdir(t, parent, 0o755)
			mkdir(t, filepath.Join(h.dir, "elsewhere"), 0o700)
			owner(h, spool, "vandox-agent", 998)

			if err := os.Symlink(filepath.Join(h.dir, "elsewhere"), spool); err != nil {
				t.Fatalf("symlink: %v", err)
			}
		}, "spool"},
		{"spool owned by another user", func(t *testing.T, h *harness, parent, spool string) {
			mkdir(t, parent, 0o755)
			mkdir(t, spool, 0o700)
			owner(h, spool, "mallory", 1234)
		}, "spool"},
		{"spool owned by root", func(t *testing.T, _ *harness, parent, spool string) {
			mkdir(t, parent, 0o755)
			mkdir(t, spool, 0o700)
		}, "spool"},
		{"spool a file", func(t *testing.T, _ *harness, parent, spool string) {
			mkdir(t, parent, 0o755)
			writeBytes(t, spool, "x")
		}, "spool"},
		{"parent a link", func(t *testing.T, h *harness, parent, _ string) {
			mkdir(t, filepath.Join(h.dir, "realparent"), 0o755)

			if err := os.Symlink(filepath.Join(h.dir, "realparent"), parent); err != nil {
				t.Fatalf("symlink: %v", err)
			}
		}, "parent"},
		{"parent owned by another user", func(t *testing.T, h *harness, parent, _ string) {
			mkdir(t, parent, 0o755)
			owner(h, parent, "mallory", 1234)
		}, "parent"},
		{"parent owned by the agent", func(t *testing.T, h *harness, parent, _ string) {
			mkdir(t, parent, 0o755)
			owner(h, parent, "vandox-agent", 998)
		}, "parent"},
		{"parent group-writable", func(t *testing.T, _ *harness, parent, _ string) { mkdir(t, parent, 0o775) }, "parent"},
		{"parent other-writable", func(t *testing.T, _ *harness, parent, _ string) { mkdir(t, parent, 0o757) }, "parent"},
		{"parent world-writable", func(t *testing.T, _ *harness, parent, _ string) { mkdir(t, parent, 0o777) }, "parent"},
		{"parent a file", func(t *testing.T, _ *harness, parent, _ string) { writeBytes(t, parent, "x") }, "parent"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			h.accounts([]string{conformUser}, append(strings.Split(groupsBase, "\n"), "vandox-agent|998"))
			h.owners()

			parent := filepath.Join(h.dir, "vandox")
			spool := filepath.Join(parent, "spool")
			tc.setup(t, h, parent, spool)

			got := h.run(`ensure_spool_directory "$1"`, "", spool)

			assertSpoolTree(t, h, tc, got, parent, spool)
		})
	}
}

// appendOwner adds an owner row to the owners file of the fake stat.
func appendOwner(h *harness, path, who string, uid int) {
	h.t.Helper()

	f, err := os.OpenFile(filepath.Join(h.logs, "owners"), os.O_APPEND|os.O_WRONLY|os.O_CREATE, 0o644)
	if err != nil {
		h.t.Fatalf("open owners: %v", err)
	}

	if _, err := fmt.Fprintf(f, "%s|%s|%d\n", path, who, uid); err != nil {
		h.t.Fatalf("append owner: %v", err)
	}

	if err := f.Close(); err != nil {
		h.t.Fatalf("close owners: %v", err)
	}
}

// assertSpoolTree checks the answer of ensure_spool_directory for one tree.
func assertSpoolTree(t *testing.T, h *harness, tc spoolTree, got shellResult, parent, spool string) {
	t.Helper()

	if tc.refuse != "" {
		path := spool
		if tc.refuse == "parent" {
			path = parent
		}

		expectResult(t, "ensure_spool_directory", got, 1, "", "install.sh: "+path+" is not the agent's directory\n")

		return
	}

	expectResult(t, "ensure_spool_directory", got, 0, "", "")

	if mode := fileMode(t, parent); mode != 0o755 {
		t.Errorf("parent mode = %o, want 755", mode)
	}

	if mode := fileMode(t, spool); mode != 0o700 {
		t.Errorf("spool mode = %o, want 700", mode)
	}

	for _, call := range h.calls("chown") {
		if strings.Contains(call, "[-R]") || strings.Contains(call, "[--recursive]") {
			t.Errorf("chown call %q recurses, want a single entry", call)
		}
	}

	if tc.name != "both right" {
		want := "[-h][vandox-agent:vandox-agent][" + spool + "]"
		if calls := h.calls("chown"); len(calls) != 1 || calls[0] != want {
			t.Errorf("chown calls = %q, want exactly %s", calls, want)
		}
	}
}

// TestTailnetWarning_OnlyForLiteralsOutsideTheRanges checks E6: only an IP literal outside 100.64.0.0/10 and
// fd7a:115c:a1e0::/48 is warned about; a name is not.
func TestTailnetWarning_OnlyForLiteralsOutsideTheRanges(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name string
		url  string
		host string // empty for no warning
	}{
		{"start of the IPv4 range", "http://100.64.0.0:8081", ""},
		{"end of the IPv4 range", "http://100.127.255.255", ""},
		{"above the IPv4 range", "http://100.128.0.1", "100.128.0.1"},
		{"below the IPv4 range", "http://100.63.255.255/", "100.63.255.255"},
		{"private address", "http://10.0.0.1:8081", "10.0.0.1"},
		{"IPv6 in the tailnet range", "http://[fd7a:115c:a1e0::1]:8081/", ""},
		{"IPv6 outside the tailnet range", "http://[fd7a:115c:a1e1::1]", "fd7a:115c:a1e1::1"},
		{"a name", "https://backend.tail1234.ts.net", ""},
		{"a name that looks like a range", "http://100.64.example.com", ""},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(`tailnet_warning "$1"`, "", tc.url)

			if tc.host == "" {
				expectResult(t, "tailnet_warning", got, 0, "", "")

				return
			}

			re := regexp.MustCompile(`^install\.sh: WARNING \[?` + regexp.QuoteMeta(tc.host) + `\]? is not a tailnet address; the unit blocks it\n$`)
			if got.code != 0 || re.MatchString(got.stdout) == false || got.stderr != "" {
				t.Errorf("tailnet_warning(%s) = (%d, %q, %q), want exit 0 and the warning for %s", tc.url, got.code, got.stdout, got.stderr, tc.host)
			}
		})
	}
}

// TestNameServerWarning_ForServersOutsideLocalhostAndTheTailnet checks E12 and N10: only a name host is
// checked, only a nameserver value that is an IP literal is printed, anything else is skipped.
func TestNameServerWarning_ForServersOutsideLocalhostAndTheTailnet(t *testing.T) {
	t.Parallel()

	const (
		nameURL = "https://backend.tail1234.ts.net"
		ipURL   = "http://100.64.0.1:8081"
	)

	warn := func(ip string) string {
		return "install.sh: WARNING name server " + ip + " is blocked by the unit; use the tailnet address or MagicDNS\n"
	}

	cases := []struct {
		name   string
		url    string
		resolv string
		want   string
	}{
		{"systemd-resolved stub", nameURL, "nameserver 127.0.0.53\n", ""},
		{"tailnet resolver", nameURL, "nameserver 100.100.100.100\n", ""},
		{"IPv6 loopback", nameURL, "nameserver ::1\n", ""},
		{"IPv6 tailnet resolver", nameURL, "nameserver fd7a:115c:a1e0::53\n", ""},
		{"public resolver", nameURL, "nameserver 1.1.1.1\n", warn("1.1.1.1")},
		{"above the tailnet range", nameURL, "nameserver 100.128.0.1\n", warn("100.128.0.1")},
		{"below the tailnet range", nameURL, "nameserver 100.63.255.255\n", warn("100.63.255.255")},
		{"an IP host", ipURL, "nameserver 1.1.1.1\n", ""},
		{"a server followed by a junk suffix", nameURL, "nameserver 1.1.1.1;x\n", ""},
		{"a command substitution", nameURL, "nameserver $(id)\n", ""},
		{"an escape sequence", nameURL, "nameserver " + asciiESC + "[31m\n", ""},
		{"an octet out of range", nameURL, "nameserver 999.1.1.1\n", ""},
		{"a link-local server with a zone", nameURL, "nameserver fe80::1%eth0\n", warn("fe80::1")},
		{
			"several lines", nameURL,
			"# nameserver 9.9.9.9\nsearch example.com\nnameserver 127.0.0.53\nnameserver 8.8.8.8\nnameserver 1.1.1.1\n",
			warn("8.8.8.8") + warn("1.1.1.1"),
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			resolv := h.writeFile("resolv.conf", tc.resolv)

			got := h.run(`name_server_warning "$1" "$2"`, "", tc.url, resolv)

			expectResult(t, "name_server_warning", got, 0, tc.want, "")
		})
	}

	t.Run("no resolv.conf", func(t *testing.T) {
		t.Parallel()

		h := newHarness(t)
		got := h.run(`name_server_warning "$1" "$2"`, "", nameURL, filepath.Join(h.dir, "missing"))

		expectResult(t, "name_server_warning", got, 0, "", "")
	})
}

// currentGroup returns the name of the test process's group.
func currentGroup(t *testing.T) string {
	t.Helper()

	u, err := user.Current()
	if err != nil {
		t.Skipf("no current user: %v", err)
	}

	g, err := user.LookupGroupId(u.Gid)
	if err != nil {
		t.Skipf("no group name for gid %s: %v", u.Gid, err)
	}

	return g.Name
}

// journalTree creates a journal directory with one file below a machine directory and one directly in it,
// both with the given mode, and returns the journal directory.
func journalTree(t *testing.T, h *harness, mode os.FileMode) string {
	t.Helper()

	journal := filepath.Join(h.dir, "journal")
	machine := filepath.Join(journal, "0123456789abcdef0123456789abcdef")

	if err := os.MkdirAll(machine, 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	for _, f := range []string{filepath.Join(machine, "system.journal"), filepath.Join(journal, "system.journal")} {
		writeBytes(t, f, "LPKSHHRH")

		if err := os.Chmod(f, mode); err != nil {
			t.Fatalf("chmod %s: %v", f, err)
		}
	}

	return journal
}

// TestCheckJournalGroup_ReportsOkAndWarning checks AC10: the journal files must be group-readable by the
// named group. Each file is placed one level below the journal directory (as /var/log/journal/<id>/) and
// directly in it, so either layout of the check sees it.
func TestCheckJournalGroup_ReportsOkAndWarning(t *testing.T) {
	t.Parallel()

	const warning = "check journal-group: WARNING journal files not readable by group systemd-journal; see deploy/agent/README.md\n"

	cases := []struct {
		name  string
		mode  os.FileMode
		group string // "" for the group of the process
		ok    bool
	}{
		{"group-readable", 0o640, "", true},
		{"group-readable and writable", 0o660, "", true},
		{"world-readable", 0o644, "", true},
		{"owner only", 0o600, "", false},
		{"readable by others but not by the group", 0o604, "", false},
		{"another group", 0o640, "no-such-group-xyz", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)

			group := tc.group
			if group == "" {
				group = currentGroup(t)
			}

			got := h.run(`check_journal_group "$1" "$2"`, "", journalTree(t, h, tc.mode), group)

			if tc.ok {
				expectResult(t, "check_journal_group", got, 0, "check journal-group: ok\n", "")

				return
			}

			expectResult(t, "check_journal_group", got, 0, warning, "")
		})
	}
}

// TestCheckJournalClock_WarnsFromOneSecond checks AC10: a difference below one second between /proc/uptime
// and the monotonic timestamp of a journal entry is ok, one second or more warns (either sign).
func TestCheckJournalClock_WarnsFromOneSecond(t *testing.T) {
	t.Parallel()

	const uptimeMicros = 100370000 // "100.37" seconds

	cases := []struct {
		name  string
		delta int64
		ms    int64 // difference in ms for the warning, 0 for ok
	}{
		{"equal", 0, 0},
		{"999,999 microseconds later", 999999, 0},
		{"999,999 microseconds earlier", -999999, 0},
		{"1,000,000 microseconds later", 1000000, 1000},
		{"1,000,000 microseconds earlier", -1000000, 1000},
		{"2.5 seconds later", 2500000, 2500},
		{"2.5 seconds earlier", -2500000, 2500},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run(`check_journal_clock "$1" "$2"`, "", "100.37", fmt.Sprint(uptimeMicros+tc.delta))

			if tc.ms == 0 {
				expectResult(t, "check_journal_clock", got, 0, "check journal-clock: ok\n", "")

				return
			}

			want := fmt.Sprintf("check journal-clock: WARNING /proc/uptime and the journal differ by %d ms; live kills get no extra snapshots\n", tc.ms)
			expectResult(t, "check_journal_clock", got, 0, want, "")
		})
	}
}

// TestCheckIPCFilter_ReportsOkAndWarning checks AC10: the listing of @ipc must hold process_madvise as a
// syscall line, not as part of a name or a comment.
func TestCheckIPCFilter_ReportsOkAndWarning(t *testing.T) {
	t.Parallel()

	const warning = "check ipc-filter: WARNING systemd does not list process_madvise in @ipc\n"

	listing := func(extra ...string) string {
		lines := append([]string{"@ipc", "    # SysV IPC, POSIX Message Queues or other IPC", "    ipc", "    memfd_create", "    mq_open", "    msgctl", "    pipe"}, extra...)

		return strings.Join(lines, "\n") + "\n"
	}

	cases := []struct {
		name  string
		input string
		ok    bool
	}{
		{"listed", listing("    process_madvise", "    semctl"), true},
		{"listed last without indentation", listing("process_madvise"), true},
		{"not listed", listing("    semctl"), false},
		{"a longer name", listing("    process_madvise2"), false},
		{"a longer name with a prefix", listing("    xprocess_madvise"), false},
		{"only in a comment", listing("    # process_madvise"), false},
		{"empty", "", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			got := h.run("check_ipc_filter", tc.input)

			if tc.ok {
				expectResult(t, "check_ipc_filter", got, 0, "check ipc-filter: ok\n", "")

				return
			}

			expectResult(t, "check_ipc_filter", got, 0, warning, "")
		})
	}
}

// TestCheckCapabilities_ReportsOkAndWarning checks AC10: CapEff must be CAP_SYS_PTRACE (bit 19) alone.
func TestCheckCapabilities_ReportsOkAndWarning(t *testing.T) {
	t.Parallel()

	status := func(eff string) string {
		return strings.Join([]string{
			"Name:" + asciiTab + "vandox-agent", "CapInh:" + asciiTab + "0000000000000000", "CapPrm:" + asciiTab + "0000000000080000",
			"CapEff:" + asciiTab + eff, "CapBnd:" + asciiTab + "0000000000080000", "CapAmb:" + asciiTab + "0000000000080000", "",
		}, "\n")
	}

	cases := []struct {
		name string
		eff  string
		ok   bool
	}{
		{"CAP_SYS_PTRACE alone", "0000000000080000", true},
		{"none", "0000000000000000", false},
		{"CAP_SYS_PTRACE and CAP_SYS_ADMIN", "0000000000280000", false},
		{"another capability", "0000000000040000", false},
		{"all", "000001ffffffffff", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := newHarness(t)
			path := h.writeFile("status", status(tc.eff))

			got := h.run(`check_capabilities "$1"`, "", path)

			if tc.ok {
				expectResult(t, "check_capabilities", got, 0, "check capabilities: ok\n", "")

				return
			}

			want := "check capabilities: WARNING CapEff is " + tc.eff + ", not CAP_SYS_PTRACE alone\n"
			expectResult(t, "check_capabilities", got, 0, want, "")
		})
	}
}
