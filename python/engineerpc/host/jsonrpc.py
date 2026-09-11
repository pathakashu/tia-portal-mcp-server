"""MCP 2025-06-18 JSON-RPC processing (port of ``McpJsonRpcRequestProcessor``).

Tool visibility is gated by deployment configuration exactly as in C#: the read tools
require the V19 worker to be enabled, and the approve/execute pair additionally requires
the default-off block-write switch.
"""

from __future__ import annotations

import hashlib
import json
import uuid
from dataclasses import dataclass
from datetime import timedelta
from typing import Any, Mapping

from .. import dotnet_json
from ..contracts import AuthenticatedPrincipal
from ..dotnet_json import get_ci
from ..engine import MAXIMUM_BLOCK_COUNT
from ..ir import operation_from_json_obj
from ..mcp.router import (
    ApproveCreateBlockRequest,
    ExecuteCreateBlockRequest,
    GetBlockCatalogRequest,
    GetProjectContextRequest,
    McpTool,
    McpToolCall,
    McpToolRouter,
)
from ..mcp.session import McpSessionError, McpSessionManager
from ..transactions import EngineeringTransaction, parse_datetime

PROTOCOL_VERSION = "2025-06-18"

_STATUS_OK = 200
_STATUS_ACCEPTED = 202
_STATUS_BAD_REQUEST = 400

DEFAULT_SESSION_DURATION_SECONDS = 300

_MISSING = object()


@dataclass(frozen=True)
class JsonRpcError:
    code: int
    message: str

    def to_json_obj(self) -> dict[str, Any]:
        return {"code": self.code, "message": self.message}


@dataclass(frozen=True)
class JsonRpcResponse:
    id: Any
    result: Any
    error: JsonRpcError | None


@dataclass(frozen=True)
class ProcessingResult:
    response: JsonRpcResponse | None
    status_code: int
    session_id: str | None


def create_deterministic_request_id(session_id: uuid.UUID, raw_json_rpc_id: str) -> uuid.UUID:
    """SHA-256 over ``{session:N}:{raw id text}``, read as a .NET GUID.

    .NET's ``new Guid(ReadOnlySpan<byte>)`` treats the first three fields as
    little-endian, which is what :func:`uuid.UUID(bytes_le=...)` reproduces.
    """
    digest = hashlib.sha256(f"{session_id.hex}:{raw_json_rpc_id}".encode("utf-8")).digest()
    return uuid.UUID(bytes_le=digest[:16])


def serialize_response(response: JsonRpcResponse) -> str:
    if response is None:
        raise ValueError("response is required.")

    payload: dict[str, Any] = {"jsonrpc": "2.0", "id": response.id}
    if response.error is not None:
        payload["error"] = response.error
    else:
        payload["result"] = response.result
    return dotnet_json.write(payload)


