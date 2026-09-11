"""AI-25: a heuristic *signal* that the text addresses the system rather than the recipient.
It never gates anything; it ORs into `contains_suspicious_instructions` so a human looks."""

from __future__ import annotations

import re
import unicodedata

_EN = [
    r"ignore (?:all |the |any |your )?(?:previous|prior|above|earlier|preceding)? ?(?:instructions?|prompts?|rules?|directions?)",
    r"disregard (?:all |the |any |your )?(?:previous|prior|above)? ?(?:instructions?|prompts?|rules?|schema|format)",
    r"\byou are now\b",
    r"\bnew instructions?\b",
    r"\bsystem prompt\b",
    r"\bdeveloper mode\b",
    r"\bas an ai\b",
    r"\bact as (?:the |a |an )?(?:system|administrator|admin|owner)",
    r"\bmark (?:all |the |this |these |every )?(?:invoices?|it|them|balance|account)? ?(?:as )?paid\b",
    r"\bset (?:the )?(?:classification|status|confidence)\b",
    r"\bclassif(?:y|ication) (?:this |it |the message )?as\b",
    r"\bowes? nothing\b",
    r"\bbalance (?:is|=) ?(?:zero|0)\b",
    r"\b(?:belongs to|another|other|different|the) tenant\b",
    r"\b(?:for|to) the (?:classifier|system|ai|model|assistant)\b|\bnote to the (?:system|ai|classifier)\b|\byour ai\b",
    r"\b(?:classification|status|confidence|requires_human_review|contains_suspicious_instructions)\s*=",
    r"\bskip (?:the )?human review\b|\bno human review\b",
    r"\bconfidence\s*[:=]?\s*(?:1(?:\.0+)?|0\.9\d*)\b",
    r"\breturn (?:the )?json\b",
    r"<\|im_(?:start|end)\|>|\[/?inst\]|<<sys>>|\bassistant\s*:",
    r"\bwrite[ -]?off\b.*\b(?:all|everything|balance)\b",
]
_AR = [
    r"تجاهل(?:ي|وا)? (?:كل |جميع |أي |اي )?(?:التعليمات|الأوامر|القواعد|ما سبق|السابق)",
    r"(?:أنت|انت|إنت) (?:الآن|الان|هلأ|هلق)",
    r"(?:اعتبر|اعتبري|سجل|سجلي|علم|صنف|صنفي) .{0,30}(?:مدفوع|مسدد|مسددة|مدفوعة)",
    r"(?:الفاتورة|الفواتير|الحساب|الرصيد) .{0,20}(?:مدفوع|مسدد|صفر)",
    r"(?:لا|ما) (?:يوجد|في|فيه) (?:أي |اي )?(?:مبلغ|رصيد|دين) (?:مستحق|علينا|علي)",
    r"(?:ايها|يا|الى|إلى|عزيزي) (?:النظام|البرنامج|المساعد|الذكاء)",
    r"(?:المستاجر|الشركة الاخرى|حساب اخر|شركة اخرى)",
    r"(?:الثقة|درجة الثقة)\s*[:=]?\s*(?:1|٠?[.,]9)",
    r"أوامر جديدة|تعليمات جديدة",
    r"ملاحظة لل(?:نظام|برنامج|مساعد)|requires_human_review|contains_suspicious_instructions",
]
_ARABIZI = [
    r"\btajahal\b|\btjahal\b|\btnasa\b",
    r"\b(?:enta|inta|ente) (?:hala2|halla|el2an|alan)\b",
    r"\b(?:e3tabir|i3tabir|sajel|sajjel|3allem)\b.{0,40}\b(?:madf?oo?3|madfou3|msaddad)",
    r"\bel ?(?:nizam|system)\b",
]

_ALL = [re.compile(p, re.IGNORECASE) for p in _EN + _AR + _ARABIZI]


def _normalise(text: str) -> str:
    t = unicodedata.normalize("NFKC", text)
    t = re.sub(r"[ً-ْـ]", "", t)  # strip Arabic diacritics and tatweel
    t = t.replace("أ", "ا").replace("إ", "ا").replace("آ", "ا")
    return re.sub(r"\s+", " ", t).strip()


def looks_like_instructions(text: str) -> bool:
    t = _normalise(text)
    return any(p.search(t) for p in _ALL)
