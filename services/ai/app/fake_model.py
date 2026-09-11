"""A deterministic stand-in for Ollama, for end-to-end tests only (AI_FAKE_MODEL=1).

It answers with the same shapes the real model does, from keyword rules, so the Playwright suite can walk
T-129 / T-130 / T-131 without a GPU. It is never the production path: /model-info reports `fake: true`,
the digest is the literal string "fake", and the service logs a warning at startup.
"""

from __future__ import annotations

import json
import re
from typing import Any

from .model_client import ModelUnavailable

DIGEST = "fake"


class FakeOllama:
    name = "fake-model"

    async def info(self) -> dict[str, Any]:
        return {"name": self.name, "digest": DIGEST, "quantization": "none", "parameter_size": "0", "family": "fake", "fake": True}

    async def ready(self) -> bool:
        return True

    async def aclose(self) -> None:
        return None

    async def chat(self, *, system: str, user: str, output_schema: dict[str, Any], options: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        if "[ai-down]" in user:
            raise ModelUnavailable("ConnectError")   # T-131: the AI container is "stopped" for this organization
        if "## Figures" in user:
            return json.dumps(self._briefing(user), ensure_ascii=False), {"eval_count": 1}
        return json.dumps(self._classify(user), ensure_ascii=False), {"eval_count": 1}

    @staticmethod
    def _block(user: str) -> str:
        m = re.search(r"<(DATA_[0-9a-f]+)>\n(.*)\n</\1>", user, re.DOTALL)
        return m.group(2) if m else ""

    def _classify(self, user: str) -> dict[str, Any]:
        text = self._block(user).lower()
        invoices = re.findall(r"\b[A-Z]{2,}-\d+\b", self._block(user))
        amount = re.search(r"\b(\d{2,7})(?:\.\d{1,3})?\b", text)
        date = re.search(r"\b(20\d{2}-\d{2}-\d{2})\b", text)
        arabic = bool(re.search(r"[؀-ۿ]", text))
        base = {
            "detected_language": "ar" if arabic else "en",
            "rationale": "fake model: keyword rule",
            "contains_suspicious_instructions": "ignore" in text or "تجاهل" in text,
            "extracted": {
                "mentioned_amount_text": amount.group(0) if amount else None,
                "mentioned_amount_numeric": f"{int(amount.group(1))}.000" if amount else None,
                "mentioned_currency": "JOD" if amount else None,
                "mentioned_date_text": date.group(1) if date else ("next week" if "next week" in text else None),
                "mentioned_date_iso": date.group(1) if date else None,
                "date_is_relative": (date is None and "next week" in text) or None,
                "referenced_invoice_numbers": invoices[:5],
                "payment_method_mentioned": "bank_transfer" if "transfer" in text else None,
                "payment_reference_text": None,
            },
            "secondary_classifications": [],
            "sentiment": "neutral",
            "requires_human_review": False,
        }
        if any(k in text for k in ("already paid", "we paid", "i paid", "was paid", "دفعنا", "تم التحويل")):
            base.update(classification="payment_claimed", reason_code="explicit_payment_statement", confidence=0.96)
        elif any(k in text for k in ("will pay", "will transfer", "رح نحول", "سنسدد")):
            base.update(classification="promise_to_pay", reason_code="explicit_future_date_commitment", confidence=0.93)
        elif any(k in text for k in ("wrong", "dispute", "غلط")):
            base.update(classification="dispute_raised", reason_code="explicit_disagreement_with_amount", confidence=0.9)
        else:
            base.update(classification="acknowledgement", reason_code="acknowledges_without_commitment", confidence=0.8)
        return base

    @staticmethod
    def _briefing(user: str) -> dict[str, Any]:
        language = re.search(r"^language: (\w+)$", user, re.MULTILINE).group(1)   # type: ignore[union-attr]
        metrics = json.loads(user.split("metrics:\n", 1)[1].rsplit("\n\nWrite", 1)[0])
        overdue = metrics["total_overdue"]
        due = metrics["promises_due_today"]
        if language == "ar":
            narrative = f"لديك {due['count']} وعود دفع مستحقة اليوم بقيمة {due['amount']['amount']} {due['amount']['currency']}. إجمالي المتأخر {overdue['amount']} {overdue['currency']}، وعدد الملفات في قائمة التحصيل {metrics['queue_size']}."
        else:
            narrative = f"You have {due['count']} promises due today worth {due['amount']['amount']} {due['amount']['currency']}. Total overdue is {overdue['amount']} {overdue['currency']}, with {metrics['queue_size']} cases in the queue."
        return {
            "language": language,
            "highlights": [f"{overdue['amount']} {overdue['currency']}"],
            "narrative": narrative,
            "numbers_used": ["promisesDueToday", "totalOverdue", "queueSize"],
            "reason_code": "generated_from_metrics",
            "confidence": 0.99,
        }
