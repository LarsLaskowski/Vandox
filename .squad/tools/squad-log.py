#!/usr/bin/env python3
"""Append one row to a squad working record's `log.md`, the way the pipeline wants it, and summarize what
each role cost over the run.

The orchestrator is the only writer of `log.md`; it records what the members report. This script keeps the
mechanics out of the orchestrator's hands:

- the row is `| <date> | <step> | <member> | <result> |`, appended after the last line, which gets its line
  ending first if it lacks one;
- the line ending follows the file (CRLF when the file uses it), so rows never end up with mixed endings;
- a `|` in a cell is written as `\\|`, a line break in the result as `<br>` (a Markdown table cell has one line);
- a control character (Unicode categories Cc, Cf, Zl, Zp other than tab and line break) is written as its
  escape (`U+202E`), never as the character itself; a tab becomes a space;
- for a subagent launch, `--launch MODEL/EFFORT --tokens N --tool-uses N --seconds N` (from the usage block
  at the end of the launch result) appends a trailer `(opus/high · 37,445 tokens · 6 tool uses · 57 s)` the
  summary can read back.

Usage, from anywhere inside the repository:
    python3 .squad/tools/squad-log.py <work folder> <step> <member> <result> [--launch opus/high
        --tokens 37445 --tool-uses 6 --seconds 57]
    python3 .squad/tools/squad-log.py <work folder> --summary

`<work folder>` is the folder's name under `specs/` (`issue-12`, or `specs/issue-12`); it is matched against the
folders that exist there, never used as a path, so no argument reaches the file system. `<result>` may
contain line breaks.
`--summary` prints a Markdown table per member (launches, tokens, tool uses, seconds) with a total row, for
the "Squad working record" comment and the wrap-up report. Exit code 0 when the row was written or the
summary printed, 1 when the folder has no `log.md`.
"""
import argparse
import datetime
import os
import re
import subprocess
import sys
import unicodedata

CONTROL_CATEGORIES = {"Cc", "Cf", "Zl", "Zp"}
KEEP = {"\t", "\n", "\r"}
TRAILER = re.compile(r"\((?P<launch>[\w.-]+/[\w-]+) · (?P<tokens>[\d,]+) tokens · (?P<tools>\d+) tool uses · "
                     r"(?P<seconds>\d+) s\)\s*$")


def escape_controls(text):
    """Every control character other than tab and line break as `U+XXXX`."""
    return "".join(f"U+{ord(ch):04X}" if unicodedata.category(ch) in CONTROL_CATEGORIES and ch not in KEEP else ch
                   for ch in text)


def cell(text):
    text = escape_controls(text).replace("\t", " ")
    text = re.sub(r"\r\n|\r|\n", "<br>", text.strip())
    return text.replace("|", "\\|")


def trailer(launch, tokens, tool_uses, seconds):
    return f"({launch} · {tokens:,} tokens · {tool_uses} tool uses · {seconds} s)"


def resolve_log(root, folder):
    """The `log.md` of the named work folder under `specs/`, or None. The name is compared with the folders
    that exist there and the path is built from the directory listing, so the argument never becomes a path."""
    wanted = folder.strip().replace("\\", "/").rstrip("/")
    wanted = wanted[len("specs/"):] if wanted.startswith("specs/") else wanted
    specs = os.path.join(root, "specs")
    if not os.path.isdir(specs):
        return None
    for name in sorted(os.listdir(specs)):
        if name == wanted and name not in (".", "..") and os.path.isdir(os.path.join(specs, name)):
            path = os.path.join(specs, name, "log.md")
            return path if os.path.isfile(path) else None
    return None


def append_row(path, step, member, result, today=None, launch=None):
    with open(path, "rb") as handle:
        data = handle.read()
    eol = b"\r\n" if b"\r\n" in data else b"\n"
    date = today or datetime.date.today().isoformat()
    text = result if launch is None else f"{result} {trailer(*launch)}"
    row = f"| {date} | {cell(step)} | {cell(member)} | {cell(text)} |".encode()
    if data and not data.endswith((b"\n", b"\r\n")):
        data += eol
    with open(path, "wb") as handle:
        handle.write(data + row + eol)
    return row.decode()


def parse_rows(text):
    """(member, result) of every `| date | step | member | result |` row; the title may contain escaped pipes."""
    rows = []
    for line in text.splitlines():
        cells = [c.strip() for c in re.split(r"(?<!\\)\|", line.strip())]
        if len(cells) >= 6 and not cells[0] and not cells[-1] and re.fullmatch(r"\d{4}-\d{2}-\d{2}", cells[1]):
            rows.append((cells[3], "|".join(cells[4:-1])))
    return rows


def summary(path):
    """Markdown table: launches, tokens, tool uses and seconds per member (and model/effort), with a total."""
    with open(path, encoding="utf-8-sig") as handle:
        rows = parse_rows(handle.read())
    per_member = {}
    for member, result in rows:
        match = TRAILER.search(result)
        if not match:
            continue
        key = (member, match.group("launch"))
        entry = per_member.setdefault(key, [0, 0, 0, 0])
        entry[0] += 1
        entry[1] += int(match.group("tokens").replace(",", ""))
        entry[2] += int(match.group("tools"))
        entry[3] += int(match.group("seconds"))
    lines = ["| Member | Model/effort | Launches | Tokens | Tool uses | Seconds |",
             "| ------ | ------------ | -------- | ------ | --------- | ------- |"]
    total = [0, 0, 0, 0]
    for (member, launch), (launches, tokens, tools, seconds) in sorted(per_member.items()):
        lines.append(f"| {member} | {launch} | {launches} | {tokens:,} | {tools} | {seconds} |")
        total = [a + b for a, b in zip(total, (launches, tokens, tools, seconds), strict=True)]
    lines.append(f"| **Total** | | {total[0]} | {total[1]:,} | {total[2]} | {total[3]} |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder")
    parser.add_argument("step", nargs="?")
    parser.add_argument("member", nargs="?")
    parser.add_argument("result", nargs="?")
    parser.add_argument("--summary", action="store_true", help="print the per-member launch table instead of a row")
    parser.add_argument("--launch", metavar="MODEL/EFFORT", help="the launched subagent's model and effort")
    parser.add_argument("--tokens", type=int, help="subagent_tokens from the launch result's usage block")
    parser.add_argument("--tool-uses", type=int, help="tool_uses from the launch result's usage block")
    parser.add_argument("--seconds", type=int, help="duration of the launch in seconds (duration_ms / 1000)")
    args = parser.parse_args()
    toplevel = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=False)
    root = toplevel.stdout.strip() if toplevel.returncode == 0 else os.getcwd()
    path = resolve_log(root, args.folder)
    if path is None:
        print(f"No log.md in {args.folder} (create it from specs/_template/log.md first)")
        return 1
    if args.summary:
        print(summary(path))
        return 0
    if args.step is None or args.member is None or args.result is None:
        parser.error("a row needs <step> <member> <result> (or --summary)")
    launch = None
    if args.launch or args.tokens is not None:
        if not args.launch or None in (args.tokens, args.tool_uses, args.seconds):
            parser.error("--launch, --tokens, --tool-uses and --seconds go together")
        launch = (args.launch, args.tokens, args.tool_uses, args.seconds)
    print(append_row(path, args.step, args.member, args.result, launch=launch))
    return 0


if __name__ == "__main__":
    sys.exit(main())
