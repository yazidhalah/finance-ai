"""Slice 30: T-107 determinism in the evaluation harness, and the T-109 corpus-proposal export handed to the importer."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "evaluations"))
import import_corpus as ic  # noqa: E402
from evaluations import evaluate  # noqa: E402


def _row(id_, predicted, confidence=0.9, reason="explicit_future_date_commitment", extracted=None, status="valid"):
    return {"id": id_, "predicted": predicted, "confidence": confidence, "reason_code": reason, "extracted": extracted or {}, "validation_status": status}


def test_determinism_sample_spreads_through_the_corpus():
    items = [{"id": f"i{n}", "language": "ar" if n < 50 else "en"} for n in range(100)]
    sample = evaluate.determinism_sample(items, 10)
    assert len(sample) == 10
    assert {i["language"] for i in sample} == {"ar", "en"}   # not the first ten, which are all Arabic
    assert evaluate.determinism_sample(items, 0) == []


def test_determinism_metrics_reports_every_difference_and_never_averages():
    sample = [{"id": "a"}, {"id": "b"}, {"id": "c"}]
    run1 = [_row("a", "promise_to_pay"), _row("b", "acknowledgement", 0.8), _row("c", "dispute_raised", extracted={"x": 1})]
    run2 = [_row("a", "promise_to_pay"), _row("b", "acknowledgement", 0.8), _row("c", "dispute_raised", extracted={"x": 1})]
    same = evaluate.determinism_metrics(sample, [run1, run2, run1])
    assert same["pass"] and same["identical"] == 3 and same["differing"] == []

    run3 = [_row("a", "promise_to_pay", 0.91), _row("b", "acknowledgement", 0.8), {"id": "c", "validation_status": "unavailable:timeout"}]
    diff = evaluate.determinism_metrics(sample, [run1, run2, run3])
    assert not diff["pass"]
    assert diff["identical"] == 1
    assert [d["id"] for d in diff["differing"]] == ["a", "c"]     # a confidence that moved, and an unavailable run, are both defects
    assert diff["differing"][1]["outputs"][2]["predicted"] == "null"

    # An empty sample cannot pass: T-107 is not satisfied by not running it.
    assert not evaluate.determinism_metrics([], [[], [], []])["pass"]


def test_report_renders_the_determinism_gate(tmp_path):
    report = {
        "title": "t", "date": "2026-09-12",
        "environment": {"name": "m", "digest": "d", "prompt_version": "v", "prompt_sha256": "s", "schema_version": "x", "seed": 7, "num_predict": 1, "num_ctx": 1, "hardware": "h"},
        "labelled": {"n": 1, "macro_f1": 1.0, "dispute_recall": 1.0, "promise_precision": 1.0, "payment_claimed_review_rate": None, "schema_valid_rate": 1.0, "repaired": 0, "schema_invalid": 0,
                     "latency_p95_s": 1.0, "latency_p50_s": 1.0, "accuracy_at_threshold": 1.0, "accuracy_raw": 1.0, "unavailable": 0, "per_label": {}, "by_language": {},
                     "extraction": {"matched": 0, "checked": 0, "misses": []}, "confusions": []},
        "injection": {"n": 0, "all_on_schema": True, "payment_claimed_count": 0, "confident_promise_count": 0, "flagged_suspicious": 0, "human_review": 0, "labels": {}},
        "determinism": {"n": 2, "runs": 3, "identical": 1, "pass": False, "differing": [{"id": "z", "outputs": [{"predicted": "a", "confidence": "0.9", "reason_code": "r"}, None, {"predicted": "b", "confidence": "0.7", "reason_code": "r"}]}]},
        "results_file": "r.json",
    }
    text = evaluate.render(report)
    assert "| T-107 | determinism: identical output across 3 runs | 1/2 identical | FAIL |" in text
    assert "- z: a 0.9 r | unavailable | b 0.7 r" in text
    assert "reported, not averaged" in text
    report["determinism"] = None
    assert "not run (`--determinism N`)" in evaluate.render(report)


def test_corpus_proposals_export_is_accepted_by_the_importer(tmp_path):
    # The exact shape FinanceAi.Migrator corpus-proposals writes (slice 30, T-109): the importer redacts and keeps the label.
    csv = (
        "id,text,label,language,provenance,note,expect\n"
        'h-0123456789ab,"رح نحول المبلغ بعد العيد، اتصل على 0791234567",promise_to_pay,ar,consented,"consent: letter 2026-09-01; decision: edited; ai: promise_to_pay (0.910)","{""amount_numeric"":""1200.000"",""date_iso"":""2026-09-22""}"\n'
        "h-0123456789ac,We will look into it,acknowledgement,en,consented,consent: letter; decision: rejected; ai: payment_claimed (0.850),\n"
    )
    path = tmp_path / "proposals.csv"
    path.write_text(csv, encoding="utf-8")
    rows = ic.read_rows(path)
    items = []
    for n, row in enumerate(rows, 1):
        item, error = ic.clean(row, n)
        assert error is None, error
        items.append(item)
    assert items[0]["label"] == "promise_to_pay" and items[0]["provenance"] == "consented"
    assert "0791234567" not in items[0]["text"] and "[REDACTED_PHONE]" in items[0]["text"]
    assert items[0]["expect"] == {"amount_numeric": "1200.000", "date_iso": "2026-09-22"}
    assert items[1]["label"] == "acknowledgement" and "expect" not in items[1]
