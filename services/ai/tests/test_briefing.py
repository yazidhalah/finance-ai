"""daily_briefing: AI-80 (figures in, as strings), AI-81 (numerals out must trace), AI-82 (schema), AI-83 (per language)."""

from __future__ import annotations

import uuid

from app import briefing
from app.schemas import BRIEFING_RESPONSE_VALIDATOR, validation_errors

from conftest import FakeModel

URL = "/internal/ai/v1/daily_briefing"


def metrics():
    return {
        "total_overdue": {"amount": "47350.750", "currency": "JOD"},
        "overdue_change": {"amount": "-1200.000", "currency": "JOD"},
        "collected_yesterday": {"amount": "3200.000", "currency": "JOD"},
        "promises_due_today": {"count": "4", "amount": {"amount": "11500.000", "currency": "JOD"}},
        "promises_broken_yesterday": "1",
        "new_disputes": "2",
        "disputes_breaching_sla": "1",
        "queue_size": "41",
        "unverified_payment_claims": "3",
        "unmatched_replies": "0",
        "replies_needing_a_human": "5",
        "pending_ai_suggestions": "2",
        "top_cases": [{"customer_name": "Petra Supplies", "amount": {"amount": "8200.000", "currency": "JOD"}, "days_past_due": "62", "status": "InProgress"}],
    }


def request_body(language="en", **overrides):
    body = {"request_id": str(uuid.uuid4()), "language": language, "date": "2026-09-11", "company_display_name": "Live Check", "metrics": metrics()}
    body.update(overrides)
    return body


def output(narrative, highlights=None, language="en", numbers_used=None):
    return {
        "language": language,
        "highlights": highlights or [],
        "narrative": narrative,
        "numbers_used": numbers_used or ["totalOverdue"],
        "reason_code": "generated_from_metrics",
        "confidence": 0.9,
    }


def test_numerals_that_trace_are_accepted():
    ok = "You have 4 promises due today worth 11,500.000 JOD; 47350.75 JOD is overdue, down 1200 since yesterday. Petra Supplies is 62 days past due on 8200.000. Date 2026-09-11."
    assert briefing.untraceable_numerals(ok, ["١ dispute breaching SLA", "٤١ cases in the queue"], metrics(), "2026-09-11") == []


def test_numerals_that_do_not_trace_are_caught():
    bad = "Roughly 47,000 JOD is overdue (12% more than last week); 7 promises are due today."
    assert briefing.untraceable_numerals(bad, [], metrics(), "2026-09-11") == ["47,000", "12", "7"]


def test_guard_failure_gets_one_repair_then_is_reported(make_client, auth):
    model = FakeModel([output("About 47,000 JOD overdue."), output("47350.750 JOD is overdue; 4 promises are due today.", ["4 promises due"])])
    c = make_client(model)
    r = c.post(URL, json=request_body(), headers=auth)
    assert r.status_code == 200
    assert r.headers["X-Ai-Validation-Status"] == "repaired"
    assert "not among the figures: 47,000" in model.calls[1]["user"]
    assert validation_errors(BRIEFING_RESPONSE_VALIDATOR, r.json()) == []

    c = make_client(FakeModel([output("About 47,000 JOD overdue."), output("Still about 47,000.")]))
    r = c.post(URL, json=request_body(), headers=auth)
    assert r.headers["X-Ai-Validation-Status"] == "rejected_by_guard"   # the backend discards it (AI-81); the service reports why
    assert r.json()["narrative"] == "Still about 47,000."


def test_language_mismatch_is_repaired(make_client, auth):
    model = FakeModel([output("4 promises due today.", language="en"), output("لديك 4 وعود دفع مستحقة اليوم.", language="ar")])
    c = make_client(model)
    r = c.post(URL, json=request_body(language="ar"), headers=auth)
    assert r.json()["language"] == "ar"
    assert r.headers["X-Ai-Validation-Status"] == "repaired"


def test_request_is_figures_only(make_client, auth):
    model = FakeModel([output("4 promises due today.")])
    c = make_client(model)
    body = request_body()
    body["metrics"]["customer_email"] = "x@example.com"
    assert c.post(URL, json=body, headers=auth).status_code == 400
    body = request_body()
    body["metrics"]["queue_size"] = 41   # numbers travel as strings (AI-80)
    assert c.post(URL, json=body, headers=auth).status_code == 400
    assert c.post(URL, json=request_body(), headers=auth).status_code == 200
    assert "47350.750" in model.calls[0]["user"] and "Petra Supplies" in model.calls[0]["user"]


def test_schema_escape_falls_back(make_client, auth):
    c = make_client(FakeModel(['{"narrative": 5}', "not json"]))
    r = c.post(URL, json=request_body(), headers=auth)
    assert r.headers["X-Ai-Validation-Status"] == "schema_invalid"
    assert r.json()["reason_code"] == "insufficient_data"
    assert validation_errors(BRIEFING_RESPONSE_VALIDATOR, r.json()) == []


def test_requires_token(make_client):
    c = make_client(FakeModel())
    assert c.post(URL, json=request_body()).status_code == 401
