"""MCP tool routing: session readiness, authorization, replay protection."""

from __future__ import annotations

import uuid
from datetime import timedelta

import pytest
from conftest import IDENTITY, NOW_UTC, advancing_clock, make_operation

from engineerpc.approvals import ApprovalService
from engineerpc.contracts import AuthenticatedPrincipal, ProjectContext
from engineerpc.engine import (
    CreateBlockWorkflow,
    EngineeringOperationPlanner,
    ProjectBlockCatalogReadService,
    ProjectContextReadService,
    SclBlockPreviewService,
)
from engineerpc.ir import BlockParameter, SclAssignment
from engineerpc.mcp.router import (
    ApproveCreateBlockRequest,
    ExecuteCreateBlockRequest,
    GetBlockCatalogRequest,
    GetProjectContextRequest,
    McpTool,
    McpToolCall,
    McpToolRouter,
)
from engineerpc.mcp.session import McpSessionManager
from engineerpc.policy import DefaultEngineeringPolicy
from engineerpc.security import (
    AuthorizationRule,
    InMemorySecurityEventSink,
    ScopeAuthorizationService,
)
from engineerpc.tia.abstractions import ProjectBlock, ProjectBlockCatalogPage
from engineerpc.tia.mock import MockTiaAdapter
from engineerpc.transactions import TransactionStateMachine
from engineerpc.validation import CreateBlockOperationValidator

RULES = (
    AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan"),
    AuthorizationRule("PreviewSclBlock", "Engineer", "engineering.plan"),
    AuthorizationRule("GetProjectContext", "Engineer", "engineering.read"),
    AuthorizationRule("GetBlockCatalog", "Engineer", "engineering.read"),
    AuthorizationRule("ApproveCreateBlock", "Engineer", "engineering.execute"),
    AuthorizationRule("ExecuteCreateBlock", "Engineer", "engineering.execute"),
)


class StaticCatalogReadService:
    def __init__(self) -> None:
        self.requested: tuple | None = None

    async def get_block_catalog_async(
        self, project_id, start_index, maximum_block_count, expected_snapshot_hash, identity, operation_id
    ):
        from engineerpc.engine import ProjectBlockCatalogReadResult

        self.requested = (start_index, maximum_block_count, expected_snapshot_hash)
        return ProjectBlockCatalogReadResult.from_page(
            ProjectBlockCatalogPage(
                ProjectContext(project_id, "snapshot-1"),
                (ProjectBlock("PLC_1", "FB_Motor", "", 1, "Scl"),),
                1,
                None,
            ),
            (),
        )


def _ready_session(session_manager: McpSessionManager, scopes=("engineering.plan",)):
    session = session_manager.connect(timedelta(minutes=5))
    session_manager.authenticate(
        session.session_id,
        AuthenticatedPrincipal(IDENTITY, frozenset({"Engineer"}), frozenset(scopes)),
    )
    return session_manager.initialize(session.session_id)


def _router(session_manager, clock, catalog_service=None):
    planner = EngineeringOperationPlanner(CreateBlockOperationValidator())
    policy = DefaultEngineeringPolicy()
    return McpToolRouter(
        session_manager,
        CreateBlockWorkflow(
            planner,
            policy,
            ApprovalService(),
            TransactionStateMachine(),
            MockTiaAdapter([ProjectContext("project-1", "snapshot-1")]),
        ),
        SclBlockPreviewService(planner, policy),
        ProjectContextReadService(MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])),
        catalog_service or StaticCatalogReadService(),
        ScopeAuthorizationService(RULES, InMemorySecurityEventSink(), clock),
        clock,
    )


def _plan_call(session_id, operation=None):
    return McpToolCall(
        uuid.uuid4(), uuid.uuid4(), session_id, McpTool.PlanCreateBlock, operation or make_operation()
    )


def test_plan_routes_to_the_workflow():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)

    result = _router(manager, clock).plan_create_block(_plan_call(session.session_id))

    assert result.is_success
    assert result.payload.is_awaiting_approval


def test_plan_rejects_a_session_that_is_not_ready():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    connected = manager.connect(timedelta(minutes=5))

    result = _router(manager, clock).plan_create_block(_plan_call(connected.session_id))

    assert not result.is_success
    assert "MCP session is not ready for tool calls." in result.errors


def test_plan_rejects_an_expired_session():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)
    clock.advance(timedelta(minutes=6))

    result = _router(manager, clock).plan_create_block(_plan_call(session.session_id))

    assert "MCP session has expired." in result.errors


