"""Typed MCP tool routing (port of ``EngineerPc.Mcp.McpToolRouter``).

Every route runs the same guard sequence before touching the Engineering Engine:
correlation id, route match, ready session, role/scope authorization, replay protection.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Callable, Generic, TypeVar

from ..approvals import ApprovalResult, HumanApproval
from ..contracts import AuthenticatedIdentity
from ..engine import (
    CreateBlockExecutionResult,
    CreateBlockSubmissionResult,
    CreateBlockWorkflowProtocol,
    ProjectBlockCatalogReadResult,
    ProjectBlockCatalogReadServiceProtocol,
    ProjectContextReadResult,
    ProjectContextReadServiceProtocol,
    SclBlockPreviewResult,
    SclBlockPreviewServiceProtocol,
    MAXIMUM_BLOCK_COUNT,
)
from ..ir import CreateBlockOperation
from ..security import AuthorizationServiceProtocol
from ..transactions import EngineeringTransaction
from .session import McpSessionManager

TPayload = TypeVar("TPayload")
TResult = TypeVar("TResult")


class McpTool(Enum):
    PlanCreateBlock = "PlanCreateBlock"
    PreviewSclBlock = "PreviewSclBlock"
    GetProjectContext = "GetProjectContext"
    GetBlockCatalog = "GetBlockCatalog"
    ApproveCreateBlock = "ApproveCreateBlock"
    ExecuteCreateBlock = "ExecuteCreateBlock"


@dataclass(frozen=True)
class McpToolCall(Generic[TPayload]):
    request_id: uuid.UUID
    correlation_id: uuid.UUID
    session_id: uuid.UUID
    tool: McpTool
    payload: TPayload


@dataclass(frozen=True)
class McpToolResult(Generic[TResult]):
    request_id: uuid.UUID
    correlation_id: uuid.UUID
    payload: TResult | None
    errors: tuple[str, ...]

    @property
    def is_success(self) -> bool:
        return self.payload is not None and len(self.errors) == 0


@dataclass(frozen=True)
class GetProjectContextRequest:
    project_id: str


@dataclass(frozen=True)
class GetBlockCatalogRequest:
    project_id: str
    start_index: int | None = None
    max_blocks: int | None = None
    expected_snapshot_hash: str | None = None


@dataclass(frozen=True)
class ApproveCreateBlockRequest:
    transaction: EngineeringTransaction
    expires_at_utc: datetime


@dataclass(frozen=True)
class ExecuteCreateBlockRequest:
    transaction: EngineeringTransaction
    operation: CreateBlockOperation


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class McpToolRouter:
    def __init__(
        self,
        session_manager: McpSessionManager,
        create_block_workflow: CreateBlockWorkflowProtocol,
        scl_block_preview_service: SclBlockPreviewServiceProtocol,
        project_context_read_service: ProjectContextReadServiceProtocol,
        project_block_catalog_read_service: ProjectBlockCatalogReadServiceProtocol,
        authorization_service: AuthorizationServiceProtocol,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        for name, value in (
            ("sessionManager", session_manager),
            ("createBlockWorkflow", create_block_workflow),
            ("sclBlockPreviewService", scl_block_preview_service),
            ("projectContextReadService", project_context_read_service),
            ("projectBlockCatalogReadService", project_block_catalog_read_service),
            ("authorizationService", authorization_service),
        ):
            if value is None:
                raise ValueError(f"{name} is required.")

        self._session_manager = session_manager
        self._create_block_workflow = create_block_workflow
        self._scl_block_preview_service = scl_block_preview_service
        self._project_context_read_service = project_context_read_service
        self._project_block_catalog_read_service = project_block_catalog_read_service
        self._authorization_service = authorization_service
        self._now = time_provider or _utc_now

    def _guard(self, tool_call: McpToolCall[Any], expected_tool: McpTool):
        """Shared precondition chain; returns (session, error)."""
        if tool_call is None:
            raise ValueError("toolCall is required.")

        if tool_call.correlation_id == uuid.UUID(int=0):
            return None, "Correlation ID is required."

        if tool_call.tool != expected_tool:
            return None, f"Tool '{tool_call.tool.value}' is not handled by this route."

        session, session_error = self._session_manager.try_get_ready_session(tool_call.session_id)
        if session is None:
            return None, session_error

        authorization = self._authorization_service.authorize(
            session.principal,
            tool_call.tool.value,
            tool_call.request_id,
            tool_call.correlation_id,
        )
        if not authorization.is_allowed:
            return None, authorization.denial_reason

        recorded, request_error = self._session_manager.try_record_request(
            tool_call.session_id, tool_call.request_id
        )
        if not recorded:
            return None, request_error

        return session, None

    def plan_create_block(
        self, tool_call: McpToolCall[CreateBlockOperation]
    ) -> McpToolResult[CreateBlockSubmissionResult]:
        session, error = self._guard(tool_call, McpTool.PlanCreateBlock)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        submission = self._create_block_workflow.submit(tool_call.payload, self._now())
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, submission, tuple(submission.errors)
        )

    def preview_scl_block(
        self, tool_call: McpToolCall[CreateBlockOperation]
    ) -> McpToolResult[SclBlockPreviewResult]:
        session, error = self._guard(tool_call, McpTool.PreviewSclBlock)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        preview = self._scl_block_preview_service.generate(
            tool_call.payload, session.principal.identity
        )
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, preview, tuple(preview.errors)
        )

    def approve_create_block(
        self, tool_call: McpToolCall[ApproveCreateBlockRequest]
    ) -> McpToolResult[ApprovalResult]:
        session, error = self._guard(tool_call, McpTool.ApproveCreateBlock)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        now = self._now()
        transaction = tool_call.payload.transaction
        # The approver identity comes from the authenticated session, never the payload.
        approval = HumanApproval(
            approval_id=uuid.uuid4(),
            transaction_id=transaction.transaction_id,
            operation_hash=transaction.operation_hash,
            project_snapshot_hash=transaction.project_context.snapshot_hash,
            approver=session.principal.identity,
            expires_at_utc=tool_call.payload.expires_at_utc,
        )
        result = self._create_block_workflow.approve(
            transaction, approval, session.principal.identity, now
        )
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, result, tuple(result.errors)
        )

    async def execute_create_block_async(
        self, tool_call: McpToolCall[ExecuteCreateBlockRequest]
    ) -> McpToolResult[CreateBlockExecutionResult]:
        session, error = self._guard(tool_call, McpTool.ExecuteCreateBlock)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        result = await self._create_block_workflow.execute_async(
            tool_call.payload.transaction, tool_call.payload.operation
        )
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, result, tuple(result.errors)
        )

    async def get_project_context_async(
        self, tool_call: McpToolCall[GetProjectContextRequest]
    ) -> McpToolResult[ProjectContextReadResult]:
        session, error = self._guard(tool_call, McpTool.GetProjectContext)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        result = await self._project_context_read_service.get_project_context_async(
            tool_call.payload.project_id, session.principal.identity, tool_call.request_id
        )
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, result, tuple(result.errors)
        )

    async def get_block_catalog_async(
        self, tool_call: McpToolCall[GetBlockCatalogRequest]
    ) -> McpToolResult[ProjectBlockCatalogReadResult]:
        session, error = self._guard(tool_call, McpTool.GetBlockCatalog)
        if error is not None:
            return McpToolResult(tool_call.request_id, tool_call.correlation_id, None, (error,))

        payload = tool_call.payload
        result = await self._project_block_catalog_read_service.get_block_catalog_async(
            payload.project_id,
            payload.start_index if payload.start_index is not None else 0,
            payload.max_blocks if payload.max_blocks is not None else MAXIMUM_BLOCK_COUNT,
            payload.expected_snapshot_hash,
            session.principal.identity,
            tool_call.request_id,
        )
        return McpToolResult(
            tool_call.request_id, tool_call.correlation_id, result, tuple(result.errors)
        )
