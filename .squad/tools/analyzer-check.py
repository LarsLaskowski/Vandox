#!/usr/bin/env python3
"""Analyzer gate for a repository with several stack profiles: runs the analyzer gate of every profile
(`analyzer-check-<profile>.py` next to this script, written by adopt-template) and fails when any of them
fails. A profile whose tool is missing fails its own script, so a missing analyzer is never skipped.

The scripts are found by their fixed name pattern next to this file; the script takes no arguments, so
nothing user-supplied reaches the shell, git or the filesystem.

Usage, from the repository root:
    python3 .squad/tools/analyzer-check.py

Exit code 0 when every profile passes, 1 otherwise.
"""
import glob
import os
import subprocess
import sys


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    scripts = sorted(glob.glob(os.path.join(here, "analyzer-check-*.py")))
    if not scripts:
        print("No analyzer-check-<profile>.py next to this script - refresh the squad with adopt-template")
        return 1
    failed = []
    for script in scripts:
        profile = os.path.basename(script)[len("analyzer-check-"):-len(".py")]
        print(f"=== Analyzer gate: {profile} ===")
        sys.stdout.flush()
        if subprocess.run([sys.executable, script], check=False).returncode != 0:
            failed.append(profile)
        print()
    print("PASS" if not failed else f"FAIL ({', '.join(failed)})")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
