package agent

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"testing"
	"time"
)

// unitFile is the unit under test, relative to this package's directory.
const unitFile = "vandox-agent.service"

// directive is one assignment of a unit file.
type directive struct {
	section string
	key     string
	value   string
	line    int
}

// systemd249Keys lists, per section, every key systemd tag v249 knows. It is read from
// src/core/load-fragment-gperf.gperf.in of that tag (the Unit keys, and the Service keys with the
// EXEC_CONTEXT, CGROUP_CONTEXT and KILL_CONTEXT macros expanded for Service) and, for [Install], from
// src/shared/install.c (Alias, WantedBy, RequiredBy, DefaultInstance, Also). Ubuntu 22.04 ships systemd 249.
// Service has StartLimitInterval but not StartLimitIntervalSec: that key belongs to [Unit] in 249.
var systemd249Keys = map[string][]string{
	"Install": {"Alias", "WantedBy", "RequiredBy", "DefaultInstance", "Also"},
	"Unit": {
		"After", "AllowIsolate", "AssertACPower", "AssertArchitecture", "AssertCPUs", "AssertCapability",
		"AssertControlGroupController", "AssertDirectoryNotEmpty", "AssertEnvironment", "AssertFileIsExecutable",
		"AssertFileNotEmpty", "AssertFirstBoot", "AssertGroup", "AssertHost", "AssertKernelCommandLine",
		"AssertKernelVersion", "AssertMemory", "AssertNeedsUpdate", "AssertOSRelease", "AssertPathExists",
		"AssertPathExistsGlob", "AssertPathIsDirectory", "AssertPathIsEncrypted", "AssertPathIsMountPoint",
		"AssertPathIsReadWrite", "AssertPathIsSymbolicLink", "AssertSecurity", "AssertUser", "AssertVirtualization",
		"Before", "BindTo", "BindsTo", "CollectMode", "ConditionACPower", "ConditionArchitecture", "ConditionCPUs",
		"ConditionCapability", "ConditionControlGroupController", "ConditionDirectoryNotEmpty", "ConditionEnvironment",
		"ConditionFileIsExecutable", "ConditionFileNotEmpty", "ConditionFirmware", "ConditionFirstBoot", "ConditionGroup",
		"ConditionHost", "ConditionKernelCommandLine", "ConditionKernelVersion", "ConditionMemory", "ConditionNeedsUpdate",
		"ConditionOSRelease", "ConditionPathExists", "ConditionPathExistsGlob", "ConditionPathIsDirectory",
		"ConditionPathIsEncrypted", "ConditionPathIsMountPoint", "ConditionPathIsReadWrite", "ConditionPathIsSymbolicLink",
		"ConditionSecurity", "ConditionUser", "ConditionVirtualization", "Conflicts", "DefaultDependencies", "Description",
		"Documentation", "FailureAction", "FailureActionExitStatus", "IgnoreOnIsolate", "IgnoreOnSnapshot",
		"JobRunningTimeoutSec", "JobTimeoutAction", "JobTimeoutRebootArgument", "JobTimeoutSec", "JoinsNamespaceOf",
		"OnFailure", "OnFailureIsolate", "OnFailureJobMode", "OnSuccess", "OnSuccessJobMode", "PartOf",
		"PropagateReloadFrom", "PropagateReloadTo", "PropagatesReloadTo", "PropagatesStopTo", "RebootArgument",
		"RefuseManualStart", "RefuseManualStop", "ReloadPropagatedFrom", "Requires", "RequiresMountsFor",
		"RequiresOverridable", "Requisite", "RequisiteOverridable", "SourcePath", "StartLimitAction", "StartLimitBurst",
		"StartLimitInterval", "StartLimitIntervalSec", "StopPropagatedFrom", "StopWhenUnneeded", "SuccessAction",
		"SuccessActionExitStatus", "Upholds", "Wants",
	},
	"Service": {
		"AllowedCPUs", "AllowedMemoryNodes", "AmbientCapabilities", "AppArmorProfile", "BPFProgram", "BindPaths",
		"BindReadOnlyPaths", "BlockIOAccounting", "BlockIODeviceWeight", "BlockIOReadBandwidth", "BlockIOWeight",
		"BlockIOWriteBandwidth", "BusName", "BusPolicy", "CPUAccounting", "CPUAffinity", "CPUQuota", "CPUQuotaPeriodSec",
		"CPUSchedulingPolicy", "CPUSchedulingPriority", "CPUSchedulingResetOnFork", "CPUShares", "CPUWeight",
		"CacheDirectory", "CacheDirectoryMode", "Capabilities", "CapabilityBoundingSet", "ConfigurationDirectory",
		"ConfigurationDirectoryMode", "CoredumpFilter", "DefaultMemoryLow", "DefaultMemoryMin", "Delegate", "DeviceAllow",
		"DevicePolicy", "DisableControllers", "DynamicUser", "Environment", "EnvironmentFile", "ExecCondition",
		"ExecPaths", "ExecReload", "ExecStart", "ExecStartPost", "ExecStartPre", "ExecStop", "ExecStopPost",
		"ExtensionImages", "FailureAction", "FileDescriptorStoreMax", "FinalKillSignal", "Group", "GuessMainPID",
		"IOAccounting", "IODeviceLatencyTargetSec", "IODeviceWeight", "IOReadBandwidthMax", "IOReadIOPSMax",
		"IOSchedulingClass", "IOSchedulingPriority", "IOWeight", "IOWriteBandwidthMax", "IOWriteIOPSMax", "IPAccounting",
		"IPAddressAllow", "IPAddressDeny", "IPCNamespacePath", "IPEgressFilterPath", "IPIngressFilterPath",
		"IgnoreSIGPIPE", "InaccessibleDirectories", "InaccessiblePaths", "KeyringMode", "KillMode", "KillSignal",
		"LimitAS", "LimitCORE", "LimitCPU", "LimitDATA", "LimitFSIZE", "LimitLOCKS", "LimitMEMLOCK", "LimitMSGQUEUE",
		"LimitNICE", "LimitNOFILE", "LimitNPROC", "LimitRSS", "LimitRTPRIO", "LimitRTTIME", "LimitSIGPENDING",
		"LimitSTACK", "LoadCredential", "LockPersonality", "LogExtraFields", "LogLevelMax", "LogNamespace",
		"LogRateLimitBurst", "LogRateLimitIntervalSec", "LogsDirectory", "LogsDirectoryMode", "ManagedOOMMemoryPressure",
		"ManagedOOMMemoryPressureLimit", "ManagedOOMPreference", "ManagedOOMSwap", "MemoryAccounting",
		"MemoryDenyWriteExecute", "MemoryHigh", "MemoryLimit", "MemoryLow", "MemoryMax", "MemoryMin", "MemorySwapMax",
		"MountAPIVFS", "MountFlags", "MountImages", "NUMAMask", "NUMAPolicy", "NetClass", "NetworkNamespacePath", "Nice",
		"NoExecPaths", "NoNewPrivileges", "NonBlocking", "NotifyAccess", "OOMPolicy", "OOMScoreAdjust", "PAMName",
		"PIDFile", "PassEnvironment", "PermissionsStartOnly", "Personality", "PrivateDevices", "PrivateIPC",
		"PrivateMounts", "PrivateNetwork", "PrivateTmp", "PrivateUsers", "ProcSubset", "ProtectClock",
		"ProtectControlGroups", "ProtectHome", "ProtectHostname", "ProtectKernelLogs", "ProtectKernelModules",
		"ProtectKernelTunables", "ProtectProc", "ProtectSystem", "ReadOnlyDirectories", "ReadOnlyPaths",
		"ReadWriteDirectories", "ReadWritePaths", "RebootArgument", "RemainAfterExit", "RemoveIPC", "Restart",
		"RestartForceExitStatus", "RestartKillSignal", "RestartPreventExitStatus", "RestartSec", "RestrictAddressFamilies",
		"RestrictNamespaces", "RestrictRealtime", "RestrictSUIDSGID", "RootDirectory", "RootDirectoryStartOnly",
		"RootHash", "RootHashSignature", "RootImage", "RootImageOptions", "RootVerity", "RuntimeDirectory",
		"RuntimeDirectoryMode", "RuntimeDirectoryPreserve", "RuntimeMaxSec", "SELinuxContext", "SecureBits", "SendSIGHUP",
		"SendSIGKILL", "SetCredential", "Slice", "SmackProcessLabel", "SocketBindAllow", "SocketBindDeny", "Sockets",
		"StandardError", "StandardInput", "StandardInputData", "StandardInputText", "StandardOutput", "StartLimitAction",
		"StartLimitBurst", "StartLimitInterval", "StartupBlockIOWeight", "StartupCPUShares", "StartupCPUWeight",
		"StartupIOWeight", "StateDirectory", "StateDirectoryMode", "SuccessExitStatus", "SupplementaryGroups",
		"SysVStartPriority", "SyslogFacility", "SyslogIdentifier", "SyslogLevel", "SyslogLevelPrefix",
		"SystemCallArchitectures", "SystemCallErrorNumber", "SystemCallFilter", "SystemCallLog", "TTYPath", "TTYReset",
		"TTYVHangup", "TTYVTDisallocate", "TasksAccounting", "TasksMax", "TemporaryFileSystem", "TimeoutAbortSec",
		"TimeoutCleanSec", "TimeoutSec", "TimeoutStartFailureMode", "TimeoutStartSec", "TimeoutStopFailureMode",
		"TimeoutStopSec", "TimerSlackNSec", "Type", "UMask", "USBFunctionDescriptors", "USBFunctionStrings",
		"UnsetEnvironment", "User", "UtmpIdentifier", "UtmpMode", "WatchdogSec", "WatchdogSignal", "WorkingDirectory",
	},
}

