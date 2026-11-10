#!/usr/bin/env bash
# Runs the load-dependent Go tests 20 times each under CPU load (record 0196): two busy-loop processes per CPU
# while one `go test -race -count=20` runs the listed tests. Fails closed: a listed test that does not exist, or a
# load process that is gone when the run ends, fails the script. No arguments; run from any directory.
set -euo pipefail

readonly RUNS=20
readonly LOADS_PER_CPU=2
readonly TEST_TIMEOUT="30m"

# Each entry is "<package path> <TestName>", grouped by the reason it is listed (docs/UNIT_TESTS.md).
readonly LISTED=(
  # Failed intermittently in CI (#329, #336).
  "internal/sender TestHTTPTransport_ReadsAtMostFourKiBOfBody"
  "internal/connstats TestConnections_ManySocketsStayWithinCaps"
  "internal/scheduler TestScheduler_TriggerWhileStuckRecordsGap"
  "internal/scheduler TestStressDryRun_WallClockStaysCloseToCPUTime"
  # The verdict is a ratio of measured durations.
  "internal/wire TestDecoder_TimeIsInProportionToTheLine"
  "internal/logstages TestClassifyMail_TimeGrowsLinearlyWithTheLengthOfTheMessage"
  # The verdict is a heap size sampled while or after the code runs.
  "internal/journal TestFile_MemoryDoesNotGrowWithJournalSize"
  "internal/kernevents TestKernel_BacklogMemoryIsBounded"
  "internal/logship TestLogs_HeapStaysBounded"
  "internal/logstages TestStages_BuffersExistOnlyWhileAGroupIsOpen"
  "internal/sender TestSender_MemoryBound"
  "internal/spool TestSpool_MemoryBound"
  "internal/spool TestOpen_TooManyFiles"
  "internal/spool TestOpen_ManyForeignEntries"
  "internal/zstdfield TestDecoder_HeapStaysBoundedForTheLargestFrame"
)

load_pids=()

fail() {
  echo "::error::$*" >&2
  exit 1
}

# shellcheck disable=SC2329 # invoked by the EXIT trap
stop_load() {
  local pid
  for pid in "${load_pids[@]}"; do
    kill "$pid" 2>/dev/null || true
  done
  for pid in "${load_pids[@]}"; do
    wait "$pid" 2>/dev/null || true
  done
}

# Exits 1, after checking all entries, when a listed test does not exist in its package.
check_listed() {
  local entry package name listing found=0 missing=0
  for entry in "${LISTED[@]}"; do
    package=${entry%% *}
    name=${entry#* }
    listing="$(go test -list "^${name}\$" "./${package}" 2>/dev/null || true)"
    if grep -qx -- "$name" <<<"$listing"; then
      found=$((found + 1))
    else
      echo "::error::listed test ${package} ${name} not found" >&2
      missing=$((missing + 1))
    fi
  done
  [[ $missing -eq 0 ]] || exit 1
  echo "all ${found} listed tests exist"
}

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

check_listed

packages=()
names=()
for entry in "${LISTED[@]}"; do
  packages+=("./${entry%% *}")
  names+=("${entry#* }")
done
mapfile -t packages < <(printf '%s\n' "${packages[@]}" | sort -u)
pattern="^($(IFS='|'; echo "${names[*]}"))\$"
readonly pattern

echo "building the race test binaries without load"
go test -race -count=1 -run '^$' "${packages[@]}"

cpus="$(nproc)"
readonly cpus
load_count=$((cpus * LOADS_PER_CPU))
readonly load_count
echo "nproc=${cpus}, starting ${load_count} busy-loop processes"
trap stop_load EXIT # invoked on every exit
for ((i = 0; i < load_count; i++)); do
  bash -c 'while :; do :; done' &
  load_pids+=("$!")
done
echo "load PIDs: ${load_pids[*]}"

echo "tests: ${names[*]}"
echo "packages: ${packages[*]}"
echo "running go test -race -count=${RUNS} -timeout ${TEST_TIMEOUT}"
status=0
started=$SECONDS
go test -race -count="$RUNS" -timeout "$TEST_TIMEOUT" -run "$pattern" "${packages[@]}" || status=$?
elapsed=$((SECONDS - started))

for pid in "${load_pids[@]}"; do
  kill -0 "$pid" 2>/dev/null || fail "load process ${pid} ended before the test run finished"
done

echo "test run finished with status ${status} after ${elapsed} s"
exit "$status"
