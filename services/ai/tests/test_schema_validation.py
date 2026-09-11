"""AI-04 strict validation, one repair retry, AI-05 threshold, AI-40 review flags, AI-102 model block."""

from __future__ import annotations

import json

from app.schemas import RESPONSE_VALIDATOR, model_output_schema, validation_errors

from conftest import FakeModel, good_output, request_body

URL = "/internal/ai/v1/classify_customer_reply"


def _post(c, auth, body=None):
    return c.post(URL, json=body or request_body(), headers=auth)


def test_valid_output_is_stamped_and_schema_valid(make_client, auth):
    c = make_client(FakeModel([good_output()]))
    r = _post(c, auth)
    assert r.status_code == 200
    out = r.json()
    assert validation_errors(RESPONSE_VALIDATOR, out) == []
    assert out["schema_version"] == "classify_customer_reply.v1"
    assert out["model"] == {"name": "fake-model", "digest": "abc123def456", "prompt_version": "classify_customer_reply.v1", "latency_ms": out["model"]["latency_ms"]}
    assert r.headers["X-Ai-Validation-Status"] == "valid"
    assert r.headers["X-Ai-Attempts"] == "1"
    assert len(r.headers["X-Ai-Input-Hash"]) == 64


def test_first_invalid_then_valid_is_repaired(make_client, auth):
    bad = good_output(classification="mark_paid")  # outside the enum: a hijacked or confused model
    model = FakeModel([bad, good_output()])
    c = make_client(model)
    r = _post(c, auth)
    assert r.status_code == 200
    assert r.headers["X-Ai-Validation-Status"] == "repaired"
    assert r.headers["X-Ai-Attempts"] == "2"
    assert r.json()["classification"] == "promise_to_pay"
    repair_turn = model.calls[1]["user"]
    assert "not valid against the schema" in repair_turn
    assert "classification: violates 'enum'" in repair_turn
    assert "mark_paid" not in repair_turn  # the repair names paths and keywords, never values


def test_invalid_twice_falls_back_to_unclassified(make_client, auth):
    outputs = [
        "not json at all",
        good_output(confidence=1.2),
    ]
    c = make_client(FakeModel(outputs))
    r = _post(c, auth)
    assert r.status_code == 200
    assert r.headers["X-Ai-Validation-Status"] == "schema_invalid"
    out = r.json()
    assert validation_errors(RESPONSE_VALIDATOR, out) == []
    assert out["classification"] == "unclassified"
    assert out["reason_code"] == "below_confidence_threshold"
    assert out["confidence"] == 0.0
    assert out["requires_human_review"] is True
    assert out["model"]["name"] == "fake-model"


def test_extra_property_and_missing_required_are_invalid(make_client, auth):
    extra = good_output()
    extra["action"] = "mark_paid"
    missing = good_output()
    del missing["reason_code"]
    c = make_client(FakeModel([extra, missing]))
    r = _post(c, auth)
    assert r.headers["X-Ai-Validation-Status"] == "schema_invalid"
    assert r.json()["classification"] == "unclassified"


def test_prose_wrapped_json_is_salvaged(make_client, auth):
    c = make_client(FakeModel(["Here is the result:\n```json\n" + json.dumps(good_output()) + "\n```"]))
    r = _post(c, auth)
    assert r.status_code == 200 and r.json()["classification"] == "promise_to_pay"


def test_below_threshold_becomes_unclassified_without_a_guess(make_client, auth):
    c = make_client(FakeModel([good_output(confidence=0.55, secondary_classifications=[{"classification": "dispute_raised", "confidence": 0.4}])]))
    r = _post(c, auth)
    out = r.json()
    assert out["classification"] == "unclassified"
    assert out["reason_code"] == "below_confidence_threshold"
    assert out["confidence"] == 0.55
    assert out["secondary_classifications"] == []
    assert out["requires_human_review"] is True
    assert r.headers["X-Ai-Validation-Status"] == "valid"


def test_threshold_comes_from_the_request(make_client, auth):
    body = request_body()
    body["options"]["min_confidence"] = 0.95
    c = make_client(FakeModel([good_output(confidence=0.92)]))
    assert _post(c, auth, body).json()["classification"] == "unclassified"
    del body["options"]
    c = make_client(FakeModel([good_output(confidence=0.92)]))
    assert _post(c, auth, body).json()["classification"] == "promise_to_pay"


def test_ai40_forces_human_review(make_client, auth):
    cases = [
        good_output(classification="payment_claimed", reason_code="explicit_payment_statement"),
        good_output(classification="dispute_raised", reason_code="explicit_disagreement_with_amount"),
        good_output(classification="refusal_to_pay", reason_code="explicit_refusal"),
        good_output(classification="complaint_or_escalation", reason_code="expresses_dissatisfaction"),
        good_output(extracted={**good_output()["extracted"], "date_is_relative": True, "mentioned_date_iso": None}),
        good_output(secondary_classifications=[{"classification": "dispute_raised", "confidence": 0.6}]),
        good_output(contains_suspicious_instructions=True),
    ]
    for output in cases:
        c = make_client(FakeModel([output]))
        out = _post(c, auth).json()
        assert out["requires_human_review"] is True, output["classification"]
    c = make_client(FakeModel([good_output()]))
    assert _post(c, auth).json()["requires_human_review"] is False


def test_model_output_schema_is_a_strict_subset():
    s = model_output_schema()
    assert "schema_version" not in s["properties"] and "model" not in s["properties"]
    assert s["additionalProperties"] is False
    assert "$ref" not in json.dumps(s)
    assert s["properties"]["classification"]["enum"] == RESPONSE_VALIDATOR.schema["properties"]["classification"]["enum"]
    assert set(s["required"]) == set(s["properties"].keys())