// readUnit reads the shipped unit file and fails the test when it does not exist.
func readUnit(t *testing.T) string {
	t.Helper()

	data, err := os.ReadFile(unitFile)
	if err != nil {
		t.Fatalf("read %s: %v", unitFile, err)
	}

	return string(data)
}

// joinContinuations joins lines that end in a backslash with the next line, as systemd does.
func joinContinuations(text string) []string {
	var joined []string

	pending := ""
	for _, line := range strings.Split(text, "\n") {
		if strings.HasSuffix(line, "\\") {
			pending += strings.TrimSuffix(line, "\\") + " "

			continue
		}

		joined = append(joined, pending+line)
		pending = ""
	}

	return joined
}

// parseUnit splits unit text into assignments; lines that are neither a section header, an assignment, a
// comment nor blank, and assignments outside a section, are returned as problems.
func parseUnit(text string) ([]directive, []string) {
	var (
		dirs     []directive
		problems []string
		section  string
	)

	for i, raw := range joinContinuations(text) {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, ";") {
			continue
		}

		if strings.HasPrefix(line, "[") && strings.HasSuffix(line, "]") {
			section = line[1 : len(line)-1]

			continue
		}

		key, value, found := strings.Cut(line, "=")
		if found == false || section == "" {
			problems = append(problems, fmt.Sprintf("line %d: %q is not an assignment inside a section", i+1, line))

			continue
		}

		dirs = append(dirs, directive{section: section, key: strings.TrimSpace(key), value: strings.TrimSpace(value), line: i + 1})
	}

	return dirs, problems
}

