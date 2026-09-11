#!/usr/bin/env python3
"""The license and notices check of SEC-68 / PRD-26 (slice 16).

Every direct dependency — NuGet PackageReferences, package.json dependencies, requirements.txt, the container images
in the Containerfiles and compose files — must appear by name in THIRD-PARTY-NOTICES.md, and its declared license
(read from the installed package metadata when available) must be on the permissive allowlist unless the notices mark
the package "Flagged". No third-party code: stdlib only.

    infrastructure/check-notices.py            # exit 1 with the offenders listed
    infrastructure/check-notices.py --self-test  # proves both failure modes fire
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTICES = os.path.join(ROOT, "THIRD-PARTY-NOTICES.md")

ALLOWED = {
    "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "PostgreSQL", "PSF-2.0", "Python-2.0",
    "Unlicense", "CC0-1.0", "BlueOak-1.0.0", "MIT AND Apache-2.0", "(MIT OR Apache-2.0)", "MIT OR Apache-2.0",
    "Apache-2.0 OR MIT", "(MIT OR CC0-1.0)", "MIT AND BSD-3-Clause",
}
# Licenses accepted only when the notices say "Flagged" beside the package (weak copyleft, file-scoped).
FLAGGABLE = {"MPL-2.0"}


def norm(expr: str | None) -> str | None:
    if not expr:
        return None
    expr = expr.strip()
    return {"Apache 2.0": "Apache-2.0", "Apache License 2.0": "Apache-2.0", "BSD": "BSD-3-Clause", "MIT License": "MIT",
            "BSD License": "BSD-3-Clause", "Apache Software License": "Apache-2.0"}.get(expr, expr)


def nuget_direct() -> dict[str, str | None]:
    out: dict[str, str | None] = {}
    home = os.path.expanduser("~/.nuget/packages")
    for csproj in glob.glob(os.path.join(ROOT, "apps", "api", "*", "*.csproj")) + glob.glob(os.path.join(ROOT, "tests", "*", "*", "*.csproj")):
        for ref in ET.parse(csproj).getroot().iter("PackageReference"):
            pid, ver = ref.get("Include"), ref.get("Version")
            if not pid:
                continue
            lic = None
            nuspec = os.path.join(home, pid.lower(), (ver or "").lower(), f"{pid.lower()}.nuspec")
            if os.path.exists(nuspec):
                text = open(nuspec, encoding="utf-8").read()
                m = re.search(r'<license type="expression">([^<]+)</license>', text)
                lic = norm(m.group(1)) if m else None
            out[pid] = lic
    return out


def npm_direct() -> dict[str, str | None]:
    out: dict[str, str | None] = {}
    for pkg in ("apps/web", "tests/e2e"):
        p = os.path.join(ROOT, pkg, "package.json")
        d = json.load(open(p, encoding="utf-8"))
        for name in list(d.get("dependencies", {})) + list(d.get("devDependencies", {})):
            meta = os.path.join(ROOT, pkg, "node_modules", *name.split("/"), "package.json")
            lic = None
            if os.path.exists(meta):
                m = json.load(open(meta, encoding="utf-8"))
                lic = m.get("license") if isinstance(m.get("license"), str) else (m.get("license") or {}).get("type") if isinstance(m.get("license"), dict) else None
                lic = norm(lic)
            out[name] = lic
    return out


def pip_direct() -> dict[str, str | None]:
    out: dict[str, str | None] = {}
    req = os.path.join(ROOT, "services", "ai", "requirements.txt")
    site = glob.glob(os.path.join(ROOT, "services", "ai", ".venv", "lib", "python*", "site-packages"))
    for line in open(req, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name = re.split(r"[=<>!~ ]", line, maxsplit=1)[0]
        lic = None
        for s in site:
            for meta in glob.glob(os.path.join(s, f"{name.replace('-', '_')}-*.dist-info", "METADATA")) + glob.glob(os.path.join(s, f"{name}-*.dist-info", "METADATA")):
                text = open(meta, encoding="utf-8", errors="replace").read()
                m = re.search(r"^License-Expression: (.+)$", text, re.M) or re.search(r"^Classifier: License :: OSI Approved :: (.+?) License$", text, re.M)
                if m:
                    lic = norm(m.group(1)); break
        out[name] = lic
    return out


def images() -> dict[str, str | None]:
    out: dict[str, str | None] = {}
    files = glob.glob(os.path.join(ROOT, "**", "Containerfile"), recursive=True) + glob.glob(os.path.join(ROOT, "infrastructure", "compose*.yml"))
    for f in files:
        if "node_modules" in f or "/.venv/" in f:
            continue
        for m in re.finditer(r"^\s*(?:FROM|image:)\s+([a-z0-9./-]+(?:/[a-z0-9._-]+)*):", open(f, encoding="utf-8").read(), re.M):
            if m.group(1).startswith("finance-ai/"):
                continue   # our own images
            out[m.group(1)] = None   # image licences are recorded by hand; presence is what is checked
    return out


def check(notices_text: str, deps: dict[str, dict[str, str | None]]) -> list[str]:
    problems: list[str] = []
    for kind, items in deps.items():
        for name, lic in sorted(items.items()):
            pattern = re.compile(r"^\|[^|]*`" + re.escape(name) + r"`[^|]*\|")   # a table row whose first cell names the package
            row = next((line for line in notices_text.splitlines() if pattern.search(line)), None)
            if row is None:
                problems.append(f"{kind}: `{name}` is not recorded in THIRD-PARTY-NOTICES.md")
                continue
            if lic is None:
                continue   # metadata not installed here; the notices row is the record
            if lic in ALLOWED:
                continue
            if lic in FLAGGABLE and "Flagged" in row:
                continue
            problems.append(f"{kind}: `{name}` declares licence '{lic}', which is not on the permissive allowlist (add it to the notices as Flagged only if it is file-scoped weak copyleft used unmodified)")
    return problems


def main() -> int:
    notices = open(NOTICES, encoding="utf-8").read()
    deps = {"nuget": nuget_direct(), "npm": npm_direct(), "pip": pip_direct(), "image": images()}
    if "--self-test" in sys.argv:
        broken = notices.replace("`Npgsql`", "`Npgsql-renamed`", 1)
        assert any("`Npgsql` is not recorded" in p for p in check(broken, deps)), "a removed row must fail"
        gpl = {"npm": {"left-pad": "GPL-3.0"}}
        assert any("GPL-3.0" in p for p in check(notices + "\n| `left-pad` | 1 | GPL-3.0 |", gpl)), "a copyleft licence must fail"
        mpl = {"pip": {"certifi": "MPL-2.0"}}
        assert check(notices, mpl) == [], "MPL-2.0 with a Flagged row must pass"
        assert check(notices.replace("**Flagged:**", ""), mpl) != [], "MPL-2.0 without Flagged must fail"
        print("check-notices self-test: ok")
        return 0
    problems = check(notices, deps)
    counted = sum(len(v) for v in deps.values())
    if problems:
        print("\n".join(problems), file=sys.stderr)
        print(f"check-notices: {len(problems)} problem(s) across {counted} direct dependencies", file=sys.stderr)
        return 1
    print(f"check-notices: {counted} direct dependencies recorded, licences within the allowlist")
    return 0


if __name__ == "__main__":
    sys.exit(main())
