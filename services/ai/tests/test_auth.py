"""AI-101 token, AI-100 health endpoints, AI-10 degradation, AI-104 fast rejection, AI-105 no state."""

from __future__ import annotations

import asyncio
import re
from pathlib import Path

from app.model_client import ModelUnavailable

from conftest import FakeModel, good_output, request_body


def test_classify_requires_token(make_client):
    c = make_client(FakeModel([good_output()]))
    assert c.post("/internal/ai/v1/classify_customer_reply", json=request_body()).status_code == 401
    assert c.post("/internal/ai/v1/classify_customer_reply", json=request_body(), headers={"X-Service-Token": "wrong"}).status_code == 401
    assert c.get("/model-info").status_code == 401


def test_health_and_ready_need_no_model_call(make_client):
    c = make_client(FakeModel())
    assert c.get("/health").json()["status"] == "ok"
    assert c.get("/ready").status_code == 200
    down = make_client(FakeModel(available=False))
    assert down.get("/ready").status_code == 503


def test_model_info_carries_versions(make_client, auth):
    c = make_client(FakeModel())
    info = c.get("/model-info", headers=auth).json()
    assert info["prompt_version"] == "classify_customer_reply.v1"
    assert info["schema_version"] == "classify_customer_reply.v1"
    assert info["options"]["temperature"] == 0 and info["options"]["seed"] == 42
    assert re.fullmatch(r"[0-9a-f]{16}", info["prompt_sha256"])


def test_model_unavailable_is_503_not_a_guess(make_client, auth):
    c = make_client(FakeModel([ModelUnavailable("ReadTimeout")]))
    r = c.post("/internal/ai/v1/classify_customer_reply", json=request_body(), headers=auth)
    assert r.status_code == 503
    assert r.json() == {"code": "model_unavailable", "reason": "ReadTimeout"}


def test_invalid_request_is_400_with_paths_only(make_client, auth):
    c = make_client(FakeModel([good_output()]))
    body = request_body()
    body["context"]["invoices_in_scope"][0]["open_amount"] = "1500.00"  # two decimals: not money as the system writes it
    body["message"]["email"] = "someone@example.com"                    # additionalProperties: false
    r = c.post("/internal/ai/v1/classify_customer_reply", json=body, headers=auth)
    assert r.status_code == 400
    errs = r.json()["errors"]
    assert any(e.startswith("context/invoices_in_scope/0/open_amount") for e in errs)
    assert any(e.startswith("message") and "additionalProperties" in e for e in errs)
    assert "someone@example.com" not in r.text


def test_busy_is_rejected_fast(make_client, auth):
    class Slow(FakeModel):
        async def chat(self, **kw):
            await asyncio.sleep(1.0)
            return await super().chat(**kw)

    slow = Slow([good_output(), good_output()])
    c = make_client(slow, max_concurrency=1, queue_wait_seconds=0.05)
    import threading

    results: list[int] = []

    def go():
        results.append(c.post("/internal/ai/v1/classify_customer_reply", json=request_body(), headers=auth).status_code)

    t1, t2 = threading.Thread(target=go), threading.Thread(target=go)
    t1.start()
    t2.start()
    t1.join()
    t2.join()
    assert sorted(results) == [200, 429]


def test_service_has_no_database_or_send_capability():
    """AI-11 / AI-105: the only outbound dependency is httpx to Ollama."""
    root = Path(__file__).resolve().parent.parent / "app"
    source = "\n".join(p.read_text(encoding="utf-8") for p in root.glob("*.py"))
    for forbidden in ("psycopg", "asyncpg", "sqlalchemy", "smtplib", "aiosmtplib", "DATABASE_URL", "ConnectionStrings"):
        assert forbidden not in source, forbidden
    req = (root.parent / "requirements.txt").read_text(encoding="utf-8")
    assert "psycopg" not in req and "sqlalchemy" not in req