class McpJsonRpcRequestProcessor:
    def __init__(
        self,
        session_manager: McpSessionManager,
        tool_router: McpToolRouter,
        project_context_read_enabled: bool = False,
        block_catalog_read_enabled: bool = False,
        block_write_enabled: bool = False,
        session_duration_seconds: int = DEFAULT_SESSION_DURATION_SECONDS,
    ) -> None:
        if session_manager is None:
            raise ValueError("sessionManager is required.")
        if tool_router is None:
            raise ValueError("toolRouter is required.")

        self._session_manager = session_manager
        self._tool_router = tool_router
        self._project_context_read_enabled = project_context_read_enabled
        self._block_catalog_read_enabled = block_catalog_read_enabled
        self._block_write_enabled = block_write_enabled
        # Sessions expire this long after initialize (absolute, not sliding). TIA-backed
        # calls can take a minute each, so a short duration can strand a write mid-flow.
        self._session_duration = timedelta(seconds=session_duration_seconds)

    async def process_async(
        self,
        message: str,
        principal: AuthenticatedPrincipal,
        session_header: str | None,
        protocol_version_header: str | None,
    ) -> ProcessingResult:
        if principal is None:
            raise ValueError("principal is required.")

        try:
            request = json.loads(message) if message else None
        except json.JSONDecodeError:
            return _error(None, -32700, "Parse error.")

        if not isinstance(request, Mapping):
            return _error(None, -32600, "Invalid JSON-RPC request.")

        request_id = request["id"] if "id" in request else _MISSING
        method = request.get("method")
        if request.get("jsonrpc") != "2.0" or not isinstance(method, str) or not method.strip():
            return _error(_id_or_none(request_id), -32600, "Invalid JSON-RPC request.")

        if method != "initialize" and protocol_version_header != PROTOCOL_VERSION:
            return _error(
                _id_or_none(request_id),
                -32600,
                "Missing or unsupported MCP-Protocol-Version header.",
                status_code=_STATUS_BAD_REQUEST,
            )

        params = request.get("params")

        if method == "initialize":
            return self._initialize(request_id, params, principal, session_header)
        if method == "notifications/initialized":
            return self._initialize_session(request_id, session_header)
        if method == "ping":
            return self._ping(request_id)
        if method == "tools/list":
            return self._list_tools(request_id, session_header)
        if method == "tools/call":
            return await self._call_tool_async(request_id, params, session_header)

        return _error(_id_or_none(request_id), -32601, "Method not found.")

    # -- lifecycle ---------------------------------------------------------------

    def _initialize(
        self,
        request_id: Any,
        params: Any,
        principal: AuthenticatedPrincipal,
        session_header: str | None,
    ) -> ProcessingResult:
        if not _has_request_id(request_id) or (session_header or "").strip():
            return _error(
                _id_or_none(request_id),
                -32600,
                "Initialize requires a request ID and no MCP session header.",
            )

        client_info = get_ci(params, "clientInfo") if isinstance(params, Mapping) else None
        requested_version = get_ci(params, "protocolVersion") if isinstance(params, Mapping) else None
        capabilities = get_ci(params, "capabilities") if isinstance(params, Mapping) else None

        if (
            not isinstance(params, Mapping)
            or not isinstance(requested_version, str)
            or not isinstance(capabilities, Mapping)
            or not isinstance(client_info, Mapping)
            or not isinstance(get_ci(client_info, "name"), str)
            or not str(get_ci(client_info, "name") or "").strip()
            or not isinstance(get_ci(client_info, "version"), str)
            or not str(get_ci(client_info, "version") or "").strip()
        ):
            return _error(
                _id_or_none(request_id),
                -32602,
                "Initialize requires protocolVersion, capabilities, and clientInfo parameters.",
            )

        if requested_version != PROTOCOL_VERSION:
            return _error(_id_or_none(request_id), -32602, "Unsupported protocol version.")

        session = self._session_manager.connect(self._session_duration)
        self._session_manager.authenticate(session.session_id, principal)
        return ProcessingResult(
            JsonRpcResponse(
                _id_or_none(request_id),
                {
                    "protocolVersion": PROTOCOL_VERSION,
                    "capabilities": {"tools": {"listChanged": False}},
                    "serverInfo": {"name": "engineer-pc", "version": "1.0.0"},
                },
                None,
            ),
            _STATUS_OK,
            session.session_id.hex,
        )

    def _initialize_session(self, request_id: Any, session_header: str | None) -> ProcessingResult:
        session_id = _try_parse_session_id(session_header)
        if _has_request_id(request_id) or session_id is None:
            return _error(
                _id_or_none(request_id),
                -32600,
                "Initialized notification requires an MCP session header and no request ID.",
            )

        try:
            self._session_manager.initialize(session_id)
            return ProcessingResult(None, _STATUS_ACCEPTED, None)
        except McpSessionError:
            return _error(None, -32602, "MCP session cannot be initialized.")

    def _ping(self, request_id: Any) -> ProcessingResult:
        if not _has_request_id(request_id):
            return _error(_id_or_none(request_id), -32600, "Ping requires a request ID.")
        return _result(_id_or_none(request_id), {})

    # -- tools -------------------------------------------------------------------

    def _list_tools(self, request_id: Any, session_header: str | None) -> ProcessingResult:
        if not _has_request_id(request_id):
            return _error(_id_or_none(request_id), -32600, "Tools list requires a request ID.")

        if self._try_get_ready_session(session_header) is None:
            return _error(
                _id_or_none(request_id), -32602, "MCP session is not ready for tool calls."
            )

        tools: list[dict[str, Any]] = [
            {
                "name": "plan_create_block",
                "title": "Plan Create Block",
                "description": "Validates and creates an approval-gated plan to create a TIA block.",
                "inputSchema": _create_block_input_schema(),
            },
            {
                "name": "preview_scl_block",
                "title": "Preview SCL Block",
                "description": "Generates a deterministic constrained SCL source preview for a block intent.",
                "inputSchema": _create_scl_block_preview_input_schema(),
            },
        ]
        if self._project_context_read_enabled:
            tools.append(
                {
                    "name": "get_project_context",
                    "title": "Get Project Context",
                    "description": "Reads the snapshot context for a deployment-configured TIA V19 project.",
                    "inputSchema": _get_project_context_input_schema(),
                }
            )
        if self._block_catalog_read_enabled:
            tools.append(
                {
                    "name": "get_block_catalog",
                    "title": "Get Block Catalog",
                    "description": "Reads the PLC block catalog for a deployment-configured TIA V19 project.",
                    "inputSchema": _get_block_catalog_input_schema(),
                }
            )
        if self._block_write_enabled:
            tools.append(
                {
                    "name": "approve_create_block",
                    "title": "Approve Create Block",
                    "description": "Records human approval for a create-block transaction that is awaiting approval.",
                    "inputSchema": _approve_create_block_input_schema(),
                }
            )
            tools.append(
                {
                    "name": "execute_create_block",
                    "title": "Execute Create Block",
                    "description": "Executes an approved create-block transaction against the configured TIA V19 project.",
                    "inputSchema": _execute_create_block_input_schema(),
                }
            )

        return _result(_id_or_none(request_id), {"tools": tools})

    async def _call_tool_async(
        self, request_id: Any, params: Any, session_header: str | None
    ) -> ProcessingResult:
        if not _has_request_id(request_id):
            return _error(_id_or_none(request_id), -32600, "Tools call requires a request ID.")

        session_id = self._try_get_ready_session(session_header)
        if session_id is None:
            return _error(
                _id_or_none(request_id), -32602, "MCP session is not ready for tool calls."
            )

        if not isinstance(params, Mapping) or not isinstance(get_ci(params, "name"), str):
            return _error(_id_or_none(request_id), -32602, "Tool name is required.")

        arguments = get_ci(params, "arguments", _MISSING)
        if arguments is _MISSING:
            return _error(_id_or_none(request_id), -32602, "Tool arguments are required.")

        tool_name = get_ci(params, "name")
        # The raw id text feeds the deterministic request id; re-serialising with the
        # .NET writer reproduces GetRawText() for every well-formed JSON-RPC id.
        internal_request_id = create_deterministic_request_id(
            session_id, dotnet_json.write(_id_or_none(request_id))
        )

        if tool_name == "plan_create_block":
            return self._call_plan_create_block(request_id, session_id, internal_request_id, arguments)
        if tool_name == "preview_scl_block":
            return self._call_preview_scl_block(request_id, session_id, internal_request_id, arguments)
        if tool_name == "get_project_context" and self._project_context_read_enabled:
            return await self._call_get_project_context_async(
                request_id, session_id, internal_request_id, arguments
            )
        if tool_name == "get_block_catalog" and self._block_catalog_read_enabled:
            return await self._call_get_block_catalog_async(
                request_id, session_id, internal_request_id, arguments
            )
        if tool_name == "approve_create_block" and self._block_write_enabled:
            return self._call_approve_create_block(
                request_id, session_id, internal_request_id, arguments
            )
        if tool_name == "execute_create_block" and self._block_write_enabled:
            return await self._call_execute_create_block_async(
                request_id, session_id, internal_request_id, arguments
            )

        return _error(_id_or_none(request_id), -32602, "Tool is not supported.")

    def _call_plan_create_block(self, request_id, session_id, internal_request_id, arguments):
        try:
            operation = operation_from_json_obj(arguments)
        except (ValueError, TypeError):
            return _error(
                _id_or_none(request_id), -32602, "Tool arguments are invalid for plan_create_block."
            )

        result = self._tool_router.plan_create_block(
            McpToolCall(internal_request_id, internal_request_id, session_id, McpTool.PlanCreateBlock, operation)
        )
        return _tool_result(request_id, result)

    def _call_preview_scl_block(self, request_id, session_id, internal_request_id, arguments):
        try:
            operation = operation_from_json_obj(arguments)
        except (ValueError, TypeError):
            return _error(
                _id_or_none(request_id), -32602, "Tool arguments are invalid for preview_scl_block."
            )

        result = self._tool_router.preview_scl_block(
            McpToolCall(internal_request_id, internal_request_id, session_id, McpTool.PreviewSclBlock, operation)
        )
        return _tool_result(request_id, result)

    def _call_approve_create_block(self, request_id, session_id, internal_request_id, arguments):
        try:
            approve_request = ApproveCreateBlockRequest(
                transaction=EngineeringTransaction.from_json_obj(get_ci(arguments, "transaction")),
                expires_at_utc=parse_datetime(get_ci(arguments, "expiresAtUtc")),
            )
        except (ValueError, TypeError):
            return _error(
                _id_or_none(request_id),
                -32602,
                "Tool arguments are invalid for approve_create_block.",
            )

        result = self._tool_router.approve_create_block(
            McpToolCall(
                internal_request_id, internal_request_id, session_id,
                McpTool.ApproveCreateBlock, approve_request,
            )
        )
        return _tool_result(request_id, result)

    async def _call_execute_create_block_async(
        self, request_id, session_id, internal_request_id, arguments
    ):
        try:
            execute_request = ExecuteCreateBlockRequest(
                transaction=EngineeringTransaction.from_json_obj(get_ci(arguments, "transaction")),
                operation=operation_from_json_obj(get_ci(arguments, "operation")),
            )
        except (ValueError, TypeError):
            return _error(
                _id_or_none(request_id),
                -32602,
                "Tool arguments are invalid for execute_create_block.",
            )

        result = await self._tool_router.execute_create_block_async(
            McpToolCall(
                internal_request_id, internal_request_id, session_id,
                McpTool.ExecuteCreateBlock, execute_request,
            )
        )
        return _tool_result(request_id, result)

    async def _call_get_project_context_async(
        self, request_id, session_id, internal_request_id, arguments
    ):
        project_id = get_ci(arguments, "projectId")
        if not isinstance(arguments, Mapping) or not (project_id or "").strip():
            return _error(
                _id_or_none(request_id),
                -32602,
                "Tool arguments are invalid for get_project_context.",
            )

        result = await self._tool_router.get_project_context_async(
            McpToolCall(
                internal_request_id, internal_request_id, session_id,
                McpTool.GetProjectContext, GetProjectContextRequest(project_id),
            )
        )
        return _tool_result(request_id, result)

    async def _call_get_block_catalog_async(
        self, request_id, session_id, internal_request_id, arguments
    ):
        project_id = get_ci(arguments, "projectId")
        if not isinstance(arguments, Mapping) or not (project_id or "").strip():
            return _error(
                _id_or_none(request_id),
                -32602,
                "Tool arguments are invalid for get_block_catalog.",
            )

        catalog_request = GetBlockCatalogRequest(
            project_id=project_id,
            start_index=get_ci(arguments, "startIndex"),
            max_blocks=get_ci(arguments, "maxBlocks"),
            expected_snapshot_hash=get_ci(arguments, "expectedSnapshotHash"),
        )
        result = await self._tool_router.get_block_catalog_async(
            McpToolCall(
                internal_request_id, internal_request_id, session_id,
                McpTool.GetBlockCatalog, catalog_request,
            )
        )
        return _tool_result(request_id, result)

    def _try_get_ready_session(self, session_header: str | None) -> uuid.UUID | None:
        session_id = _try_parse_session_id(session_header)
        if session_id is None:
            return None
        session, _ = self._session_manager.try_get_ready_session(session_id)
        return session_id if session is not None else None


