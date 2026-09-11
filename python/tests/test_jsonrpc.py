"""JSON-RPC processing: handshake, tool gating and the full write flow over the wire."""

from __future__ import annotations

import json
import uuid
from datetime import datetime, timedelta, timezone

import pytest
from conftest import IDENTITY, make_operation

from engineerpc import dotnet_json
from engineerpc.approvals import ApprovalService
from engineerpc.contracts import AuthenticatedPrincipal, ProjectContext
from engineerpc.engine import (
    CreateBlockWorkflow,
    EngineeringOperationPlanner,
    ProjectBlockCatalogReadService,
    ProjectContextReadService,
    SclBlockPreviewService,
    ProjectContextReadResult,
    ProjectBlockCatalogReadResult,
)
from engineerpc.host.jsonrpc import PROTOCOL_VERSION, McpJsonRpcRequestProcessor, serialize_response
from engineerpc.ir import hash_json_obj
from engineerpc.mcp.router import McpToolRouter
from engineerpc.mcp.session import McpSessionManager
from engineerpc.policy import DefaultEngineeringPolicy
from engineerpc.security import (
    AuthorizationRule,
    InMemorySecurityEventSink,
    ScopeAuthorizationService,
)
from engineerpc.tia.abstractions import (
    ProjectBlock,
    ProjectBlockCatalogPage,
    TiaAdapterExecutionResult,
)
from engineerpc.transactions import TransactionStateMachine
from engineerpc.validation import CreateBlockOperationValidator
from engineerpc.host.app import AUTHORIZATION_RULES

PRINCIPAL = AuthenticatedPrincipal(
    IDENTITY,
    frozenset({"Engineer"}),
    frozenset({"engineering.plan", "engineering.read", "engineering.execute"}),
)


class RecordingCreateBlockAdapter:
    def __init__(self) -> None:
        self._writes = 0

    async def get_project_context_async(self, project_id):
        return ProjectContext(project_id, "snapshot-1")

    async def create_block_async(self, operation, scl_source_text=None):
        self._writes += 1
        return TiaAdapterExecutionResult(
            ProjectContext(operation.project_context.project_id, f"snapshot-{self._writes + 1}"), ()
        )


class StaticProjectContextReadService:
    async def get_project_context_async(self, project_id, identity, operation_id):
        return ProjectContextReadResult(ProjectContext(project_id, "snapshot-1"), ())


class StaticProjectBlockCatalogReadService:
    async def get_block_catalog_async(
        self, project_id, start_index, maximum_block_count, expected_snapshot_hash, identity, operation_id
    ):
        return ProjectBlockCatalogReadResult.from_page(
            ProjectBlockCatalogPage(
                ProjectContext(project_id, "snapshot-1"),
                (ProjectBlock("PLC_1", "FB_Motor", "", 1, "Scl"),),
                6,
                6,
            ),
            (),
        )


def create_processor(
    project_context_read_enabled=False,
    block_catalog_read_enabled=False,
    block_write_enabled=False,
    session_duration_seconds=300,
    time_provider=None,
):
    session_manager = McpSessionManager(time_provider)
    planner = EngineeringOperationPlanner(CreateBlockOperationValidator())
    policy = DefaultEngineeringPolicy()
    workflow = CreateBlockWorkflow(
        planner, policy, ApprovalService(), TransactionStateMachine(), RecordingCreateBlockAdapter()
    )
    router = McpToolRouter(
        session_manager,
        workflow,
        SclBlockPreviewService(planner, policy),
        StaticProjectContextReadService(),
        StaticProjectBlockCatalogReadService(),
        ScopeAuthorizationService(AUTHORIZATION_RULES, InMemorySecurityEventSink()),
    )
    return (
        McpJsonRpcRequestProcessor(
            session_manager,
            router,
            project_context_read_enabled,
            block_catalog_read_enabled,
            block_write_enabled,
            session_duration_seconds,
        ),
        session_manager,
    )


def request(method: str, params, request_id="1") -> str:
    payload = {"jsonrpc": "2.0", "method": method}
    if request_id is not None:
        payload["id"] = request_id
    if params is not None:
        payload["params"] = params
    return json.dumps(payload)


def initialize_request() -> str:
    return request(
        "initialize",
        {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "test-client", "version": "1.0.0"},
        },
    )


