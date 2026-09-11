"""Bounded MCP Streamable HTTP endpoint (port of the C# ``Program.cs``).

Composition, gating and fail-closed behaviour mirror the C# host: the endpoint is
non-writing by default, execution only reaches a real project when the V19 worker and the
separate block-write switch are both enabled, and production requires mTLS with an
explicit thumbprint-to-principal mapping.
"""

from __future__ import annotations

import asyncio
import uuid
from datetime import timedelta
from pathlib import Path
from typing import Any, Awaitable, Callable
from urllib.parse import urlparse

from starlette.applications import Starlette
from starlette.middleware import Middleware
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import JSONResponse, PlainTextResponse, Response
from starlette.routing import Mount, Route
from starlette.staticfiles import StaticFiles

from ..approvals import ApprovalService
from ..audit import JsonLinesEngineeringAuditSink
from ..contracts import AuthenticatedIdentity, AuthenticatedPrincipal, ProjectContext
from ..engine import (
    CreateBlockWorkflow,
    EngineeringOperationPlanner,
    ProjectBlockCatalogReadService,
    ProjectContextReadService,
    SclBlockPreviewService,
)
from ..ir import CreateBlockOperation
from ..mcp.router import McpToolRouter
from ..mcp.session import McpSessionManager
from ..policy import DefaultEngineeringPolicy
from ..security import (
    AuthorizationRule,
    JsonLinesSecurityEventSink,
    ScopeAuthorizationService,
)
from ..tia.abstractions import TiaAdapterExecutionResult
from ..tia.v19_client import TiaV19WorkerClient, TiaV19WorkerClientOptions
from ..transactions import TransactionStateMachine
from ..validation import CreateBlockOperationValidator
from .jsonrpc import PROTOCOL_VERSION, McpJsonRpcRequestProcessor, serialize_response
from .options import (
    McpAuditOptions,
    McpTransportOptions,
    TiaV19WorkerHostOptions,
    load_options,
    validate_transport_options,
)
from .tls import ClientCertificatePrincipalMapper

WWWROOT = Path(__file__).parent / "wwwroot"

AUTHORIZATION_RULES = (
    AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan"),
    AuthorizationRule("PreviewSclBlock", "Engineer", "engineering.plan"),
    AuthorizationRule("GetProjectContext", "Engineer", "engineering.read"),
    AuthorizationRule("GetBlockCatalog", "Engineer", "engineering.read"),
    AuthorizationRule("ApproveCreateBlock", "Engineer", "engineering.execute"),
    AuthorizationRule("ExecuteCreateBlock", "Engineer", "engineering.execute"),
)


class PlanningOnlyTiaAdapter:
    """Fails closed: planning works, execution is unavailable without a real adapter."""

    async def get_project_context_async(self, project_id: str) -> ProjectContext | None:
        return None

    async def create_block_async(
        self, operation: CreateBlockOperation, scl_source_text: str | None = None
    ) -> TiaAdapterExecutionResult:
        return TiaAdapterExecutionResult(
            None, ("Execution is unavailable until a TIA Portal V19 adapter is configured.",)
        )