// parseUnitFile reads and parses the shipped unit and fails the test on a malformed line.
func parseUnitFile(t *testing.T) []directive {
	t.Helper()

	dirs, problems := parseUnit(readUnit(t))
	for _, problem := range problems {
		t.Errorf("%s %s", unitFile, problem)
	}

	return dirs
}

// valuesOf returns the values of every assignment of key in section, in file order, whitespace-normalized.
func valuesOf(dirs []directive, section, key string) []string {
	var values []string

	for _, d := range dirs {
		if d.section == section && d.key == key {
			values = append(values, strings.Join(strings.Fields(d.value), " "))
		}
	}

	return values
}

// requiredSetting is a key with the exact list of values it must carry, in file order.
type requiredSetting struct {
	section string
	key     string
	values  []string
}

// requiredSettings is every setting of the plan's Approach (Unit).
var requiredSettings = []requiredSetting{
	{"Unit", "Description", []string{"Vandox monitoring agent"}},
	{"Unit", "After", []string{"systemd-journal-flush.service"}},
	{"Unit", "StartLimitIntervalSec", []string{"0"}},
	{"Service", "Type", []string{"notify"}},
	{"Service", "NotifyAccess", []string{"main"}},
	{"Service", "ExecStart", []string{"/usr/local/bin/vandox-agent"}},
	{"Service", "User", []string{"vandox-agent"}},
	{"Service", "Group", []string{"vandox-agent"}},
	{"Service", "SupplementaryGroups", []string{"adm systemd-journal"}},
	{"Service", "AmbientCapabilities", []string{"CAP_SYS_PTRACE"}},
	{"Service", "CapabilityBoundingSet", []string{"CAP_SYS_PTRACE"}},
	{"Service", "NoNewPrivileges", []string{"yes"}},
	{"Service", "LoadCredential", []string{"agent-token:/etc/vandox/agent-token"}},
	{"Service", "Environment", []string{
		"VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token",
		"GOMEMLIMIT=192MiB",
	}},
	{"Service", "ProtectSystem", []string{"strict"}},
	{"Service", "ReadWritePaths", []string{"/var/lib/vandox"}},
	{"Service", "ProtectHome", []string{"yes"}},
	{"Service", "PrivateTmp", []string{"yes"}},
	{"Service", "PrivateDevices", []string{"yes"}},
	{"Service", "ProtectKernelTunables", []string{"yes"}},
	{"Service", "ProtectKernelModules", []string{"yes"}},
	{"Service", "ProtectKernelLogs", []string{"yes"}},
	{"Service", "ProtectControlGroups", []string{"yes"}},
	{"Service", "ProtectHostname", []string{"yes"}},
	{"Service", "RestrictNamespaces", []string{"yes"}},
	{"Service", "RestrictRealtime", []string{"yes"}},
	{"Service", "RestrictSUIDSGID", []string{"yes"}},
	{"Service", "LockPersonality", []string{"yes"}},
	{"Service", "MemoryDenyWriteExecute", []string{"yes"}},
	{"Service", "RemoveIPC", []string{"yes"}},
	{"Service", "UMask", []string{"0077"}},
	{"Service", "KeyringMode", []string{"private"}},
	{"Service", "RestrictAddressFamilies", []string{"AF_UNIX AF_INET AF_INET6"}},
	{"Service", "SystemCallArchitectures", []string{"native"}},
	{"Service", "SystemCallFilter", []string{
		"@system-service adjtimex",
		"~ptrace process_vm_readv process_vm_writev pidfd_getfd open_by_handle_at process_madvise",
	}},
	{"Service", "SystemCallErrorNumber", []string{"EPERM"}},
	{"Service", "InaccessiblePaths", []string{"/etc/shadow /etc/gshadow"}},
	{"Service", "IPAddressDeny", []string{"any"}},
	{"Service", "IPAddressAllow", []string{"localhost 100.64.0.0/10 fd7a:115c:a1e0::/48"}},
	{"Service", "MemoryMax", []string{"256M"}},
	{"Service", "CPUQuota", []string{"50%"}},
	{"Service", "Nice", []string{"5"}},
	{"Service", "IOWeight", []string{"50"}},
	{"Service", "WatchdogSec", []string{"2min"}},
	{"Service", "Restart", []string{"on-failure"}},
	{"Service", "RestartSec", []string{"30s"}},
	{"Install", "WantedBy", []string{"multi-user.target"}},
}

