#!/usr/bin/env python3
"""Brings a pilot's labelled replies into the evaluation corpus (doc 09 §5.1, slice 21).

Input: JSONL or CSV, one reply per row, with `text`, `label`, `language` (ar | en | ar_latin | mixed), `provenance`
(synthetic | anonymized | consented — T-92), and optionally `id`, `label_2` (a second annotator, T-91), `note`, `expect`.

Every text is redacted before it is written (IBAN, card and phone through the service's own redaction; email addresses
here) — a corpus file must never carry a real customer's contact details (SEC-90). Rows without provenance, with an
unknown label or language, or with nothing left after redaction are refused, and the whole import is refused with
them: a corpus is either clean or not written.

    evaluations/import_corpus.py pilot.jsonl --out evaluations/corpus/pilot.jsonl [--disagreements out.jsonl]
    evaluations/import_corpus.py --check evaluations/corpus/pilot.jsonl      # readiness against T-90 only

The readiness report states the T-90 targets (≥ 300 items; ~40 / 25 / 15 / 20 % MSA / dialect / Arabizi / English;
every class present; ≥ 15 for the consequential four) and T-91 agreement (percent and Cohen's kappa) when a second
label exists. It never blocks the import — the numbers are for the decision record.
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.redaction import redact  # noqa: E402

CLASSES = [
    "payment_claimed", "promise_to_pay", "partial_payment_offer", "payment_plan_request", "dispute_raised",
    "invoice_not_received", "information_request", "wrong_recipient", "out_of_office", "acknowledgement",
    "refusal_to_pay", "hardship_or_delay_notice", "complaint_or_escalation", "unrelated", "unclassified",
]
CONSEQUENTIAL = ["payment_claimed", "promise_to_pay", "dispute_raised", "refusal_to_pay"]
LANGUAGES = {"ar", "en", "ar_latin", "mixed"}
PROVENANCE = {"synthetic", "anonymized", "consented"}
# T-90 language mix. `ar` covers MSA and dialect (the corpus does not label register); Arabizi is `ar_latin`.
TARGET_MIX = {"ar": 0.65, "ar_latin": 0.15, "en": 0.20}
MINIMUM_ITEMS = 300
MINIMUM_CONSEQUENTIAL = 15

_EMAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")


def read_rows(path: Path) -> list[dict]:
    if path.suffix.lower() == ".csv":
        with path.open(encoding="utf-8-sig", newline="") as f:
            return [dict(r) for r in csv.DictReader(f)]
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def clean(row: dict, index: int) -> tuple[dict | None, str | None]:
    """One row → a corpus item, or a reason it was refused."""
    text = (row.get("text") or "").strip()
    label = (row.get("label") or "").strip()
    language = (row.get("language") or "").strip()
    provenance = (row.get("provenance") or "").strip()
    if not text:
        return None, f"row {index}: empty text"
    if label not in CLASSES:
        return None, f"row {index}: unknown label '{label}'"
    if language not in LANGUAGES:
        return None, f"row {index}: unknown language '{language}' (ar | en | ar_latin | mixed)"
    if provenance not in PROVENANCE:
        return None, f"row {index}: provenance must be synthetic | anonymized | consented (T-92), got '{provenance}'"
    redacted, counts = redact(text)
    redacted, emails = _EMAIL.subn("[REDACTED_EMAIL]", redacted)
    if emails:
        counts["[REDACTED_EMAIL]"] = emails
    if not re.sub(r"\[REDACTED_[A-Z]+\]", "", redacted).strip():
        return None, f"row {index}: nothing left after redaction"
    item = {
        "id": (row.get("id") or f"p-{index:04d}").strip(),
        "language": language,
        "text": redacted,
        "label": label,
        "provenance": provenance,
    }
    label_2 = (row.get("label_2") or "").strip()
    if label_2:
        if label_2 not in CLASSES:
            return None, f"row {index}: unknown label_2 '{label_2}'"
        item["label_2"] = label_2
    if row.get("note"):
        item["note"] = str(row["note"]).strip()
    expect = row.get("expect")
    if expect:
        item["expect"] = json.loads(expect) if isinstance(expect, str) else expect
    if counts:
        item["redacted"] = counts
    return item, None


def cohen_kappa(pairs: list[tuple[str, str]]) -> float | None:
    if not pairs:
        return None
    n = len(pairs)
    agree = sum(1 for a, b in pairs if a == b) / n
    first = Counter(a for a, _ in pairs)
    second = Counter(b for _, b in pairs)
    expected = sum(first[c] / n * second[c] / n for c in set(first) | set(second))
    return 1.0 if expected == 1 else (agree - expected) / (1 - expected)


def readiness(items: list[dict]) -> dict:
    n = len(items)
    languages = Counter(i["language"] for i in items)
    labels = Counter(i["label"] for i in items)
    pairs = [(i["label"], i["label_2"]) for i in items if i.get("label_2")]
    per_class_agreement = {}
    for c in CLASSES:
        both = [(a, b) for a, b in pairs if a == c or b == c]
        if both:
            per_class_agreement[c] = round(sum(1 for a, b in both if a == b) / len(both), 3)
    return {
        "items": n,
        "enough_items": n >= MINIMUM_ITEMS,
        "languages": {k: {"count": v, "share": round(v / n, 3)} for k, v in sorted(languages.items())} if n else {},
        "language_targets": TARGET_MIX,
        "classes_missing": [c for c in CLASSES if labels[c] == 0],
        "consequential_below_minimum": {c: labels[c] for c in CONSEQUENTIAL if labels[c] < MINIMUM_CONSEQUENTIAL},
        "labels": dict(sorted(labels.items())),
        "provenance": dict(Counter(i.get("provenance", "?") for i in items)),
        "double_labelled": len(pairs),
        "agreement": round(sum(1 for a, b in pairs if a == b) / len(pairs), 3) if pairs else None,
        "kappa": None if not pairs else round(cohen_kappa(pairs), 3),
        "agreement_by_class": per_class_agreement,
    }


def render(r: dict) -> str:
    lines = [f"items: {r['items']} (T-90 minimum {MINIMUM_ITEMS}: {'ok' if r['enough_items'] else 'NOT MET'})"]
    for lang, v in r["languages"].items():
        target = TARGET_MIX.get(lang)
        lines.append(f"  {lang:9} {v['count']:5} ({v['share']:.0%}){f' — target ~{target:.0%}' if target else ''}")
    if r["classes_missing"]:
        lines.append(f"classes with no example: {', '.join(r['classes_missing'])}")
    if r["consequential_below_minimum"]:
        lines.append("consequential classes under 15: " + ", ".join(f"{k}={v}" for k, v in r["consequential_below_minimum"].items()))
    lines.append("provenance: " + ", ".join(f"{k}={v}" for k, v in r["provenance"].items()))
    if r["double_labelled"]:
        lines.append(f"T-91 agreement over {r['double_labelled']} double-labelled items: {r['agreement']:.1%} (Cohen's kappa {r['kappa']})")
        weak = {k: v for k, v in r["agreement_by_class"].items() if v < 0.8}
        if weak:
            lines.append("  classes where humans agree < 80 % (the definition needs work before the model is held to it): " + ", ".join(f"{k} {v:.0%}" for k, v in weak.items()))
    else:
        lines.append("T-91: no second label — inter-annotator agreement not reported")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("source", help="JSONL or CSV of labelled replies, or an existing corpus with --check")
    parser.add_argument("--out", help="corpus JSONL to write")
    parser.add_argument("--disagreements", help="JSONL of items whose two labels differ (T-91)")
    parser.add_argument("--check", action="store_true", help="readiness report only; no writing, no redaction")
    args = parser.parse_args()

    rows = read_rows(Path(args.source))
    if args.check:
        print(render(readiness(rows)))
        return 0
    if not args.out:
        parser.error("--out is required unless --check")

    items: list[dict] = []
    refused: list[str] = []
    for index, row in enumerate(rows, start=1):
        item, reason = clean(row, index)
        if reason:
            refused.append(reason)
        else:
            items.append(item)
    ids = Counter(i["id"] for i in items)
    refused += [f"duplicate id '{k}' ({v} rows)" for k, v in ids.items() if v > 1]
    if refused:
        print("\n".join(refused), file=sys.stderr)
        print(f"import refused: {len(refused)} problem(s); nothing written", file=sys.stderr)
        return 1

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("".join(json.dumps(i, ensure_ascii=False) + "\n" for i in items), encoding="utf-8")
    redacted = sum(sum(i.get("redacted", {}).values()) for i in items)
    print(f"wrote {len(items)} items to {out} ({redacted} redactions)")
    if args.disagreements:
        dis = [i for i in items if i.get("label_2") and i["label_2"] != i["label"]]
        Path(args.disagreements).write_text("".join(json.dumps(i, ensure_ascii=False) + "\n" for i in dis), encoding="utf-8")
        print(f"wrote {len(dis)} disagreements to {args.disagreements}")
    print(render(readiness(items)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
