"""Per-repository settings for the squad tools (Go agent and .NET backend). Seeded once by adopt-template and
kept on later refreshes; the scripts that import it are template-managed.

The repository has two languages. Both coverage reports are Cobertura XML: coverlet writes one per .NET test
project, `go-coverage-to-cobertura.py` converts the Go profile, and all of them land under `TestResults/`."""

# Coverage gate (.squad/tools/coverage-check.py)
COVERAGE_FORMAT = "cobertura"
COVERAGE_REPORT_GLOB = "TestResults/**/coverage.cobertura.xml"
COVERAGE_PATHSPECS = ["*.go", "*.cs", "*.razor"]
COVERAGE_EXCLUDES = ["*_test.go", "tests/*", "**/obj/*"]
COVERAGE_TEST_PATHSPECS = ["*_test.go", "tests/*"]