func TestService_RestartsWithoutStartLimit(t *testing.T) {
	dirs := parseUnitFile(t)

	cases := []struct {
		section string
		key     string
		want    []string
	}{
		{"Unit", "StartLimitIntervalSec", []string{"0"}},
		{"Service", "Restart", []string{"on-failure"}},
		{"Service", "RestartSec", []string{"30s"}},
	}
	for _, c := range cases {
		t.Run(c.section+"."+c.key, func(t *testing.T) {
			got := valuesOf(dirs, c.section, c.key)
			if !slices.Equal(got, c.want) {
				t.Errorf("[%s] %s = %q, want %q", c.section, c.key, got, c.want)
			}
		})
	}

	t.Run("no start limit key in [Service], where systemd 249 ignores it", func(t *testing.T) {
		for _, key := range []string{"StartLimitIntervalSec", "StartLimitInterval", "StartLimitBurst", "StartLimitAction"} {
			if got := valuesOf(dirs, "Service", key); len(got) != 0 {
				t.Errorf("[Service] %s = %q, want no such key", key, got)
			}
		}
	})
}

func TestService_NotifiesAndHasAWatchdog(t *testing.T) {
	dirs := parseUnitFile(t)

	cases := []struct {
		key  string
		want []string
	}{
		{"Type", []string{"notify"}},
		{"NotifyAccess", []string{"main"}},
		{"WatchdogSec", []string{"2min"}},
		{"ExecStart", []string{"/usr/local/bin/vandox-agent"}},
	}
	for _, c := range cases {
		t.Run(c.key, func(t *testing.T) {
			got := valuesOf(dirs, "Service", c.key)
			if !slices.Equal(got, c.want) {
				t.Errorf("[Service] %s = %q, want %q", c.key, got, c.want)
			}
		})
	}
}

func TestService_HoldsTheRequiredSettings(t *testing.T) {
	dirs := parseUnitFile(t)

	for _, r := range requiredSettings {
		t.Run(r.section+"."+r.key, func(t *testing.T) {
			got := valuesOf(dirs, r.section, r.key)
			if !slices.Equal(got, r.values) {
				t.Errorf("[%s] %s = %q, want %q", r.section, r.key, got, r.values)
			}
		})
	}

	t.Run("Unit.Documentation names the README on GitHub", func(t *testing.T) {
		got := valuesOf(dirs, "Unit", "Documentation")
		if len(got) != 1 || !strings.HasPrefix(got[0], "https://github.com/LarsLaskowski/Vandox/") ||
			!strings.HasSuffix(got[0], "deploy/agent/README.md") {
			t.Errorf("[Unit] Documentation = %q, want one URL of the GitHub repository ending in deploy/agent/README.md", got)
		}
	})
}

// forbiddenPathPrefixes are the paths no InaccessiblePaths entry may name or lie below.
var forbiddenPathPrefixes = []string{"/run/dbus", "/run/mysqld", "/var/log/journal", "/run/log/journal"}

// forbiddenKeys are the keys that must not appear at all.
var forbiddenKeys = []string{
	"ProtectClock", "PrivateNetwork", "NetworkNamespacePath", "ProcSubset", "PrivateUsers", "TemporaryFileSystem", "DynamicUser",
}

// namesForbiddenPath reports whether an InaccessiblePaths value names a forbidden path or one below it.
func namesForbiddenPath(value string) bool {
	for _, field := range strings.Fields(value) {
		path := strings.TrimLeft(field, "-+~")
		for _, prefix := range forbiddenPathPrefixes {
			if path == prefix || strings.HasPrefix(path, prefix+"/") {
				return true
			}
		}
	}

	return false
}

// forbiddenFindings returns one finding per forbidden setting in the unit text.
func forbiddenFindings(text string) []string {
	dirs, _ := parseUnit(text)

	var findings []string

	for _, d := range dirs {
		switch {
		case slices.Contains(forbiddenKeys, d.key):
			findings = append(findings, "forbidden key "+d.key)
		case d.key == "ProtectProc" && d.value != "default":
			findings = append(findings, "ProtectProc other than default")
		case d.key == "InaccessiblePaths" && namesForbiddenPath(d.value):
			findings = append(findings, "InaccessiblePaths names "+d.value)
		}
	}

	if specifierPattern.MatchString(text) {
		findings = append(findings, "contains a specifier")
	}

	if strings.Contains(text, "VANDOX_AGENT_TOKEN=") {
		findings = append(findings, "sets VANDOX_AGENT_TOKEN")
	}

	return findings
}