def create_app(
    transport_options: McpTransportOptions | None = None,
    worker_options: TiaV19WorkerHostOptions | None = None,
    audit_options: McpAuditOptions | None = None,
) -> Starlette:
    if transport_options is None or worker_options is None or audit_options is None:
        loaded_transport, loaded_worker, loaded_audit = load_options()
        transport_options = transport_options or loaded_transport
        worker_options = worker_options or loaded_worker
        audit_options = audit_options or loaded_audit

    option_validation = validate_transport_options(transport_options)
    if not option_validation.is_valid:
        raise RuntimeError(" ".join(option_validation.errors))

    session_manager = McpSessionManager()
    engineering_audit_sink = JsonLinesEngineeringAuditSink(audit_options.engineering_file_path)

    worker_client: TiaV19WorkerClient | None = None
    if worker_options.enabled:
        worker_client = TiaV19WorkerClient(
            TiaV19WorkerClientOptions(
                worker_executable_path=worker_options.worker_executable_path or "",
                configuration_path=worker_options.configuration_path or "",
                request_timeout=timedelta(seconds=worker_options.request_timeout_seconds),
            )
        )

    project_context_adapter = worker_client or PlanningOnlyTiaAdapter()

    block_write_enabled = worker_options.enabled and worker_options.enable_block_write
    execution_adapter = (
        worker_client if block_write_enabled and worker_client else PlanningOnlyTiaAdapter()
    )

    operation_planner = EngineeringOperationPlanner(CreateBlockOperationValidator())
    engineering_policy = DefaultEngineeringPolicy()
    workflow = CreateBlockWorkflow(
        operation_planner,
        engineering_policy,
        ApprovalService(),
        TransactionStateMachine(),
        execution_adapter,
        engineering_audit_sink,
    )
    scl_block_preview_service = SclBlockPreviewService(
        operation_planner, engineering_policy, engineering_audit_sink
    )
    project_context_read_service = ProjectContextReadService(
        project_context_adapter, engineering_audit_sink
    )
    project_block_catalog_read_service = ProjectBlockCatalogReadService(
        worker_client, engineering_audit_sink
    )
    authorization_service = ScopeAuthorizationService(
        AUTHORIZATION_RULES, JsonLinesSecurityEventSink(audit_options.file_path)
    )

    request_processor = McpJsonRpcRequestProcessor(
        session_manager,
        McpToolRouter(
            session_manager,
            workflow,
            scl_block_preview_service,
            project_context_read_service,
            project_block_catalog_read_service,
            authorization_service,
        ),
        worker_options.enabled,
        worker_options.enabled and worker_options.enable_block_catalog_read,
        block_write_enabled,
        transport_options.session_duration_seconds,
    )

    development_principal: AuthenticatedPrincipal | None = None
    principal_mapper: ClientCertificatePrincipalMapper | None = None
    if transport_options.allow_insecure_localhost:
        development_principal = AuthenticatedPrincipal(
            identity=AuthenticatedIdentity("localhost-development", "localhost-development"),
            roles=frozenset({"Engineer"}),
            scopes=frozenset({"engineering.plan", "engineering.read", "engineering.execute"}),
        )
    else:
        principal_mapper = ClientCertificatePrincipalMapper(
            transport_options.client_principal_mappings
        )

    async def health(request: Request) -> Response:
        return JSONResponse({"status": "ready"})

    async def mcp_endpoint(request: Request) -> Response:
        if development_principal is not None:
            principal = development_principal
        else:
            tls = (request.scope.get("extensions") or {}).get("tls") or {}
            certificate_der = tls.get("client_cert_der")
            certificate_dict = tls.get("client_cert_dict") or {}
            subject = _format_subject(certificate_dict.get("subject"))
            principal = principal_mapper.try_map(certificate_der, subject)
            if principal is None:
                return PlainTextResponse("", status_code=403)

        return await _handle_mcp_request(request, principal, request_processor)

    routes: list[Any] = [
        Route("/health", health, methods=["GET"]),
        Route("/mcp", mcp_endpoint, methods=["POST", "GET"]),
    ]
    if WWWROOT.is_dir():
        routes.append(Mount("/", app=StaticFiles(directory=str(WWWROOT), html=True)))

    async def request_timeout(
        request: Request, call_next: Callable[[Request], Awaitable[Response]]
    ) -> Response:
        try:
            return await asyncio.wait_for(
                call_next(request), timeout=transport_options.request_timeout_seconds
            )
        except asyncio.TimeoutError:
            return PlainTextResponse("", status_code=504)

    app = Starlette(
        routes=routes,
        middleware=[Middleware(BaseHTTPMiddleware, dispatch=request_timeout)],
    )
    app.state.transport_options = transport_options
    app.state.worker_options = worker_options
    app.state.request_processor = request_processor
    app.state.session_manager = session_manager
    return app


async def _handle_mcp_request(
    request: Request,
    principal: AuthenticatedPrincipal,
    request_processor: McpJsonRpcRequestProcessor,
) -> Response:
    if not _is_allowed_origin(request):
        return PlainTextResponse("", status_code=403)

    if request.method == "GET":
        # No SSE stream is offered, matching the C# host.
        return PlainTextResponse("", status_code=405, headers={"Allow": "POST"})

    content_type = (request.headers.get("content-type") or "").split(";")[0].strip().lower()
    if content_type != "application/json":
        return PlainTextResponse("", status_code=415)

    message = (await request.body()).decode("utf-8", errors="replace")
    result = await request_processor.process_async(
        message,
        principal,
        request.headers.get("Mcp-Session-Id"),
        request.headers.get("MCP-Protocol-Version"),
    )

    headers = {}
    if result.session_id is not None:
        headers["Mcp-Session-Id"] = result.session_id

    if result.response is None:
        return PlainTextResponse("", status_code=result.status_code, headers=headers)

    return Response(
        content=serialize_response(result.response),
        status_code=result.status_code,
        media_type="application/json",
        headers=headers,
    )


def _is_allowed_origin(request: Request) -> bool:
    origin = request.headers.get("Origin")
    if origin is None:
        return True

    parsed = urlparse(origin)
    if not parsed.scheme or not parsed.netloc:
        return False

    host = request.headers.get("host") or ""
    return parsed.scheme.lower() == request.url.scheme.lower() and parsed.netloc.lower() == host.lower()


def _format_subject(subject: Any) -> str | None:
    if not subject:
        return None
    from .tls import _format_issuer  # reuse the same RDN rendering

    return _format_issuer(subject)
