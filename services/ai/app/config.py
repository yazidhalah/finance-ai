"""Configuration from the environment. Nothing here is a secret except AI_SERVICE_TOKEN (AI-101)."""

from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    service_token: str
    ollama_url: str
    model: str
    timeout_seconds: float
    seed: int
    num_predict: int
    num_ctx: int
    max_concurrency: int
    queue_wait_seconds: float
    default_min_confidence: float

    @staticmethod
    def from_env() -> "Settings":
        token = os.environ.get("AI_SERVICE_TOKEN", "")
        if len(token) < 16:
            raise RuntimeError("AI_SERVICE_TOKEN must be set (at least 16 characters); the service refuses to start without it (AI-101)")
        return Settings(
            service_token=token,
            ollama_url=os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434").rstrip("/"),
            model=os.environ.get("AI_MODEL", "qwen3:4b"),
            timeout_seconds=float(os.environ.get("AI_TIMEOUT_SECONDS", "20")),   # AI-10
            seed=int(os.environ.get("AI_SEED", "42")),                              # AI-09
            num_predict=int(os.environ.get("AI_NUM_PREDICT", "700")),                # AI-09
            num_ctx=int(os.environ.get("AI_NUM_CTX", "8192")),
            max_concurrency=int(os.environ.get("AI_MAX_CONCURRENCY", "5")),          # AI-104
            queue_wait_seconds=float(os.environ.get("AI_QUEUE_WAIT_SECONDS", "2")),
            default_min_confidence=float(os.environ.get("AI_DEFAULT_MIN_CONFIDENCE", "0.70")),  # AI-05
        )
