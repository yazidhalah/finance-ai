#!/usr/bin/env python3
"""Merges coverlet's cobertura files (one per test project) into per-assembly line coverage (slice 19 groundwork).

A line counts as covered when any suite covered it; generated code under obj/ is excluded. Stdlib only.
    infrastructure/coverage-report.py <cobertura.xml>... [--floor Domain=90 Infrastructure=80 Api=75]
Exits 1 when a floor is not met.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def main() -> int:
    files = [a for a in sys.argv[1:] if not a.startswith("--") and "=" not in a]
    floors = {a.split("=")[0]: float(a.split("=")[1]) for a in sys.argv[1:] if "=" in a}
    lines: dict[str, dict[tuple[str, int], bool]] = defaultdict(dict)   # assembly -> (file, line) -> hit
    for path in files:
        for package in ET.parse(path).getroot().iter("package"):
            assembly = package.get("name", "")
            for cls in package.iter("class"):
                filename = cls.get("filename", "")
                if "/obj/" in filename:
                    continue   # source-generated code (regex, OpenAPI XML comments) is not ours to cover
                for line in cls.iter("line"):
                    key = (filename, int(line.get("number", "0")))
                    lines[assembly][key] = lines[assembly].get(key, False) or int(line.get("hits", "0")) > 0
    failed = False
    print(f"{'assembly':32} {'lines':>8} {'covered':>8} {'%':>7}")
    for assembly in sorted(lines):
        total = len(lines[assembly]); hit = sum(1 for v in lines[assembly].values() if v)
        pct = 100.0 * hit / total if total else 0.0
        short = assembly.replace("FinanceAi.", "")
        floor = floors.get(short)
        mark = ""
        if floor is not None and pct < floor:
            mark = f"  < floor {floor:.0f}"; failed = True
        print(f"{assembly:32} {total:8} {hit:8} {pct:6.1f}%{mark}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