func TestService_HoldsNoForbiddenSetting(t *testing.T) {
	t.Run("the shipped unit", func(t *testing.T) {
		if got := forbiddenFindings(readUnit(t)); len(got) != 0 {
			t.Errorf("forbiddenFindings(%s) = %q, want none", unitFile, got)
		}
	})

	// The checker itself must detect each forbidden form, or the test above proves nothing.
	detects := []struct {
		name string
		unit string
		want string
	}{
		{"ProtectClock", "[Service]\nProtectClock=yes\n", "forbidden key ProtectClock"},
		{"PrivateNetwork", "[Service]\nPrivateNetwork=yes\n", "forbidden key PrivateNetwork"},
		{"NetworkNamespacePath", "[Service]\nNetworkNamespacePath=/run/netns/x\n", "forbidden key NetworkNamespacePath"},
		{"ProcSubset", "[Service]\nProcSubset=pid\n", "forbidden key ProcSubset"},
		{"ProtectProc invisible", "[Service]\nProtectProc=invisible\n", "ProtectProc other than default"},
		{"PrivateUsers", "[Service]\nPrivateUsers=yes\n", "forbidden key PrivateUsers"},
		{"TemporaryFileSystem", "[Service]\nTemporaryFileSystem=/run\n", "forbidden key TemporaryFileSystem"},
		{"DynamicUser", "[Service]\nDynamicUser=yes\n", "forbidden key DynamicUser"},
		{"escaped percent sign", "[Service]\nEnvironment=A=100%%\n", "contains a specifier"},
		{"specifier", "[Service]\nEnvironment=A=%d\n", "contains a specifier"},
		{"token variable", "[Service]\nEnvironment=VANDOX_AGENT_TOKEN=x\n", "sets VANDOX_AGENT_TOKEN"},
		{"quoted token variable", "[Service]\nEnvironment=\"VANDOX_AGENT_TOKEN=x\"\n", "sets VANDOX_AGENT_TOKEN"},
		{"run dbus", "[Service]\nInaccessiblePaths=/etc/shadow /run/dbus\n", "InaccessiblePaths names /etc/shadow /run/dbus"},
		{"below run mysqld", "[Service]\nInaccessiblePaths=-/run/mysqld/mysqld.sock\n", "InaccessiblePaths names -/run/mysqld/mysqld.sock"},
		{"var log journal", "[Service]\nInaccessiblePaths=/var/log/journal\n", "InaccessiblePaths names /var/log/journal"},
		{"run log journal", "[Service]\nInaccessiblePaths=/run/log/journal\n", "InaccessiblePaths names /run/log/journal"},
	}
	for _, c := range detects {
		t.Run("detects "+c.name, func(t *testing.T) {
			got := forbiddenFindings(c.unit)
			if !slices.Contains(got, c.want) {
				t.Errorf("forbiddenFindings(%q) = %q, want it to contain %q", c.unit, got, c.want)
			}
		})
	}

	allowed := []string{
		"[Service]\nProtectProc=default\n",
		"[Service]\nCPUQuota=50%\n",
		"[Service]\nEnvironment=VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token\n",
		"[Service]\nInaccessiblePaths=/etc/shadow /etc/gshadow\n",
		"[Service]\nInaccessiblePaths=/run/dbusx\n",
	}
	for _, unit := range allowed {
		t.Run("accepts "+strings.ReplaceAll(strings.TrimSpace(unit), "\n", " "), func(t *testing.T) {
			if got := forbiddenFindings(unit); len(got) != 0 {
				t.Errorf("forbiddenFindings(%q) = %q, want none", unit, got)
			}
		})
	}
}

// unknownKeyFindings returns one finding per section or key systemd 249 does not know.
func unknownKeyFindings(text string) []string {
	dirs, problems := parseUnit(text)
	findings := slices.Clone(problems)

	for _, d := range dirs {
		keys, known := systemd249Keys[d.section]

		switch {
		case known == false:
			findings = append(findings, fmt.Sprintf("line %d: unknown section [%s]", d.line, d.section))
		case slices.Contains(keys, d.key) == false:
			findings = append(findings, fmt.Sprintf("line %d: unknown key %s in [%s]", d.line, d.key, d.section))
		}
	}

	return findings
}

func TestService_UsesOnlyKeysKnownToSystemd249(t *testing.T) {
	t.Run("the shipped unit", func(t *testing.T) {
		text := readUnit(t)
		if dirs, _ := parseUnit(text); len(dirs) == 0 {
			t.Fatalf("%s holds no assignment", unitFile)
		}

		if got := unknownKeyFindings(text); len(got) != 0 {
			t.Errorf("unknownKeyFindings(%s) = %q, want none", unitFile, got)
		}
	})

	t.Run("every required setting is a key of systemd 249", func(t *testing.T) {
		for _, r := range requiredSettings {
			if !slices.Contains(systemd249Keys[r.section], r.key) {
				t.Errorf("[%s] %s is not in the key list of systemd v249", r.section, r.key)
			}
		}
	})

	// The checker itself must flag what systemd 249 ignores.
	detects := []struct {
		name string
		unit string
		want string
	}{
		{"StartLimitIntervalSec in [Service]", "[Service]\nStartLimitIntervalSec=0\n", "line 2: unknown key StartLimitIntervalSec in [Service]"},
		{"ImportCredential (systemd 254)", "[Service]\nImportCredential=x\n", "line 2: unknown key ImportCredential in [Service]"},
		{"unknown section", "[Sandbox]\nA=b\n", "line 2: unknown section [Sandbox]"},
		{"key outside a section", "A=b\n", "line 1: \"A=b\" is not an assignment inside a section"},
	}
	for _, c := range detects {
		t.Run("detects "+c.name, func(t *testing.T) {
			got := unknownKeyFindings(c.unit)
			if !slices.Contains(got, c.want) {
				t.Errorf("unknownKeyFindings(%q) = %q, want it to contain %q", c.unit, got, c.want)
			}
		})
	}
}

// specifierPattern matches a systemd specifier or an escaped percent sign.
var specifierPattern = regexp.MustCompile(`%[A-Za-z%]`)

func TestService_HasNoSpecifier(t *testing.T) {
	for i, line := range strings.Split(readUnit(t), "\n") {
		if specifierPattern.MatchString(line) {
			t.Errorf("%s line %d = %q, want no specifier (systemd 249 lacks %%d for credentials)", unitFile, i+1, line)
		}
	}

	if !specifierPattern.MatchString("RuntimeDirectory=%d") || !specifierPattern.MatchString("Nice=5%%") {
		t.Error("specifierPattern misses a specifier or an escaped percent sign")
	}

	if specifierPattern.MatchString("CPUQuota=50%") {
		t.Error("specifierPattern matches the percent sign of CPUQuota=50%, which ends the value")
	}
}

