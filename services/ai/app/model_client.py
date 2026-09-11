"""The only outbound call the service makes: Ollama on the local network (AI-11, AI-105)."""

from __future__ import annotations

from typing import Any, Protocol

import httpx


class ModelUnavailable(Exception):
    """Ollama unreachable, timed out, or answered with an error."""


class ModelClient(Protocol):
    async def chat(self, *, system: str, user: str, output_schema: dict[str, Any], options: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        """Returns the raw content and runtime stats (token counts and durations, no text)."""
        ...
    async def info(self) -> dict[str, Any]: ...


class OllamaClient:
    def __init__(self, base_url: str, model: str, timeout_seconds: float) -> None:
        self._base = base_url
        self._model = model
        self._client = httpx.AsyncClient(base_url=base_url, timeout=httpx.Timeout(timeout_seconds, connect=3.0))
        self._info: dict[str, Any] | None = None

    async def aclose(self) -> None:
        await self._client.aclose()

    async def chat(self, *, system: str, user: str, output_schema: dict[str, Any], options: dict[str, Any]) -> tuple[str, dict[str, Any]]:
        body = {
            "model": self._model,
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "stream": False,
            "format": output_schema,
            "options": options,
            "think": False,          # Qwen3's thinking channel is off: deterministic, bounded, schema-shaped output only
            "keep_alive": "15m",
        }
        try:
            r = await self._client.post("/api/chat", json=body)
        except httpx.HTTPError as e:
            raise ModelUnavailable(type(e).__name__) from e
        if r.status_code >= 400:
            raise ModelUnavailable(f"ollama_http_{r.status_code}")
        data = r.json()
        content = (data.get("message") or {}).get("content")
        if not isinstance(content, str):
            raise ModelUnavailable("ollama_empty_content")
        stats = {k: data.get(k) for k in ("prompt_eval_count", "eval_count", "prompt_eval_duration", "eval_duration", "load_duration", "done_reason") if k in data}
        return content, stats

    async def info(self) -> dict[str, Any]:
        """Name, digest, quantization and context length (AI-100). Cached after the first success."""
        if self._info is not None:
            return self._info
        try:
            tags = await self._client.get("/api/tags")
        except httpx.HTTPError as e:
            raise ModelUnavailable(type(e).__name__) from e
        if tags.status_code >= 400:
            raise ModelUnavailable(f"ollama_http_{tags.status_code}")
        for m in tags.json().get("models", []):
            if m.get("name") == self._model or m.get("model") == self._model:
                details = m.get("details") or {}
                self._info = {
                    "name": self._model,
                    "digest": str(m.get("digest", ""))[:12],
                    "quantization": details.get("quantization_level"),
                    "parameter_size": details.get("parameter_size"),
                    "family": details.get("family"),
                }
                return self._info
        raise ModelUnavailable("model_not_pulled")

    async def ready(self) -> bool:
        try:
            await self.info()
            return True
        except ModelUnavailable:
            return False
