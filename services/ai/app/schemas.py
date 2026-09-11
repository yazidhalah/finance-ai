"""Schema loading and validation (AI-04, AI-110). The files under schemas/ are the contract."""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker

SCHEMA_DIR = Path(__file__).resolve().parent.parent / "schemas"
PROMPT_DIR = Path(__file__).resolve().parent.parent / "prompts"

RESPONSE_SCHEMA_VERSION = "classify_customer_reply.v1"
BRIEFING_SCHEMA_VERSION = "daily_briefing.v1"


def load_schema(name: str) -> dict[str, Any]:
    with (SCHEMA_DIR / name).open(encoding="utf-8") as f:
        return json.load(f)


REQUEST_SCHEMA = load_schema("classify_customer_reply.request.v1.json")
RESPONSE_SCHEMA = load_schema("classify_customer_reply.response.v1.json")

_format_checker = FormatChecker()
REQUEST_VALIDATOR = Draft202012Validator(REQUEST_SCHEMA, format_checker=_format_checker)
RESPONSE_VALIDATOR = Draft202012Validator(RESPONSE_SCHEMA, format_checker=_format_checker)

BRIEFING_REQUEST_SCHEMA = load_schema("daily_briefing.request.v1.json")
BRIEFING_RESPONSE_SCHEMA = load_schema("daily_briefing.response.v1.json")
BRIEFING_REQUEST_VALIDATOR = Draft202012Validator(BRIEFING_REQUEST_SCHEMA, format_checker=_format_checker)
BRIEFING_RESPONSE_VALIDATOR = Draft202012Validator(BRIEFING_RESPONSE_SCHEMA, format_checker=_format_checker)

CLASSIFICATIONS: list[str] = RESPONSE_SCHEMA["properties"]["classification"]["enum"]
REASON_CODES: list[str] = RESPONSE_SCHEMA["properties"]["reason_code"]["enum"]


def validation_errors(validator: Draft202012Validator, instance: Any) -> list[str]:
    """Human-readable messages, deterministic order, never containing the instance's free text."""
    errors = sorted(validator.iter_errors(instance), key=lambda e: list(e.absolute_path))
    out: list[str] = []
    for e in errors:
        path = "/".join(str(p) for p in e.absolute_path) or "(root)"
        # e.message can embed the offending value; keep only the validator keyword and path.
        out.append(f"{path}: violates '{e.validator}'")
    return out


def model_output_schema() -> dict[str, Any]:
    """The subset of the response schema the model itself fills in, with the local $ref inlined
    so it can be handed to Ollama's structured-output `format` (D-3). `schema_version` and
    `model` are stamped by the service, never by the model (AI-102)."""
    s = copy.deepcopy(RESPONSE_SCHEMA)
    for key in ("$schema", "$id"):
        s.pop(key, None)
    props = s["properties"]
    props.pop("schema_version")
    props.pop("model")
    s["required"] = [r for r in s["required"] if r not in ("schema_version", "model")]
    # Make every advisory field required so the grammar always produces it (null allowed).
    for k in ("extracted", "secondary_classifications", "sentiment", "requires_human_review", "rationale"):
        if k not in s["required"]:
            s["required"].append(k)
    ex = props["extracted"]
    ex["required"] = list(ex["properties"].keys())
    # Property order is the order a grammar-constrained model writes them: language and rationale
    # first so the label is committed after the model has "read" the message, not before.
    order = ["detected_language", "rationale", "contains_suspicious_instructions", "extracted",
             "classification", "reason_code", "confidence", "secondary_classifications", "sentiment", "requires_human_review"]
    s["properties"] = {k: props[k] for k in order}
    s["required"] = order
    props = s["properties"]
    # Inline the $ref inside secondary_classifications.
    item_props = props["secondary_classifications"]["items"]["properties"]
    item_props["classification"] = {"type": "string", "enum": list(CLASSIFICATIONS)}
    # Grammar builders handle plain keywords best; descriptions and format hints are dropped.
    _strip(s, {"description", "format"})
    return s


def _strip(node: Any, keys: set[str]) -> None:
    if isinstance(node, dict):
        for k in list(node.keys()):
            if k in keys:
                del node[k]
            else:
                _strip(node[k], keys)
    elif isinstance(node, list):
        for item in node:
            _strip(item, keys)


def briefing_output_schema() -> dict[str, Any]:
    """The daily_briefing response minus the service-stamped fields, for Ollama's `format` (AI-82)."""
    s = copy.deepcopy(BRIEFING_RESPONSE_SCHEMA)
    for key in ("$schema", "$id"):
        s.pop(key, None)
    props = s["properties"]
    props.pop("schema_version")
    props.pop("model")
    order = ["language", "highlights", "narrative", "numbers_used", "reason_code", "confidence"]
    s["properties"] = {k: props[k] for k in order}
    s["required"] = order
    _strip(s, {"description", "format"})
    return s
