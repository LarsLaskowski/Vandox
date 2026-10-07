"""Helpers shared by the profiles' analyzer gates (`analyzer-check*.py`): git access with a fixed base, and the
shellcheck pass over changed shell scripts. Written by adopt-template; it takes no arguments, so nothing
user-supplied reaches the shell, git or the filesystem."""
import os
import shutil
import subprocess

BASE_REF = "origin/main"


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def shell_check():
    """shellcheck on changed shell scripts when it is installed; says so when files are skipped (the gate
    does not analyse shell otherwise). Returns True when nothing failed."""
    merge_base = git("merge-base", BASE_REF, "HEAD").strip()
    names = git("diff", "--name-only", "--diff-filter=d", merge_base).splitlines()
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
