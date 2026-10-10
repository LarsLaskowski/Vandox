"""Per-repository settings for the squad tools (Go agent and .NET backend). Seeded once by adopt-template and
kept on later refreshes; the scripts that import it are template-managed.

The repository has two languages. Both coverage reports are Cobertura XML: coverlet writes one per .NET test
project, `go-coverage-to-cobertura.py` converts the Go profile, and all of them land under `TestResults/`."""

# Base branch the gates diff against (merge base with HEAD); fetch it before running the gates.
BASE_REF = "origin/main"

# Solution the analyzer gate builds.
SOLUTION = "Vandox.slnx"

# Coverage gate (.squad/tools/coverage-check.py): line coverage on new/changed production lines, and overall.
# COVERAGE_OVERALL_THRESHOLD may start below COVERAGE_THRESHOLD in a repository adopted with a coverage debt;
# it is never lowered and is raised towards COVERAGE_THRESHOLD as coverage improves.
COVERAGE_THRESHOLD = 80
COVERAGE_OVERALL_THRESHOLD = 80
COVERAGE_REPORTS = [
    ("cobertura", "TestResults/go/coverage.cobertura.xml"),  # Go agent, converted from coverage.out
    ("cobertura", "TestResults/*-*-*-*-*/coverage.cobertura.xml"),  # every .NET test project (coverlet), merged
]
COVERAGE_PATHSPECS = ["*.go", "*.cs", "*.razor"]
COVERAGE_EXCLUDES = ["*_test.go", "tests/*", "**/obj/*"]
COVERAGE_TEST_PATHSPECS = ["*_test.go", "tests/*"]
