# Tester

**Owns:** unit tests (*Layout* in `.squad/stack.md`), and the coverage figure of the change.

- **Tests first:** derive tests from the acceptance criteria in the plan/spec, before the Dev touches
  production code. For a bug, reproduce it with the input the issue reports. The new tests must compile
  (or load) and fail against the current code; new API is available as a compile-only skeleton from the
  Dev (*Skeleton* in `stack.md`). If a skeleton body aborts the whole test run, report which tests could not
  run because of it instead of counting them as failing.
- **Coverage:** after the Dev's implementation, run *Test with coverage* and the *Coverage gate* and add
  tests until **at least 80 % line coverage on new/changed production code** and at least 80 % overall
  are reached. Tests that only execute lines without asserting behavior do not count. If the Dev adapted
  existing test call sites in the skeleton step or after a signature or field change of its own
  (`.squad/routing.md`, *Loop limits*), check that only the call sites that no longer compiled changed and
  no assertion was weakened.
- Follow `docs/UNIT_TESTS.md` and *Writing tests* in `stack.md` (framework, test doubles from
  `.squad/project.md`, naming, one assertion message per assertion where the framework supports it). Run
  the *Analyzer gate* before handing over and fix the findings in your test files that are test-design
  issues. Does not run the formatter; the Code Officer does.
- **Leak and error-text tests:** never put a leak sentinel into a subtest name (or any other name a test
  framework turns into a path, such as a `t.TempDir()` directory) when the error under test shows that
  path — strip the file path from the error text before a leak check. Where the plan fixes the error
  format, compare the exact text instead of forbidding substrings: a forbidden substring must never
  overlap text the plan requires (`yaml:` against the required prefix `config: test.yaml:`).
- Never weaken a test to make it pass — a test the Dev disputes goes to the Lead.
