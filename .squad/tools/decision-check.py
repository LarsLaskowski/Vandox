#!/usr/bin/env python3
"""Decision-record gate: the records in `docs/decisions/` must be consistent and released records frozen.

Checks, without arguments (an optional argument is the repository root):

- every `docs/decisions/NNNN-title.md` has a valid `Status` (`Proposed`, `Accepted` or `Superseded by NNNN`)
  and a `Supersedes` field naming existing records;
- the index in `docs/decisions/README.md` has exactly one row per record, with the same status;
- `Superseded by NNNN` names an existing record that lists this one under `Supersedes`;
- a record is *released* once the commit that added it is contained in a release tag (`v*`). A released
  record is never deleted and never changed except for its status becoming `Superseded by NNNN`; an
  unreleased record is never `Superseded` (it is edited in place or deleted instead).

Without release tags nothing is released and the freeze checks pass. Without Git history (not a Git
repository, shallow clone) the freeze checks are skipped with a warning.

Usage, from anywhere inside the repository:
    python3 .squad/tools/decision-check.py

Exit code 0 when everything is valid, 1 otherwise.
"""
import glob
import os
import re
import subprocess
import sys

DECISIONS_DIR = os.path.join("docs", "decisions")
RECORD_NAME = re.compile(r"^(\d{4})-.+\.md$")
STATUS_LINE = re.compile(r"^- \*\*Status:\*\*[ \t]*(.*)$", re.MULTILINE)
SUPERSEDES_LINE = re.compile(r"^- \*\*Supersedes:\*\*[ \t]*(.*)$", re.MULTILINE)
SUPERSEDED_BY = re.compile(r"^Superseded by (\d{4})$")
NUMBER = re.compile(r"\b(\d{4})\b")
INDEX_BLOCK = re.compile(r"<!-- project:begin index -->(.*?)<!-- project:end index -->", re.DOTALL)
INDEX_ROW = re.compile(r"^\|\s*(\d{4})\s*\|(.*)\|\s*([^|]*?)\s*\|\s*[^|]*?\s*\|\s*$")
RELEASE_TAGS = "v*"


def read_text(path):
    with open(path, encoding="utf-8-sig") as handle:
        return handle.read().replace("\r\n", "\n")


def git(root, *args):
    result = subprocess.run(["git", "-C", root, *args], capture_output=True, text=True, encoding="utf-8")
    return result.stdout if result.returncode == 0 else None


def load_records(root, errors):
    records = {}
    for path in sorted(glob.glob(os.path.join(root, DECISIONS_DIR, "*.md"))):
        name = os.path.basename(path)
        match = RECORD_NAME.match(name)
        if not match:
            continue
        number, text = match.group(1), read_text(path)
        rel = f"{DECISIONS_DIR}/{name}".replace(os.sep, "/")
        status = STATUS_LINE.search(text)
        supersedes = SUPERSEDES_LINE.search(text)
        if not status:
            errors.append(f"{rel}: no '- **Status:**' line")
            continue
        status = status.group(1).strip()
        if status not in ("Proposed", "Accepted") and not SUPERSEDED_BY.match(status):
            errors.append(f"{rel}: status '{status}' must be Proposed, Accepted or Superseded by NNNN")
        if number in records:
            errors.append(f"{rel}: number {number} is used twice")
        records[number] = {"rel": rel, "status": status, "text": text,
                           "supersedes": NUMBER.findall(supersedes.group(1)) if supersedes else []}
    return records


def check_links(records, errors):
    for number, record in records.items():
        for older in record["supersedes"]:
            if older not in records:
                errors.append(f"{record['rel']}: Supersedes {older}, which does not exist")
        match = SUPERSEDED_BY.match(record["status"])
        if not match:
            continue
        newer = records.get(match.group(1))
        if newer is None:
            errors.append(f"{record['rel']}: Superseded by {match.group(1)}, which does not exist")
        elif number not in newer["supersedes"]:
            errors.append(f"{record['rel']}: Superseded by {match.group(1)}, but {newer['rel']} does not list "
                          f"{number} under Supersedes")