async def handshake(processor) -> str:
    initialized = await processor.process_async(initialize_request(), PRINCIPAL, None, None)
    session_id = initialized.session_id
    await processor.process_async(
        request("notifications/initialized", None, request_id=None),
        PRINCIPAL,
        session_id,
        PROTOCOL_VERSION,
    )
    return session_id


async def test_initialize_returns_a_session_and_tool_capability():
    processor, _ = create_processor()

    result = await processor.process_async(initialize_request(), PRINCIPAL, None, None)

    assert result.status_code == 200
    assert result.session_id is not None
    assert result.response.error is None
    assert "tools" in serialize_response(result.response)


async def test_initialize_rejects_missing_client_info():
    processor, _ = create_processor()

    result = await processor.process_async(
        request("initialize", {"protocolVersion": PROTOCOL_VERSION, "capabilities": {}}),
        PRINCIPAL, None, None,
    )

    assert result.response.error.code == -32602
    assert (
        result.response.error.message
        == "Initialize requires protocolVersion, capabilities, and clientInfo parameters."
    )


async def test_initialize_rejects_an_unsupported_protocol_version():
    processor, _ = create_processor()

    result = await processor.process_async(
        request(
            "initialize",
            {
                "protocolVersion": "1999-01-01",
                "capabilities": {},
                "clientInfo": {"name": "c", "version": "1"},
            },
        ),
        PRINCIPAL, None, None,
    )

    assert result.response.error.message == "Unsupported protocol version."


async def test_missing_protocol_version_header_is_rejected():
    processor, _ = create_processor()
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, session_id, None
    )

    assert result.status_code == 400
    assert result.response.error.message == "Missing or unsupported MCP-Protocol-Version header."


async def test_tools_list_before_initialized_is_rejected():
    processor, _ = create_processor()

    result = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, uuid.uuid4().hex, PROTOCOL_VERSION
    )

    assert result.response.error.code == -32602
    assert result.response.error.message == "MCP session is not ready for tool calls."


async def test_tools_list_exposes_only_planning_tools_by_default():
    processor, _ = create_processor()
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, session_id, PROTOCOL_VERSION
    )

    body = serialize_response(result.response)
    assert "plan_create_block" in body
    assert "preview_scl_block" in body
    assert "get_project_context" not in body
    assert "get_block_catalog" not in body
    assert "execute_create_block" not in body


async def test_tools_list_exposes_gated_tools_when_enabled():
    processor, _ = create_processor(True, True, True)
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, session_id, PROTOCOL_VERSION
    )

    body = serialize_response(result.response)
    for name in (
        "get_project_context", "get_block_catalog", "approve_create_block", "execute_create_block"
    ):
        assert name in body


async def test_gated_tool_call_is_refused_when_disabled():
    processor, _ = create_processor()
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/call", {"name": "get_project_context", "arguments": {"projectId": "p"}}),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    assert result.response.error.message == "Tool is not supported."


