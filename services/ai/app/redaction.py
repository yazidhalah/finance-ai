"""AI-21 delimiter integrity, AI-22 length cap, AI-31 pre-flight redaction of untrusted text."""

from __future__ import annotations

import re
import secrets

MAX_TEXT_CHARS = 4000

# Arabic-Indic (٠-٩) and Eastern Arabic-Indic (۰-۹) digits map 1:1 onto ASCII so spans line up.
_DIGIT_MAP = {ord(c): str(i) for i, c in enumerate("٠١٢٣٤٥٦٧٨٩")}
_DIGIT_MAP.update({ord(c): str(i) for i, c in enumerate("۰۱۲۳۴۵۶۷۸۹")})

# Order matters: IBAN before card before phone, each on the digit-normalised copy.
_PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    ("[REDACTED_IBAN]", re.compile(r"\b[A-Z]{2}\d{2}(?:[ -]?[A-Z0-9]){11,30}\b")),
    ("[REDACTED_CARD]", re.compile(r"(?<!\d)(?:\d[ -]?){12}\d{1,7}(?!\d)")),
    ("[REDACTED_PHONE]", re.compile(
        r"(?<![\dA-Za-z])(?:\+?962[ -]?|00962[ -]?|0)7[789](?:[ -]?\d){7}(?!\d)"   # Jordanian mobiles
        r"|(?<![\dA-Za-z])\+\d{1,3}(?:[ -]?\d){7,13}(?!\d)"                          # any international form
        r"|(?<![\dA-Za-z])0[2-6](?:[ -]?\d){7}(?!\d)"                                # Jordanian landlines
    )),
]


def new_delimiter() -> str:
    """A per-call random tag (AI-21). 128 bits: unguessable, and unforgeable by the text."""
    return f"DATA_{secrets.token_hex(16)}"


def strip_delimiter(text: str, delimiter: str) -> str:
    return text.replace(delimiter, "")


def redact(text: str) -> tuple[str, dict[str, int]]:
    """Replace IBAN-, card- and phone-like patterns. Returns the text and per-kind counts."""
    normalised = text.translate(_DIGIT_MAP)
    counts: dict[str, int] = {}
    out = text
    # Apply in order on progressively-redacted text; the replacement tokens contain no digits,
    # so later patterns cannot match inside an earlier replacement.
    for token, pattern in _PATTERNS:
        spans = [m.span() for m in pattern.finditer(normalised)]
        if not spans:
            continue
        counts[token] = len(spans)
        pieces: list[str] = []
        last = 0
        for start, end in spans:
            pieces.append(out[last:start])
            pieces.append(token)
            last = end
        pieces.append(out[last:])
        out = "".join(pieces)
        normalised = out.translate(_DIGIT_MAP)
    return out, counts


def truncate(text: str, limit: int = MAX_TEXT_CHARS) -> tuple[str, bool]:
    if len(text) <= limit:
        return text, False
    return text[:limit], True


def normalise_digits(text: str) -> str:
    """Arabic-Indic digits become ASCII digits. The stored message keeps the original; the model
    sees ASCII because a 4B model under a JSON grammar degenerates when copying ٠-٩ (observed
    in the slice 9 evaluation), and amounts still read as the customer wrote them."""
    return text.translate(_DIGIT_MAP)


def prepare_untrusted(text: str, delimiter: str) -> tuple[str, bool, dict[str, int]]:
    """AI-21 → digit normalisation → AI-31 → AI-22, in that order. Returns (text, truncated, redaction_counts)."""
    stripped = normalise_digits(strip_delimiter(text, delimiter))
    redacted, counts = redact(stripped)
    capped, truncated = truncate(redacted)
    return capped, truncated, counts