# -- helpers ---------------------------------------------------------------------


def _tool_result(request_id: Any, result) -> ProcessingResult:
    structured: Any = result.payload if result.payload is not None else {"errors": list(result.errors)}
    return _result(
        _id_or_none(request_id),
        {
            "content": [{"type": "text", "text": dotnet_json.write(structured)}],
            "structuredContent": structured,
            "isError": not result.is_success,
        },
    )


def _has_request_id(request_id: Any) -> bool:
    return request_id is not _MISSING and request_id is not None


def _id_or_none(request_id: Any) -> Any:
    return None if request_id is _MISSING else request_id


def _try_parse_session_id(session_header: str | None) -> uuid.UUID | None:
    text = (session_header or "").strip()
    if len(text) != 32:
        return None
    try:
        return uuid.UUID(hex=text)
    except ValueError:
        return None


def _result(request_id: Any, result: Any) -> ProcessingResult:
    return ProcessingResult(JsonRpcResponse(request_id, result, None), _STATUS_OK, None)


def _error(request_id: Any, code: int, message: str, status_code: int = _STATUS_OK) -> ProcessingResult:
    return ProcessingResult(
        JsonRpcResponse(request_id, None, JsonRpcError(code, message)), status_code, None
    )


# -- tool input schemas (kept identical to the C# host) --------------------------


