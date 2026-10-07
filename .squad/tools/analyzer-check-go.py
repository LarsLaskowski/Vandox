#!/usr/bin/env python3
"""Analyzer gate (Go profile): `go vet ./...` must pass for the whole module, and golangci-lint must report
no issue anywhere in a file changed since the merge base with origin/main (`--whole-files`; working tree
and untracked files included). The per-linter caps are switched off so one run prints every finding.

The base (origin/main) and the commands are fixed here: the script takes no arguments, so nothing
user-supplied reaches the shell, git or the filesystem.

Usage, from the repository root:
    python3 .squad/tools/analyzer-check.py

Exit code 0 when both pass, 1 otherwise.
"""
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyzer_common import BASE_REF, shell_check  # noqa: E402  (shared helpers next to this script)

STEPS = [
    ("go vet", ["go", "vet", "./..."]),
    ("golangci-lint (changed files)", ["golangci-lint", "run", "--new-from-merge-base=" + BASE_REF, "--whole-files",
                                     "--max-issues-per-linter=0", "--max-same-issues=0", "./..."]),
]


def main():
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    failed = False
    for name, command in STEPS:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        output = (result.stdout + result.stderr).strip()
        if output:
            print(output[-6000:])
        print(f"{name}: {'PASS' if result.returncode == 0 else 'FAIL'}\n")
        failed = failed or result.returncode != 0
    failed = not shell_check() or failed
    print("PASS" if not failed else "FAIL")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