func TestService_TokenOnlyAsCredential(t *testing.T) {
	text := readUnit(t)
	dirs := parseUnitFile(t)

	wantCredential := []string{"agent-token:/etc/vandox/agent-token"}
	if got := valuesOf(dirs, "Service", "LoadCredential"); !slices.Equal(got, wantCredential) {
		t.Errorf("[Service] LoadCredential = %q, want %q", got, wantCredential)
	}

	wantEnvironment := "VANDOX_AGENT_TOKEN_FILE=/run/credentials/vandox-agent.service/agent-token"
	if got := valuesOf(dirs, "Service", "Environment"); !slices.Contains(got, wantEnvironment) {
		t.Errorf("[Service] Environment = %q, want it to contain %q", got, wantEnvironment)
	}

	if strings.Contains(text, "VANDOX_AGENT_TOKEN=") {
		t.Errorf("%s sets VANDOX_AGENT_TOKEN, want the token only through the credential file", unitFile)
	}

	for _, key := range []string{"EnvironmentFile", "PassEnvironment", "SetCredential", "SetCredentialEncrypted", "ImportCredential"} {
		if got := valuesOf(dirs, "Service", key); len(got) != 0 {
			t.Errorf("[Service] %s = %q, want no such key", key, got)
		}
	}

	if got := valuesOf(dirs, "Service", "ExecStart"); len(got) != 1 || strings.Contains(got[0], " ") {
		t.Errorf("[Service] ExecStart = %q, want one command without arguments", got)
	}

	for _, d := range dirs {
		if d.key != "LoadCredential" && strings.Contains(d.value, "/etc/vandox/agent-token") {
			t.Errorf("%s line %d (%s) names the token file outside LoadCredential", unitFile, d.line, d.key)
		}
	}
}

// verifyFindings returns the lines of systemd-analyze verify output that report an unknown key or section, an
// ignored line or a specifier problem; the exit status is not looked at.
func verifyFindings(t *testing.T, analyze, path string) []string {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	out, err := exec.CommandContext(ctx, analyze, "verify", "--man=no", path).CombinedOutput()

	var exitErr *exec.ExitError
	if err != nil && !errors.As(err, &exitErr) {
		t.Fatalf("run %s verify %s: %v", analyze, path, err)
	}

	own, foreign := splitFindings(string(out))

	for _, line := range foreign {
		t.Logf("ignored, concerns another unit: %s", line)
	}

	return own
}

// splitFindings sorts the lines of systemd-analyze verify output that report an unknown key or section, an
// ignored line or a specifier problem: own are the ones that name vandox-agent.service, foreign the ones about
// any other unit the tool loaded (for example snapd.service on the runner), which this unit cannot fix.
func splitFindings(output string) (own, foreign []string) {
	for _, line := range strings.Split(output, "\n") {
		if !strings.Contains(line, "Unknown") && !strings.Contains(line, "ignoring") && !strings.Contains(line, "specifier") {
			continue
		}

		if strings.Contains(line, unitFile) {
			own = append(own, line)
		} else {
			foreign = append(foreign, line)
		}
	}

	return own, foreign
}

func TestSplitFindings_CountsOnlyLinesOfThisUnit(t *testing.T) {
	t.Parallel()

	foreignLine := "/lib/systemd/system/snapd.service:23: Unknown key name 'RestartMode' in section 'Service', ignoring."
	ownLine := "/tmp/verify-1/" + unitFile + ":4: Unknown key name 'NoSuchKeyAtAll' in section 'Service', ignoring."
	quiet := unitFile + ": Command /usr/local/bin/vandox-agent is not executable: No such file or directory"

	t.Run("a foreign line is ignored", func(t *testing.T) {
		t.Parallel()

		own, foreign := splitFindings(quiet + "\n" + foreignLine + "\n")
		if len(own) != 0 || len(foreign) != 1 || foreign[0] != foreignLine {
			t.Errorf("splitFindings = own %q, foreign %q, want no own line and the foreign line", own, foreign)
		}
	})

	t.Run("an own line is caught beside a foreign one", func(t *testing.T) {
		t.Parallel()

		own, foreign := splitFindings(foreignLine + "\n" + ownLine + "\n" + quiet + "\n")
		if len(own) != 1 || own[0] != ownLine || len(foreign) != 1 {
			t.Errorf("splitFindings = own %q, foreign %q, want the own line caught and the foreign one set aside", own, foreign)
		}
	})

	t.Run("a specifier line of this unit is caught", func(t *testing.T) {
		t.Parallel()

		line := unitFile + ":9: Failed to resolve specifiers in Environment=%d"
		if own, _ := splitFindings(line); len(own) != 1 {
			t.Errorf("splitFindings(%q) own = %q, want the line", line, own)
		}
	})
}

// writeTempUnit writes text as vandox-agent.service into a fresh directory whose name holds none of the words
// verifyFindings looks for (t.TempDir would embed the test name, which contains "Unknown").
func writeTempUnit(t *testing.T, text string) string {
	t.Helper()

	dir, err := os.MkdirTemp("", "verify-")
	if err != nil {
		t.Fatalf("create temp dir: %v", err)
	}

	t.Cleanup(func() { _ = os.RemoveAll(dir) })

	path := filepath.Join(dir, unitFile)
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatalf("write %s: %v", path, err)
	}

	return path
}

