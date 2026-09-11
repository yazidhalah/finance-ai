"""daily_briefing (doc 07 §6.3). The input is already-computed figures as strings (AI-80); the output is prose
whose every numeral must be one of them (AI-81). The backend runs the mandatory guard; this module runs the
same check first so a slip gets one repair retry before a human sees "no summary today"."""

from __future__ import annotations

import hashlib
import json
import re
import time
from dataclasses import dataclass, field
from typing import Any

from .model_client import ModelClient
from .schemas import BRIEFING_RESPONSE_VALIDATOR, BRIEFING_SCHEMA_VERSION, PROMPT_DIR, briefing_output_schema, validation_errors

OPERATION = "daily_briefing"
PROMPT_VERSION = "daily_briefing.v1"
_PROMPT_TEMPLATE = (PROMPT_DIR / "daily_briefing" / "v1.md").read_text(encoding="utf-8")
PROMPT_SHA256 = hashlib.sha256(_PROMPT_TEMPLATE.encode("utf-8")).hexdigest()[:16]
_OUTPUT_SCHEMA = briefing_output_schema()

_DIGITS = {ord(c): str(i) for i, c in enumerate("٠١٢٣٤٥٦٧٨٩")}
_DIGITS.update({ord(c): str(i) for i, c in enumerate("۰۱۲۳۴۵۶۷۸۹")})
_NUMERAL = re.compile(r"\d{4}[-/]\d{2}[-/]\d{2}|\d+(?:[.,]\d+)*")


@dataclass
class Outcome:
    body: dict[str, Any]
    validation_status: str          # valid | repaired | schema_invalid | rejected_by_guard
    attempts: int
    latency_ms: int
    input_hash: str
    untraceable: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)


def system_prompt() -> str:
    return _PROMPT_TEMPLATE.split("<!--", 1)[-1].split("-->", 1)[-1].strip()


def user_message(request: dict[str, Any]) -> str:
    lines = ["## Figures (computed by the system; copy numbers exactly)", f"language: {request['language']}", f"date: {request['date']}"]
    if request.get("company_display_name"):
        lines.append(f"company: {request['company_display_name']}")
    lines.append("metrics:")
    lines.append(json.dumps(request["metrics"], ensure_ascii=False, indent=1))
    lines.append("")
    lines.append("Write the briefing now as the JSON object.")
    return "\n".join(lines)


def allowed_numerals(metrics: dict[str, Any], date: str) -> set[str]:
    """Every string a numeral in the prose may equal, after canonicalisation."""
    out: set[str] = set()

    def add(value: str) -> None:
        c = _canonical(value)
        out.add(c)
        if "." in c:
            out.add(c.split(".", 1)[0])

    def walk(node: Any) -> None:
        if isinstance(node, dict):
            for v in node.values():
                walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)
        elif isinstance(node, str) and re.fullmatch(r"-?\d+(?:\.\d+)?", node):
            add(node.lstrip("-"))

    walk(metrics)
    out.update({date, date.replace("-", "/"), date.split("-")[0]})   # the whole date or the year; never a bare day or month
    return out


def _canonical(token: str) -> str:
    t = token.translate(_DIGITS)
    if "," in t and "." in t:
        t = t.replace(",", "")
    elif "," in t:
        parts = t.split(",")
        t = "".join(parts) if all(len(p) == 3 for p in parts[1:]) else t.replace(",", ".")
    if "." in t:
        t = t.rstrip("0").rstrip(".") or "0"
    t = t.lstrip("0") or "0"
    return t


def untraceable_numerals(narrative: str, highlights: list[str], metrics: dict[str, Any], date: str) -> list[str]:
    allowed = allowed_numerals(metrics, date)
    bad: list[str] = []
    for text in [narrative, *highlights]:
        for m in _NUMERAL.finditer(text.translate(_DIGITS)):
            tok = m.group(0)
            if _canonical(tok) not in allowed and tok not in allowed:
                bad.append(tok)
    return bad


def _stamp(obj: dict[str, Any], model_info: dict[str, Any], latency_ms: int) -> dict[str, Any]:
    out = dict(obj)
    out["schema_version"] = BRIEFING_SCHEMA_VERSION
    out["model"] = {"name": model_info["name"], "digest": model_info.get("digest", ""), "prompt_version": PROMPT_VERSION, "latency_ms": latency_ms}
    return out


def _parse(content: str) -> dict[str, Any] | None:
    content = content.strip().strip("`")
    if content.startswith("json"):
        content = content[4:]
    try:
        obj = json.loads(content)
    except json.JSONDecodeError:
        start, end = content.find("{"), content.rfind("}")
        if start < 0 or end <= start:
            return None
        try:
            obj = json.loads(content[start : end + 1])
        except json.JSONDecodeError:
            return None
    return obj if isinstance(obj, dict) else None


async def narrate(request: dict[str, Any], client: ModelClient, model_info: dict[str, Any], settings) -> Outcome:
    started = time.perf_counter()
    system = system_prompt()
    user = user_message(request)
    input_hash = hashlib.sha256((system + "\n" + user).encode("utf-8")).hexdigest()
    options = {"temperature": 0, "top_p": 1, "seed": settings.seed, "num_predict": max(settings.num_predict, 900), "num_ctx": settings.num_ctx}

    errors: list[str] = []
    attempts = 0
    status = "schema_invalid"
    candidate: dict[str, Any] | None = None
    untraceable: list[str] = []
    user_turn = user
    while attempts < 2:
        attempts += 1
        content, _ = await client.chat(system=system, user=user_turn, output_schema=_OUTPUT_SCHEMA, options=options)
        parsed = _parse(content)
        probe = _stamp(parsed, model_info, 0) if parsed is not None else None
        errs = validation_errors(BRIEFING_RESPONSE_VALIDATOR, probe) if probe is not None else ["(root): not a JSON object"]
        if errs:
            errors.extend(errs)
            user_turn = user + "\n\nYour previous output was not valid against the schema: " + "; ".join(errs[:6]) + ". Return only the corrected JSON object."
            continue
        if parsed is not None and parsed.get("language") != request["language"]:
            errors.append("language: mismatch")
            user_turn = user + f"\n\nWrite in the requested language ({request['language']}) and set `language` accordingly."
            continue
        bad = untraceable_numerals(parsed["narrative"], parsed.get("highlights", []), request["metrics"], request["date"])   # type: ignore[index]
        if bad:
            untraceable = bad
            status = "rejected_by_guard"
            candidate = parsed
            user_turn = user + "\n\nYour previous text contained numbers that are not among the figures: " + ", ".join(bad[:6]) + ". Use only the figures given, copied exactly. Return only the JSON object."
            continue
        candidate = parsed
        untraceable = []
        status = "valid" if attempts == 1 else "repaired"
        break

    latency_ms = int((time.perf_counter() - started) * 1000)
    if candidate is None:
        body = _stamp(
            {"language": request["language"], "narrative": "-", "highlights": [], "numbers_used": [], "confidence": 0.0, "reason_code": "insufficient_data"},
            model_info,
            latency_ms,
        )
        status = "schema_invalid"
    else:
        body = _stamp(candidate, model_info, latency_ms)
    return Outcome(body=body, validation_status=status, attempts=attempts, latency_ms=latency_ms, input_hash=input_hash, untraceable=untraceable, errors=errors)
