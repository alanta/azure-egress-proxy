#!/usr/bin/env python3
"""Fail when the Go line differs between go.mod, Dockerfiles and setup-go steps.

The Go line is declared in proxy/go.mod, proxy/Dockerfile and every setup-go step in the
workflows. Dependabot bumps the Dockerfile tag on its own and cannot bump go.mod, so a PR
could ship the container on one Go line while govulncheck and the release binaries use
another. Versions are compared at major.minor: 1.25 and 1.25.14 agree.

Run from anywhere: python3 scripts/ci/check-go-toolchain.py
"""
import glob
import os
import re
import sys
from pathlib import Path

os.chdir(Path(__file__).resolve().parents[2])
IN_ACTIONS = os.environ.get("GITHUB_ACTIONS") == "true"

VERSION = r"(\d+)\.(\d+)(?:\.\d+)?"
SOURCES = [
    (["proxy/go.mod"], rf"^(?:go|toolchain go)\s*{VERSION}\s*$"),
    (glob.glob("**/Dockerfile*", recursive=True), rf"golang:{VERSION}"),
    (glob.glob(".github/workflows/*.y*ml"), rf"go-version:\s*['\"]?{VERSION}"),
]


def error(path, line, message):
    print(f"::error file={path},line={line}::{message}" if IN_ACTIONS else f"{path}:{line}: error: {message}")


found = []
for paths, pattern in SOURCES:
    for path in sorted(paths):
        for number, line in enumerate(open(path, encoding="utf-8"), 1):
            for m in re.finditer(pattern, line.strip()):
                found.append((path, number, f"{m.group(1)}.{m.group(2)}"))

if not any(path == "proxy/go.mod" for path, _, _ in found):
    error("proxy/go.mod", 1, "No go directive found.")
    sys.exit(1)

lines = sorted({v for _, _, v in found})
for path, number, v in found:
    print(f"{path}:{number}: Go {v}")
if len(lines) > 1:
    for path, number, v in found:
        error(path, number, f"Go {v} here; the repository declares {', '.join(lines)}. "
                            "Move every declaration together.")
    sys.exit(1)
print(f"All {len(found)} declarations are on Go {lines[0]}.")