func TestService_SystemdAnalyzeVerifyReportsNoUnknownKey(t *testing.T) {
	analyze, err := exec.LookPath("systemd-analyze")
	if err != nil {
		t.Skip("systemd-analyze is not installed; the CI job agent-install runs it on systemd 249")
	}

	bad := "[Service]\nExecStart=/bin/true\nStartLimitIntervalSec=0\nNoSuchKeyAtAll=1\n"
	if got := verifyFindings(t, analyze, writeTempUnit(t, bad)); len(got) == 0 {
		t.Fatal("systemd-analyze verify reported nothing for a unit with unknown keys, so this check cannot detect them")
	}

	text := readUnit(t)
	if got := verifyFindings(t, analyze, writeTempUnit(t, text)); len(got) != 0 {
		t.Errorf("systemd-analyze verify %s reported %q, want no unknown key, ignored line or specifier", unitFile, got)
	}
}

// staleSentences are the sentences that say the unit or the install scripts do not exist.
var staleSentences = []string{"follow with the collectors", "nothing to install", "will list"}

// documentsOfAC11 are the files, relative to the repository root, that may have mentioned the missing unit or scripts.
var documentsOfAC11 = []string{
	"README.md",
	"docs/INSTALL.md",
	"docs/ARCHITECTURE.md",
	"docs/areas/agent.md",
	"docs/areas/configuration-and-secrets.md",
	"docs/areas/network-and-security-model.md",
	"deploy/agent/README.md",
}

// installDocuments must name both scripts, because they describe installing and uninstalling the agent.
var installDocuments = []string{"README.md", "docs/INSTALL.md", "deploy/agent/README.md"}

// readDocument reads a repository file with every whitespace run (line breaks included) as one space and lower case.
func readDocument(t *testing.T, name string) string {
	t.Helper()

	data, err := os.ReadFile(filepath.Join("..", "..", filepath.FromSlash(name)))
	if err != nil {
		t.Fatalf("read %s: %v", name, err)
	}

	return strings.ToLower(strings.Join(strings.Fields(string(data)), " "))
}

func TestReadme_NamesNoMissingUnitOrScripts(t *testing.T) {
	for _, name := range documentsOfAC11 {
		t.Run(name+" holds no stale sentence", func(t *testing.T) {
			text := readDocument(t, name)
			for _, sentence := range staleSentences {
				if strings.Contains(text, sentence) {
					t.Errorf("%s contains %q, want the unit and the install scripts described as existing", name, sentence)
				}
			}
		})
	}

	for _, name := range installDocuments {
		t.Run(name+" names both scripts", func(t *testing.T) {
			text := readDocument(t, name)
			if !strings.Contains(text, "uninstall.sh") {
				t.Errorf("%s does not name uninstall.sh, want uninstalling described", name)
			}

			// "install.sh" is a substring of "uninstall.sh", so the latter is removed before looking for the former.
			if !strings.Contains(strings.ReplaceAll(text, "uninstall.sh", ""), "install.sh") {
				t.Errorf("%s does not name install.sh, want installing described", name)
			}
		})
	}
}

const (
	bundlePrefix  = "vandox-agent-install-"
	mktempChain   = `d=$(mktemp -d) && cd "$d"`
	checkChain    = "sha256sum --check --strict - &&"
	extractPrefix = "tar --no-same-owner --no-same-permissions -xzf"
	awkSelect     = "$2 == f"
)

// codeBlocks returns the fenced code blocks of a Markdown text.
func codeBlocks(text string) []string {
	var blocks []string

	parts := strings.Split(text, "```")
	for i := 1; i < len(parts); i += 2 {
		blocks = append(blocks, parts[i])
	}

	return blocks
}

// chainFindings checks one documented install block: a fresh private directory before the check and before
// tar, the bundle line selected by exact name comparison, the check chained into tar and tar chained into
// install.sh. It returns what is missing or out of order.
func chainFindings(block string) []string {
	var findings []string

	mk := strings.Index(block, mktempChain)
	check := strings.Index(block, checkChain)
	tar := strings.Index(block, extractPrefix)

	if mk < 0 {
		findings = append(findings, "no `"+mktempChain+"` (mktemp -d before the check and before tar)")
	}

	if check < 0 || strings.Contains(block, awkSelect) == false || strings.Contains(block, "awk -v f=") == false {
		findings = append(findings, "no awk selection of the bundle line followed by `"+checkChain+"`")
	}

	if tar < 0 {
		findings = append(findings, "no `"+extractPrefix+"` line")
	}

	if mk >= 0 && check >= 0 && tar >= 0 && (mk > check || check > tar) {
		findings = append(findings, "mktemp, the check and tar are not in that order")
	}

	return append(findings, chainTail(block, tar)...)
}

// chainTail checks the lines after the check: the tar line ends in && and install.sh follows it.
func chainTail(block string, tar int) []string {
	if tar < 0 {
		return nil
	}

	lines := strings.Split(block[tar:], "\n")
	if strings.HasSuffix(strings.TrimSpace(lines[0]), "&&") == false {
		return []string{"the tar line does not end in &&"}
	}

	if len(lines) < 2 || strings.Contains(lines[1], "install.sh") == false {
		return []string{"the line after tar is not the install.sh line"}
	}

	return nil
}

