"""FastAPI entry point (AI-100…AI-105). No database, no tools, no send — the model returns labels."""

from __future__ import annotations

import asyncio
import hmac
import json
import logging
import sys
import time
import uuid
from contextlib import asynccontextmanager
from typing import Any

from fastapi import Depends, FastAPI, Header, HTTPException, Request, Response
from fastapi.responses import JSONResponse

from . import classify as classify_op
from .config import Settings
from .model_client import ModelClient, ModelUnavailable, OllamaClient
from .schemas import REQUEST_VALIDATOR, RESPONSE_SCHEMA_VERSION, validation_errors

log = logging.getLogger("finance_ai.ai_service")


class _JsonFormatter(logging.Formatter):
    """One JSON object per line. Fields come from `extra`; there is no free-text message body (AI-103)."""

    def format(self, record: logging.LogRecord) -> str:
        payload = {"ts": self.formatTime(record, "%Y-%m-%dT%H:%M:%S"), "level": record.levelname, "event": record.getMessage()}
        payload.update(getattr(record, "fields", {}))
        return json.dumps(payload, ensure_ascii=True)


def _configure_logging() -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(_JsonFormatter())
    log.handlers[:] = [handler]
    log.setLevel(logging.INFO)
    log.propagate = False


def _confidence_bucket(c: float) -> str:
    if c >= 0.9:
        return "0.9+"
    if c >= 0.7:
        return "0.7-0.9"
    if c >= 0.5:
        return "0.5-0.7"
    return "<0.5"


def create_app(settings: Settings, client: ModelClient | None = None) -> FastAPI:
    _configure_logging()
    model_client: ModelClient = client or OllamaClient(settings.ollama_url, settings.model, settings.timeout_seconds)
    slots = asyncio.Semaphore(settings.max_concurrency)

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        yield
        aclose = getattr(model_client, "aclose", None)
        if aclose:
            await aclose()

    app = FastAPI(title="finance-ai AI service", version="1.0", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)

    async def require_token(x_service_token: str | None = Header(default=None)) -> None:
        # Constant-time comparison; the same 401 whether the header is missing, short, or wrong (AI-101).
        supplied = (x_service_token or "").encode("utf-8")
        expected = settings.service_token.encode("utf-8")
        if not hmac.compare_digest(supplied, expected):
            raise HTTPException(status_code=401, detail={"code": "unauthorized"})

    @app.get("/health")
    async def health() -> dict[str, Any]:
        return {"status": "ok", "operation": classify_op.OPERATION, "prompt_version": classify_op.PROMPT_VERSION}

    @app.get("/ready")
    async def ready() -> Response:
        try:
            info = await model_client.info()
        except ModelUnavailable as e:
            return JSONResponse(status_code=503, content={"status": "model_unavailable", "reason": str(e)})
        return JSONResponse({"status": "ready", "model": info["name"], "digest": info.get("digest")})

    @app.get("/model-info", dependencies=[Depends(require_token)])
    async def model_info() -> Response:
        try:
            info = await model_client.info()
        except ModelUnavailable as e:
            return JSONResponse(status_code=503, content={"code": "model_unavailable", "reason": str(e)})
        return JSONResponse(
            {
                **info,
                "prompt_version": classify_op.PROMPT_VERSION,
                "prompt_sha256": classify_op.PROMPT_SHA256,
                "schema_version": RESPONSE_SCHEMA_VERSION,
                "options": {"temperature": 0, "top_p": 1, "seed": settings.seed, "num_predict": settings.num_predict, "num_ctx": settings.num_ctx},
                "timeout_seconds": settings.timeout_seconds,
            }
        )

    @app.post("/internal/ai/v1/classify_customer_reply", dependencies=[Depends(require_token)])
    async def classify_customer_reply(request: Request) -> Response:
        started = time.perf_counter()
        try:
            body = await request.json()
        except ValueError:
            raise HTTPException(status_code=400, detail={"code": "request_invalid", "errors": ["(root): not JSON"]})
        errs = validation_errors(REQUEST_VALIDATOR, body)
        if errs:
            raise HTTPException(status_code=400, detail={"code": "request_invalid", "errors": errs})
        request_id = body["request_id"]

        # AI-104: bounded wait, then fast rejection instead of unbounded latency.
        try:
            await asyncio.wait_for(slots.acquire(), timeout=settings.queue_wait_seconds)
        except TimeoutError:
            log.info("rejected_busy", extra={"fields": {"request_id": request_id, "operation": classify_op.OPERATION}})
            raise HTTPException(status_code=429, detail={"code": "ai_busy"})
        try:
            try:
                info = await model_client.info()
                outcome = await classify_op.classify(body, model_client, info, settings)
            except ModelUnavailable as e:
                log.warning(
                    "model_unavailable",
                    extra={"fields": {"request_id": request_id, "operation": classify_op.OPERATION, "reason": str(e), "latency_ms": int((time.perf_counter() - started) * 1000)}},
                )
                raise HTTPException(status_code=503, detail={"code": "model_unavailable", "reason": str(e)})
        finally:
            slots.release()

        log.info(
            "classified",
            extra={
                "fields": {
                    "request_id": request_id,
                    "operation": classify_op.OPERATION,
                    "prompt_version": classify_op.PROMPT_VERSION,
                    "schema_version": RESPONSE_SCHEMA_VERSION,
                    "model": outcome.body["model"]["name"],
                    "digest": outcome.body["model"]["digest"],
                    "latency_ms": outcome.latency_ms,
                    "attempts": outcome.attempts,
                    "validation_status": outcome.validation_status,
                    "classification": outcome.body["classification"],
                    "confidence_bucket": _confidence_bucket(float(outcome.body["confidence"])),
                    "suspicious": outcome.body["contains_suspicious_instructions"],
                    "heuristic_injection": outcome.heuristic_injection,
                    "truncated": outcome.truncated,
                    "redactions": outcome.redactions,
                    "input_hash": outcome.input_hash[:16],
                    "validation_errors": outcome.errors[:6],
                }
            },
        )
        headers = {
            "X-Ai-Validation-Status": outcome.validation_status,
            "X-Ai-Input-Hash": outcome.input_hash,
            "X-Ai-Truncated": "true" if outcome.truncated else "false",
            "X-Ai-Attempts": str(outcome.attempts),
        }
        return JSONResponse(outcome.body, headers=headers)

    @app.exception_handler(HTTPException)
    async def http_error(_: Request, exc: HTTPException) -> Response:
        detail = exc.detail if isinstance(exc.detail, dict) else {"code": str(exc.detail)}
        return JSONResponse(status_code=exc.status_code, content=detail)

    return app


def app_from_env() -> FastAPI:
    return create_app(Settings.from_env())
