# API reference

Use `http://127.0.0.1:8000/docs` after starting the backend for authoritative request and response schemas generated from the running FastAPI app. Route models are in `app/schemas/api.py`; implementations are in `app/main.py`.

| Area | Implemented routes |
|---|---|
| System | `/`, `/health`, `/api/v1/health`, `/system/status`, `/api/v1/system/status` |
| Ingest | `POST /api/v1/ingest`, job lookup, observation list |
| Search | image/text/semantic/hybrid, filter, unified, explain, locations, similar sites |
| Change | analyze aliases, lookup, review, processing, provenance, before/after |
| Models and AI | model status/load, chat/insight aliases, compare, report, agent task/status |
| Missions and products | create/list/get/run mission, feedback, export |

Exact paths, aliases, validation, and response shapes are deliberately delegated to generated OpenAPI rather than duplicated here. Loopback restriction is enabled by default. Bearer-token authentication is applied only when both `REQUIRE_TOKEN_AUTH` and `API_AUTH_TOKEN` are configured; documentation and health/status paths are exempt.