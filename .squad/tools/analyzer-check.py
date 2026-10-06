#!/usr/bin/env python3
"""Analyzer gate (Go agent and .NET backend).

Go: `go vet ./...` must pass for the whole module, and golangci-lint must report no issue anywhere in a file
changed since the merge base with origin/main (`--whole-files`; working tree and untracked files included).
The per-linter caps are switched off so one run prints every finding.

.NET: `dotnet build Vandox.slnx` with `TreatWarningsAsErrors` (set in `Directory.Build.props`) must pass: the
Reihitsu and SonarAnalyzer.CSharp analyzers run inside the build with every rule, info level included, on
the whole solution. The build is forced (`--no-incremental`) so cached results never hide a diagnostic.

The base (origin/main) and the commands are fixed here: the script takes no arguments, so nothing
user-supplied reaches the shell, git or the filesystem. A step whose tool is not installed fails the gate
(it never passes silently).

Usage, from the repository root:
    python3 .squad/tools/analyzer-check.py

Exit code 0 when all steps pass, 1 otherwise.
"""
import os
import shutil
import subprocess
import sys

BASE_REF = "origin/main"
STEPS = [
    ("go vet", ["go", "vet", "./..."]),
    ("golangci-lint (changed files)", ["golangci-lint", "run", "--new-from-merge-base=" + BASE_REF, "--whole-files",
                                     "--max-issues-per-linter=0", "--max-same-issues=0", "./..."]),
    ("dotnet build (analyzers, warnings as errors)", ["dotnet", "build", "Vandox.slnx", "--no-incremental", "--nologo",
                                                      "-warnaserror"]),
]


def main():
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    failed = False
    for name, command in STEPS:
        if shutil.which(command[0]) is None:
            print(f"{name}: FAIL ({command[0]} is not installed)\n")
            failed = True
            continue
        result = subprocess.run(command, capture_output=True, text=True, check=False)
        output = (result.stdout + result.stderr).strip()
        if output:
            print(output[-6000:])
        print(f"{name}: {'PASS' if result.returncode == 0 else 'FAIL'}\n")
        failed = failed or result.returncode != 0
    print("PASS" if not failed else "FAIL")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
