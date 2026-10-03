"""Per-repository settings for the squad tools (Go profile). Seeded once by adopt-template and kept on
later refreshes; the scripts that import it are template-managed."""

# Coverage gate (.squad/tools/coverage-check.py)
COVERAGE_FORMAT = "go"
COVERAGE_REPORT_GLOB = "coverage.out"
COVERAGE_PATHSPECS = ["*.go"]
COVERAGE_EXCLUDES = ["*_test.go"]