def check_index(root, records, errors):
    path = os.path.join(root, DECISIONS_DIR, "README.md")
    if not os.path.isfile(path):
        if records:
            errors.append(f"{DECISIONS_DIR}/README.md is missing")
        return
    block = INDEX_BLOCK.search(read_text(path))
    if not block:
        errors.append(f"{DECISIONS_DIR}/README.md: no 'project:begin index' block")
        return
    rows = {}
    for line in block.group(1).splitlines():
        match = INDEX_ROW.match(line.strip())
        if match:
            if match.group(1) in rows:
                errors.append(f"{DECISIONS_DIR}/README.md: index lists {match.group(1)} twice")
            rows[match.group(1)] = match.group(3)
    for number, record in records.items():
        if number not in rows:
            errors.append(f"{record['rel']}: missing in the index of {DECISIONS_DIR}/README.md")
        elif rows[number] != record["status"]:
            errors.append(f"{record['rel']}: index says '{rows[number]}', the record says '{record['status']}'")
    for number in rows:
        if number not in records:
            errors.append(f"{DECISIONS_DIR}/README.md: index row {number} has no record file")


def without_status(text):
    return STATUS_LINE.sub("- **Status:**", text, count=1)


def check_freeze(root, records, errors, warnings):
    if git(root, "rev-parse", "--git-dir") is None:
        warnings.append("not a Git repository - released records are not checked")
        return
    tags = (git(root, "tag", "--list", RELEASE_TAGS) or "").split()
    if not tags:
        return
    if (git(root, "rev-parse", "--is-shallow-repository") or "").strip() == "true":
        warnings.append("shallow clone - released records are not checked (fetch the full history and tags)")
        return
    released = {}
    for number, record in records.items():
        added = (git(root, "log", "--diff-filter=A", "--format=%H", "--", record["rel"]) or "").split()
        if not added:
            continue  # not committed yet
        containing = (git(root, "tag", "--contains", added[-1], "--list", RELEASE_TAGS) or "").split()
        if containing:
            released[number] = containing[0]
        elif SUPERSEDED_BY.match(record["status"]):
            errors.append(f"{record['rel']}: is not released (no '{RELEASE_TAGS}' tag contains it) and must not "
                          f"be 'Superseded' - edit the record in place or delete it")
    for number, tag in released.items():
        record = records[number]
        old = git(root, "show", f"{tag}:{record['rel']}")
        if old is None:
            continue
        old = old.replace("\r\n", "\n").lstrip("﻿")
        if without_status(old) != without_status(record["text"]):
            errors.append(f"{record['rel']}: released in {tag} and changed since - only its status may change "
                          f"(supersede it with a new record instead)")
        old_status = STATUS_LINE.search(old)
        if old_status and old_status.group(1).strip() != record["status"] \
                and not SUPERSEDED_BY.match(record["status"]):
            errors.append(f"{record['rel']}: released in {tag}; its status may only change to 'Superseded by NNNN'")
    for tag in tags:
        listing = git(root, "ls-tree", "--name-only", f"{tag}:{DECISIONS_DIR.replace(os.sep, '/')}") or ""
        for name in listing.split():
            match = RECORD_NAME.match(name)
            if match and match.group(1) not in records:
                errors.append(f"{DECISIONS_DIR}/{name}: released in {tag} and deleted since - a released record "
                              f"is never deleted")


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else (git(".", "rev-parse", "--show-toplevel") or ".").strip()
    errors, warnings = [], []
    records = load_records(root, errors)
    check_links(records, errors)
    check_index(root, records, errors)
    check_freeze(root, records, errors, warnings)
    for warning in warnings:
        print(f"WARNING: {warning}")
    for error in errors:
        print(f"ERROR: {error}")
    print(f"\nChecked {len(records)} decision records: {'PASS' if not errors else 'FAIL'}")
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