def _project_context_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "projectId": {"type": "string"},
            "snapshotHash": {"type": "string"},
        },
        "required": ["projectId", "snapshotHash"],
    }


def _parameter_array_schema() -> dict[str, Any]:
    return {
        "type": "array",
        "items": {
            "type": "object",
            "properties": {"name": {"type": "string"}, "dataType": {"type": "string"}},
            "required": ["name", "dataType"],
        },
    }


def _create_block_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operationId": {"type": "string", "format": "uuid"},
            "projectContext": _project_context_schema(),
            "idempotencyKey": {"type": "string"},
            "name": {"type": "string"},
            "blockType": {
                "type": "string",
                "enum": ["Function", "FunctionBlock", "OrganizationBlock"],
            },
            "language": {"type": "string", "enum": ["Scl", "Lad", "Fbd"]},
            "interface": {
                "type": "object",
                "properties": {"inputs": _parameter_array_schema()},
                "required": ["inputs"],
            },
        },
        "required": [
            "operationId", "projectContext", "idempotencyKey", "name",
            "blockType", "language", "interface",
        ],
    }


def _create_scl_block_preview_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operationId": {"type": "string", "format": "uuid"},
            "projectContext": _project_context_schema(),
            "idempotencyKey": {"type": "string"},
            "name": {"type": "string"},
            "blockType": {"type": "string", "enum": ["Function", "FunctionBlock"]},
            "language": {"type": "string", "enum": ["Scl"]},
            "interface": {
                "type": "object",
                "properties": {
                    "inputs": _parameter_array_schema(),
                    "outputs": _parameter_array_schema(),
                },
                "required": ["inputs"],
            },
            "statements": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {"target": {"type": "string"}, "source": {"type": "string"}},
                    "required": ["target", "source"],
                },
            },
        },
        "required": [
            "operationId", "projectContext", "idempotencyKey", "name",
            "blockType", "language", "interface",
        ],
    }


