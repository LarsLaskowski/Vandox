#!/usr/bin/env python3
"""Converts the Go coverage profile (`coverage.out`, from `go test -coverprofile`) into a Cobertura report at
`TestResults/go/coverage.cobertura.xml`, so `coverage-check.py` merges the Go agent's coverage with the .NET
projects' coverlet reports (it reads one report format only).

The input, the output and the module come from fixed paths in the repository, not from the command line.

Usage, from the repository root, after `go test ./... -race -coverprofile=coverage.out`:
    python3 .squad/tools/go-coverage-to-cobertura.py

Exit code 0 on success, 1 when the profile is missing or malformed.
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

PROFILE = "coverage.out"
OUTPUT = os.path.join("TestResults", "go", "coverage.cobertura.xml")
BLOCK = re.compile(r"^(.+):(\d+)\.\d+,(\d+)\.\d+ (\d+) (\d+)$")


def module_name():
    with open("go.mod", encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("module "):
                return line.split()[1].strip()
    sys.exit("go.mod has no module line")


def read_profile(module):
    """Return {repo-relative file: {line: hits}} from the profile."""
    if not os.path.exists(PROFILE):
        sys.exit(f"{PROFILE} not found - run `go test ./... -race -coverprofile={PROFILE}` first")
    files = {}
    with open(PROFILE, encoding="utf-8") as handle:
        for raw in handle:
            match = BLOCK.match(raw.strip())
            if not match:
                continue
            name, start, end, statements, count = match.groups()
            if int(statements) == 0:
                continue
            path = name[len(module) + 1:] if name.startswith(module + "/") else name
            lines = files.setdefault(path, {})
            for number in range(int(start), int(end) + 1):
                lines[number] = max(lines.get(number, 0), int(count))
    return files


def main():
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    files = read_profile(module_name())
    root = ET.Element("coverage")
    ET.SubElement(ET.SubElement(root, "sources"), "source").text = os.getcwd()
    classes = ET.SubElement(ET.SubElement(ET.SubElement(root, "packages"), "package", name="go"), "classes")
    for path, lines in sorted(files.items()):
        cls = ET.SubElement(classes, "class", name=path, filename=path)
        ET.SubElement(cls, "methods")
        container = ET.SubElement(cls, "lines")
        for number, count in sorted(lines.items()):
            ET.SubElement(container, "line", number=str(number), hits=str(count))
    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    ET.ElementTree(root).write(OUTPUT, encoding="utf-8", xml_declaration=True)
    print(f"{OUTPUT}: {len(files)} Go files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
