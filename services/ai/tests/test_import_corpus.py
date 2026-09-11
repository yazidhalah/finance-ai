"""Slice 21: the pilot corpus importer — redaction, refusal, agreement (doc 09 §5.1, T-91, T-92)."""
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "evaluations"))
import import_corpus as ic  # noqa: E402

SCRIPT = Path(__file__).resolve().parents[1] / "evaluations" / "import_corpus.py"


def row(**kw):
    base = {"text": "سنحول المبلغ يوم الخميس", "label": "promise_to_pay", "language": "ar", "provenance": "anonymized"}
    base.update(kw)
    return base


def test_clean_redacts_contact_details_and_keeps_the_label():
    item, reason = ic.clean(row(text="call me on 0791234567 or ahmad@example.com, IBAN JO94CBJO0010000000000131000302"), 1)
    assert reason is None
    assert "0791234567" not in item["text"] and "ahmad@example.com" not in item["text"] and "JO94" not in item["text"]
    assert item["text"].count("[REDACTED_") == 3
    assert item["redacted"] == {"[REDACTED_PHONE]": 1, "[REDACTED_EMAIL]": 1, "[REDACTED_IBAN]": 1}
    assert item["label"] == "promise_to_pay" and item["provenance"] == "anonymized"


def test_clean_refuses_bad_rows():
    assert ic.clean(row(provenance=""), 3)[1].startswith("row 3: provenance")
    assert "unknown label" in ic.clean(row(label="paid"), 4)[1]
    assert "unknown language" in ic.clean(row(language="fr"), 5)[1]
    assert "empty text" in ic.clean(row(text="  "), 6)[1]
    assert "nothing left" in ic.clean(row(text="0791234567"), 7)[1]
    assert "unknown label_2" in ic.clean(row(label_2="nope"), 8)[1]


def test_kappa_and_readiness():
    pairs = [("a", "a")] * 6 + [("b", "b")] * 2 + [("a", "b"), ("b", "a")]
    kappa = ic.cohen_kappa(pairs)
    assert abs(kappa - 0.524) < 0.001   # agreement 0.8, chance 0.58
    assert ic.cohen_kappa([("a", "a"), ("b", "b")]) == 1.0
    assert ic.cohen_kappa([]) is None
    items = [ic.clean(row(id=f"i{n}", label_2="promise_to_pay" if n % 5 else "dispute_raised"), n)[0] for n in range(1, 21)]
    r = ic.readiness(items)
    assert r["items"] == 20 and not r["enough_items"]
    assert r["double_labelled"] == 20 and r["agreement"] == 0.8
    assert "dispute_raised" in r["classes_missing"]
    assert r["consequential_below_minimum"]["dispute_raised"] == 0
    assert "dispute_raised 0%" in ic.render(r)   # the class humans disagree on is named


def test_import_is_atomic_and_writes_a_clean_corpus(tmp_path):
    source = tmp_path / "pilot.jsonl"
    out = tmp_path / "corpus.jsonl"
    good = row(id="p1", text="Payment sent today, ref TRX-1, my number 0791234567")
    bad = row(id="p2", provenance="")
    source.write_text(json.dumps(good) + "\n" + json.dumps(bad) + "\n", encoding="utf-8")
    refused = subprocess.run([sys.executable, str(SCRIPT), str(source), "--out", str(out)], capture_output=True, text=True)
    assert refused.returncode == 1 and "nothing written" in refused.stderr and not out.exists()

    source.write_text(json.dumps(good) + "\n", encoding="utf-8")
    ok = subprocess.run([sys.executable, str(SCRIPT), str(source), "--out", str(out)], capture_output=True, text=True)
    assert ok.returncode == 0, ok.stderr
    written = json.loads(out.read_text(encoding="utf-8").strip())
    assert "0791234567" not in written["text"] and "[REDACTED_PHONE]" in written["text"]
    assert "T-90 minimum 300: NOT MET" in ok.stdout