def test_plan_rejects_a_missing_scope():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=())

    result = _router(manager, clock).plan_create_block(_plan_call(session.session_id))

    assert "Authenticated principal does not satisfy the required role and scope." in result.errors


def test_plan_rejects_a_replayed_request_id():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)
    router = _router(manager, clock)
    call = _plan_call(session.session_id)

    first = router.plan_create_block(call)
    replay = router.plan_create_block(call)

    assert first.is_success
    assert "Request ID has already been processed for this session." in replay.errors


def test_plan_rejects_an_empty_correlation_id():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)

    call = McpToolCall(
        uuid.uuid4(), uuid.UUID(int=0), session.session_id, McpTool.PlanCreateBlock, make_operation()
    )
    result = _router(manager, clock).plan_create_block(call)

    assert "Correlation ID is required." in result.errors


def test_route_rejects_a_mismatched_tool():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)

    call = McpToolCall(
        uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.GetBlockCatalog, make_operation()
    )
    result = _router(manager, clock).plan_create_block(call)

    assert "Tool 'GetBlockCatalog' is not handled by this route." in result.errors


def test_preview_returns_declaration_source():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager)
    operation = make_operation(
        inputs=(BlockParameter("Start", "Bool"),),
        outputs=(BlockParameter("Running", "Bool"),),
        statements=(SclAssignment("Running", "Start"),),
    )

    result = _router(manager, clock).preview_scl_block(
        McpToolCall(uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.PreviewSclBlock, operation)
    )

    assert result.is_success
    assert 'FUNCTION_BLOCK "FB_Motor"' in result.payload.source_text
    assert "Running := Start;" in result.payload.source_text


async def test_get_project_context_requires_the_read_scope():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=("engineering.plan",))

    result = await _router(manager, clock).get_project_context_async(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id,
            McpTool.GetProjectContext, GetProjectContextRequest("project-1"),
        )
    )

    assert "Authenticated principal does not satisfy the required role and scope." in result.errors


async def test_get_block_catalog_passes_the_requested_page_through():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=("engineering.read",))
    catalog_service = StaticCatalogReadService()

    result = await _router(manager, clock, catalog_service).get_block_catalog_async(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.GetBlockCatalog,
            GetBlockCatalogRequest("project-1", 100, 25, "snapshot-1"),
        )
    )

    assert result.is_success
    assert catalog_service.requested == (100, 25, "snapshot-1")


async def test_approve_then_execute_commits_and_updates_context():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=("engineering.plan", "engineering.execute"))
    router = _router(manager, clock)
    operation = make_operation()

    submission = router.plan_create_block(_plan_call(session.session_id, operation))
    approval = router.approve_create_block(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.ApproveCreateBlock,
            ApproveCreateBlockRequest(submission.payload.transaction, NOW_UTC + timedelta(minutes=5)),
        )
    )
    execution = await router.execute_create_block_async(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.ExecuteCreateBlock,
            ExecuteCreateBlockRequest(approval.payload.transaction, operation),
        )
    )

    assert submission.payload.is_awaiting_approval
    assert approval.payload.is_approved
    assert execution.is_success
    assert execution.payload.is_committed
    assert execution.payload.updated_project_context.snapshot_hash != "snapshot-1"


def test_approve_requires_the_execute_scope():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=("engineering.plan",))
    router = _router(manager, clock)
    submission = router.plan_create_block(_plan_call(session.session_id))

    approval = router.approve_create_block(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.ApproveCreateBlock,
            ApproveCreateBlockRequest(submission.payload.transaction, NOW_UTC + timedelta(minutes=5)),
        )
    )

    assert "Authenticated principal does not satisfy the required role and scope." in approval.errors


async def test_execute_without_approval_is_refused():
    clock = advancing_clock()
    manager = McpSessionManager(clock)
    session = _ready_session(manager, scopes=("engineering.plan", "engineering.execute"))
    router = _router(manager, clock)
    operation = make_operation()
    submission = router.plan_create_block(_plan_call(session.session_id, operation))

    execution = await router.execute_create_block_async(
        McpToolCall(
            uuid.uuid4(), uuid.uuid4(), session.session_id, McpTool.ExecuteCreateBlock,
            ExecuteCreateBlockRequest(submission.payload.transaction, operation),
        )
    )

    assert not execution.is_success
    assert "Transaction must be approved before execution." in execution.errors
