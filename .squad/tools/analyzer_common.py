"""Helpers shared by the profiles' analyzer gates (`analyzer-check*.py`): git access with a fixed base, and the
shellcheck pass over changed shell scripts. Written by adopt-template; it takes no arguments, so nothing
user-supplied reaches the shell, git or the filesystem."""
import os
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import squad_settings as settings  # noqa: E402  (per-repository settings next to this script)

BASE_REF = getattr(settings, "BASE_REF", "origin/main")


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def merge_base():
    """The merge base of BASE_REF and HEAD; a missing base ref is a clear error, not a traceback."""
    result = subprocess.run(["git", "merge-base", BASE_REF, "HEAD"], capture_output=True, text=True, check=False)
    if result.returncode != 0:
        sys.exit(f"Cannot find the merge base with {BASE_REF}: fetch it (git fetch origin <branch>) or set BASE_REF "
                 "in .squad/tools/squad_settings.py to the base branch of this repository")
    return result.stdout.strip()


def shell_check():
    """shellcheck on changed shell scripts when it is installed; says so when files are skipped (the gate
    does not analyse shell otherwise). Returns True when nothing failed."""
    names = git("diff", "--name-only", "--diff-filter=d", merge_base()).splitlines()
    names += git("ls-files", "--others", "--exclude-standard").splitlines()
    files = sorted({n.strip() for n in names if n.strip().endswith(".sh") and os.path.isfile(n.strip())})
    if not files:
        return True
    if shutil.which("shellcheck") is None:
        print(f"shellcheck: NOT RUN (not installed) - {len(files)} changed shell script(s) are not analysed locally\n")
        return True
    result = subprocess.run(["shellcheck", "--", *files], capture_output=True, text=True, check=False)
    output = (result.stdout + result.stderr).strip()
    if output:
        print(output[-6000:])
    print(f"shellcheck (changed shell scripts): {'PASS' if result.returncode == 0 else 'FAIL'}\n")
    return result.returncode == 0
