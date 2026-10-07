#!/usr/bin/env python3
"""Fail on an NSG security rule description longer than ARM accepts (140 characters).

`az bicep build` does NOT catch this: it is preflight validation, not compilation. Without
this check the failure surfaces twenty minutes into a deployment, after the resource
groups, the registry and the scale set have already been created. Cheap here, expensive
there.

Run from anywhere: python3 scripts/ci/check-nsg-descriptions.py
"""
import glob
import os
import re
import sys
from pathlib import Path

os.chdir(Path(__file__).resolve().parents[2])
IN_ACTIONS = os.environ.get("GITHUB_ACTIONS") == "true"
LIMIT = 140

bad = 0
for path in sorted(glob.glob("infra/**/*.bicep", recursive=True)):
    src = open(path, encoding="utf-8").read()
    for m in re.finditer(r"description:\s*'((?:[^'\\]|\\.)*)'", src):
        value = m.group(1).replace("\\'", "'")
        if len(value) > LIMIT:
            line = src[:m.start()].count("\n") + 1
            message = f"NSG rule description is {len(value)} chars, limit is {LIMIT}"
            print(f"::error file={path},line={line}::{message}" if IN_ACTIONS else f"{path}:{line}: error: {message}")
            bad += 1
sys.exit(1 if bad else 0)
