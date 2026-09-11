"""Test doubles. The fake model records what it was asked and returns scripted content."""

from __future__ import annotations

import json
import os
import sys
import uuid
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from app.config import Settings  # noqa: E402
from app.main import create_app  # noqa: E402
from app.model_client import ModelUnavailable  # noqa: E402

TOKEN = "test-token-0123456789abcdef"


def settings(**overrides: Any) -> Settings:
    base = dict(
        service_token=TOKEN,
        ollama_url="http://127.0.0.1:1",
        model="fake-model",
        timeout_seconds=1.0,
        seed=42,
        num_predict=700,
        num_ctx=8192,
        max_concurrency=5,
        queue_wait_seconds=0.2,
        default_min_confidence=0.70,
    )
    base.update(overrides)
    return Settings(**base)


class FakeModel:
    """Scripted outputs; each call pops the next. A str is returned as content, an Exception is raised."""

    def __init__(self, outputs: list[Any] | None = None, *, available: bool = True) -> None:
        self.outputs = list(outputs or [])
        self.calls: list[dict[str, Any]] = []
        self.available = available

    async def chat(self, *, system: str, user: str, output_schema: dict[str, Any], options: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        self.calls.append({"system": system, "user": user, "schema": output_schema, "options": options})
        if not self.outputs:
            raise AssertionError("fake model has no scripted output left")
        nxt = self.outputs.pop(0)
        if isinstance(nxt, Exception):
            raise nxt
        if isinstance(nxt, dict):
            nxt = json.dumps(nxt, ensure_ascii=False)
        return nxt, {"eval_count": 1}

    async def info(self) -> dict[str, Any]:
        if not self.available:
            raise ModelUnavailable("ConnectError")
        return {"name": "fake-model", "digest": "abc123def456", "quantization": "Q4_K_M"}


def good_output(**overrides: Any) -> dict[str, Any]:
    """A schema-valid model output (without the service-stamped fields)."""
    out: dict[str, Any] = {
        "detected_language": "en",
        "rationale": "Customer commits to pay next Thursday.",
        "contains_suspicious_instructions": False,
        "extracted": {
            "mentioned_amount_text": "1500",
            "mentioned_amount_numeric": "1500.000",
            "mentioned_currency": "JOD",
            "mentioned_date_text": "18 September",
            "mentioned_date_iso": "2026-09-18",
            "date_is_relative": False,
            "referenced_invoice_numbers": ["INV-1001"],
            "payment_method_mentioned": "bank_transfer",
            "payment_reference_text": None,
        },
        "classification": "promise_to_pay",
        "reason_code": "explicit_future_date_commitment",
        "confidence": 0.92,
        "secondary_classifications": [],
        "sentiment": "cooperative",
        "requires_human_review": False,
    }
    out.update(overrides)
    return out


def request_body(text: str = "We will transfer 1500 JOD on 18 September for INV-1001.", **overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "request_id": str(uuid.uuid4()),
        "message": {"text": text, "received_at": "2026-09-11T08:00:00Z", "declared_language": "en"},
        "context": {
            "today": "2026-09-11",
            "customer_display_name": "Petra Supplies",
            "case_status": "Open",
            "invoices_in_scope": [
                {"invoice_number": "INV-1001", "due_date": "2026-08-01", "open_amount": "1500.000", "currency": "JOD", "days_past_due": 41}
            ],
            "prior_promises": {"kept": 1, "broken": 0},
        },
        "options": {"min_confidence": 0.70},
    }
    body.update(overrides)
    return body


@pytest.fixture
def make_client():
    def _make(model: FakeModel, **overrides: Any) -> TestClient:
        app = create_app(settings(**overrides), client=model)
        return TestClient(app)

    return _make


@pytest.fixture
def auth() -> dict[str, str]:
    return {"X-Service-Token": TOKEN}


def live_ollama_available() -> bool:
    """The live-model tests run only when asked for and Ollama is reachable."""
    if os.environ.get("AI_LIVE_TESTS") != "1":
        return False
    import httpx

    try:
        return httpx.get(os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434") + "/api/tags", timeout=2).status_code == 200
    except httpx.HTTPError:
        return False