// forbiddenInDocuments are the ways an install document must not describe the agent install.
var forbiddenInDocuments = []*regexp.Regexp{
	regexp.MustCompile(`git clone`),
	regexp.MustCompile(`install\.sh\s+--version`),
	regexp.MustCompile(`\|\s*(ba)?sh\b`),
}

// TestInstallDocuments_UseTheReleaseBundle checks AC11: the install documents show the bundle download, the
// check before the extraction as one && chain in a fresh mktemp directory, and neither git, --version nor a
// pipe into a shell.
func TestInstallDocuments_UseTheReleaseBundle(t *testing.T) {
	for _, name := range installDocuments {
		t.Run(name, func(t *testing.T) {
			checkInstallDocument(t, name)
		})
	}
}

// checkInstallDocument checks one document for the names, the forbidden forms and the install chain.
func checkInstallDocument(t *testing.T, name string) {
	t.Helper()

	data, err := os.ReadFile(filepath.Join("..", "..", filepath.FromSlash(name)))
	if err != nil {
		t.Fatalf("read %s: %v", name, err)
	}

	text := string(data)

	for _, want := range []string{bundlePrefix, "sha256sum --check"} {
		if strings.Contains(text, want) == false {
			t.Errorf("%s does not contain %q", name, want)
		}
	}

	for _, re := range forbiddenInDocuments {
		if loc := re.FindStringIndex(text); loc != nil {
			t.Errorf("%s contains %q, want the bundle chain without it", name, text[loc[0]:loc[1]])
		}
	}

	assertChainBlocks(t, name, text)
}

// isInstallBlock reports whether a code block installs the agent from the bundle: it has the extraction line,
// or it names the bundle and runs tar (a weakened extraction). A log archive (tar without the bundle) and an
// uninstall block (the bundle without tar) are not install blocks.
func isInstallBlock(block string) bool {
	return strings.Contains(block, extractPrefix) || (strings.Contains(block, bundlePrefix) && strings.Contains(block, "tar "))
}

// assertChainBlocks requires at least one block that extracts the bundle and checks each of them.
func assertChainBlocks(t *testing.T, name, text string) {
	t.Helper()

	found := 0

	for _, block := range codeBlocks(text) {
		if isInstallBlock(block) == false {
			continue
		}

		found++

		for _, finding := range chainFindings(block) {
			t.Errorf("%s: an install block has a defect: %s\n%s", name, finding, block)
		}
	}

	if found == 0 {
		t.Errorf("%s has no code block that extracts the bundle with tar", name)
	}
}

// TestChainFindings_CatchesAWeakChain keeps the document check honest: the approved chain passes and each
// weakening is found.
func TestChainFindings_CatchesAWeakChain(t *testing.T) {
	t.Parallel()

	good := "v=v1.2.3\n" + mktempChain + " &&\n  curl -fsSLO \"$base/x\" &&\n" +
		"  awk -v f=\"vandox-agent-install-$v.tar.gz\" 'NF == 2 && $2 == f' SHA256SUMS | sha256sum --check --strict - &&\n" +
		"  " + extractPrefix + " \"x.tar.gz\" &&\n  \"./vandox-agent-install-$v/install.sh\" --agent-id web-1\n"

	if got := chainFindings(good); len(got) != 0 {
		t.Fatalf("the approved chain has findings %q, want none", got)
	}

	weak := map[string]string{
		"no mktemp":         strings.Replace(good, mktempChain, "cd /tmp", 1),
		"check not chained": strings.Replace(good, checkChain, "sha256sum --check --strict -;", 1),
		"tar without &&":    strings.Replace(good, "\"x.tar.gz\" &&", "\"x.tar.gz\"", 1),
		"no awk":            strings.Replace(good, "awk -v f=", "grep -v f=", 1),
		"no install line":   strings.Replace(good, "install.sh", "uninstall-notes", 1),
	}

	for name, block := range weak {
		if len(chainFindings(block)) == 0 {
			t.Errorf("chainFindings found nothing wrong with the chain %q", name)
		}
	}
}

// TestIsInstallBlock_IgnoresOtherTarCommands keeps the heuristic honest: an unrelated tar -czf block is not an
// install block, while a block that names the bundle but has a weak chain is one and its defect is found.
func TestIsInstallBlock_IgnoresOtherTarCommands(t *testing.T) {
	t.Parallel()

	logs := "sudo tar -czf ~/vandox-logs.tar.gz --exclude=var/log/journal /var/log\n"
	if isInstallBlock(logs) {
		t.Errorf("a log archive block is taken for an install block")
	}

	weak := mktempChain + " &&\n  awk -v f=\"vandox-agent-install-$v.tar.gz\" 'NF == 2 && $2 == f' SHA256SUMS | sha256sum --check --strict - &&\n" +
		"  " + extractPrefix + " \"x.tar.gz\"\n  \"./vandox-agent-install-$v/install.sh\"\n"

	uninstall := "sudo \"./vandox-agent-install-$v/uninstall.sh\" --purge\n"
	if isInstallBlock(uninstall) {
		t.Errorf("an uninstall block is taken for an install block")
	}

	if isInstallBlock(weak) == false {
		t.Fatalf("a block that names the bundle is not taken for an install block")
	}

	if len(chainFindings(weak)) == 0 {
		t.Errorf("a chain without && before the install line was not caught")
	}
}