def _get_project_context_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {"projectId": {"type": "string"}},
        "required": ["projectId"],
    }


def _transaction_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "transactionId": {"type": "string", "format": "uuid"},
            "operationId": {"type": "string", "format": "uuid"},
            "operationHash": {"type": "string"},
            "projectContext": _project_context_schema(),
            "idempotencyKey": {"type": "string"},
            "state": {"type": "string"},
            "createdAtUtc": {"type": "string", "format": "date-time"},
        },
        "required": [
            "transactionId", "operationId", "operationHash", "projectContext",
            "idempotencyKey", "state", "createdAtUtc",
        ],
    }


def _approve_create_block_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "transaction": _transaction_input_schema(),
            "expiresAtUtc": {"type": "string", "format": "date-time"},
        },
        "required": ["transaction", "expiresAtUtc"],
    }


def _execute_create_block_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "transaction": _transaction_input_schema(),
            "operation": _create_block_input_schema(),
        },
        "required": ["transaction", "operation"],
    }


def _get_block_catalog_input_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "projectId": {"type": "string"},
            "startIndex": {"type": "integer", "minimum": 0},
            "maxBlocks": {"type": "integer", "minimum": 1, "maximum": MAXIMUM_BLOCK_COUNT},
            "expectedSnapshotHash": {"type": "string"},
        },
        "required": ["projectId"],
    }