async def test_plan_create_block_returns_an_approval_gated_plan():
    processor, _ = create_processor()
    session_id = await handshake(processor)
    operation = json.loads(dotnet_json.write(make_operation()))

    result = await processor.process_async(
        request("tools/call", {"name": "plan_create_block", "arguments": operation}),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    body = serialize_response(result.response)
    assert '"isAwaitingApproval":true' in body
    assert '"isError":false' in body


async def test_plan_create_block_rejects_invalid_arguments():
    processor, _ = create_processor()
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/call", {"name": "plan_create_block", "arguments": {"name": "FB"}}),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    assert result.response.error.message == "Tool arguments are invalid for plan_create_block."


async def test_preview_scl_block_returns_source():
    processor, _ = create_processor()
    session_id = await handshake(processor)
    from engineerpc.ir import BlockParameter, SclAssignment

    operation = json.loads(
        dotnet_json.write(
            make_operation(
                inputs=(BlockParameter("Start", "Bool"),),
                outputs=(BlockParameter("Running", "Bool"),),
                statements=(SclAssignment("Running", "Start"),),
            )
        )
    )

    result = await processor.process_async(
        request("tools/call", {"name": "preview_scl_block", "arguments": operation}),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    body = serialize_response(result.response)
    assert "FUNCTION_BLOCK" in body
    assert "Running := Start;" in body
    assert '"isError":false' in body


async def test_enabled_project_context_tool_returns_configured_context():
    processor, _ = create_processor(project_context_read_enabled=True)
    session_id = await handshake(processor)

    result = await processor.process_async(
        request("tools/call", {"name": "get_project_context", "arguments": {"projectId": "project-1"}}),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    body = serialize_response(result.response)
    assert "snapshot-1" in body
    assert '"isError":false' in body


async def test_enabled_block_catalog_tool_reports_truncation():
    processor, _ = create_processor(block_catalog_read_enabled=True)
    session_id = await handshake(processor)

    result = await processor.process_async(
        request(
            "tools/call",
            {
                "name": "get_block_catalog",
                "arguments": {"projectId": "project-1", "startIndex": 5, "maxBlocks": 1},
            },
        ),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    body = serialize_response(result.response)
    assert '"totalBlockCount":6' in body
    assert '"isTruncated":true' in body
    assert '"nextStartIndex":6' in body


async def test_full_write_flow_over_json_rpc():
    processor, _ = create_processor(block_write_enabled=True)
    session_id = await handshake(processor)
    operation = json.loads(dotnet_json.write(make_operation()))

    plan = await processor.process_async(
        request("tools/call", {"name": "plan_create_block", "arguments": operation}, "1"),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )
    transaction = json.loads(serialize_response(plan.response))["result"]["structuredContent"][
        "transaction"
    ]

    approve = await processor.process_async(
        request(
            "tools/call",
            {
                "name": "approve_create_block",
                "arguments": {
                    "transaction": transaction,
                    "expiresAtUtc": (
                        datetime.now(timezone.utc) + timedelta(minutes=5)
                    ).isoformat(),
                },
            },
            "2",
        ),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )
    approve_body = serialize_response(approve.response)
    assert '"isError":false' in approve_body
    approved_transaction = json.loads(approve_body)["result"]["structuredContent"]["transaction"]

    execute = await processor.process_async(
        request(
            "tools/call",
            {
                "name": "execute_create_block",
                "arguments": {"transaction": approved_transaction, "operation": operation},
            },
            "3",
        ),
        PRINCIPAL, session_id, PROTOCOL_VERSION,
    )

    execute_body = serialize_response(execute.response)
    assert '"isError":false' in execute_body
    assert '"isCommitted":true' in execute_body


async def test_session_duration_is_configurable():
    """McpTransport:SessionDurationSeconds must actually govern session expiry.

    TIA-backed calls take ~a minute each, so a session that expires mid-workflow strands
    an approved write. The setting was previously validated but ignored.
    """
    from conftest import advancing_clock

    clock = advancing_clock()
    processor, _ = create_processor(session_duration_seconds=1800, time_provider=clock)
    session_id = await handshake(processor)

    clock.advance(timedelta(minutes=20))  # would have expired under the old hard-coded 5 min
    still_alive = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, session_id, PROTOCOL_VERSION
    )

    clock.advance(timedelta(minutes=20))  # now past the configured 30 minutes
    expired = await processor.process_async(
        request("tools/list", {}), PRINCIPAL, session_id, PROTOCOL_VERSION
    )

    assert still_alive.response.error is None
    assert expired.response.error.message == "MCP session is not ready for tool calls."


async def test_parse_error_and_unknown_method():
    processor, _ = create_processor()

    parse_error = await processor.process_async("{not json", PRINCIPAL, None, None)
    unknown = await processor.process_async(
        request("does/not/exist", {}), PRINCIPAL, None, PROTOCOL_VERSION
    )

    assert parse_error.response.error.code == -32700
    assert unknown.response.error.code == -32601


async def test_ping_requires_a_request_id():
    processor, _ = create_processor()

    ok = await processor.process_async(request("ping", {}), PRINCIPAL, None, PROTOCOL_VERSION)
    missing = await processor.process_async(
        request("ping", {}, request_id=None), PRINCIPAL, None, PROTOCOL_VERSION
    )

    assert ok.response.error is None
    assert missing.response.error.message == "Ping requires a request ID."


async def test_replayed_json_rpc_id_is_rejected_by_the_router():
    """The internal request id is derived from the session and JSON-RPC id."""
    processor, _ = create_processor()
    session_id = await handshake(processor)
    operation = json.loads(dotnet_json.write(make_operation()))
    call = request("tools/call", {"name": "plan_create_block", "arguments": operation}, "same-id")

    first = await processor.process_async(call, PRINCIPAL, session_id, PROTOCOL_VERSION)
    replay = await processor.process_async(call, PRINCIPAL, session_id, PROTOCOL_VERSION)

    assert '"isError":false' in serialize_response(first.response)
    assert "Request ID has already been processed for this session." in serialize_response(
        replay.response
    )
