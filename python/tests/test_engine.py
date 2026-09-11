"""Planner, SCL preview, create-block workflow and read services."""

from __future__ import annotations

import uuid
from datetime import timedelta

import pytest
from conftest import IDENTITY, NOW_UTC, make_operation

from engineerpc.approvals import ApprovalService, HumanApproval
from engineerpc.audit import EngineeringAuditEventType, InMemoryEngineeringAuditSink
from engineerpc.contracts import ProjectContext
from engineerpc.engine import (
    CreateBlockWorkflow,
    EngineeringOperationPlanner,
    ProjectBlockCatalogReadService,
    ProjectContextReadService,
    SclBlockPreviewService,
    SclSourceRenderer,
)
from engineerpc.ir import BlockParameter, BlockType, ProgrammingLanguage, SclAssignment
from engineerpc.policy import DefaultEngineeringPolicy
from engineerpc.tia.abstractions import (
    ProjectBlock,
    ProjectBlockCatalogPage,
    ProjectBlockCatalogSnapshotChangedError,
    TiaAdapterExecutionResult,
)
from engineerpc.tia.mock import MockTiaAdapter
from engineerpc.transactions import TransactionState, TransactionStateMachine
from engineerpc.validation import CreateBlockOperationValidator


def _planner() -> EngineeringOperationPlanner:
    return EngineeringOperationPlanner(CreateBlockOperationValidator())


def test_plan_is_deterministic_for_equal_operations():
    operation = make_operation(
        operation_id=uuid.UUID("a5b9a005-26bc-4151-a45c-902a9338dc5a"),
        inputs=(BlockParameter("Start", "Bool"),),
    )
    planner = _planner()

    first = planner.plan(operation)
    second = planner.plan(operation)

    assert first.plan.operation_hash == second.plan.operation_hash
    assert first.plan.preview == "Create FunctionBlock 'FB_Motor' using Scl."


def test_plan_hash_changes_with_any_field():
    planner = _planner()
    operation_id = uuid.uuid4()
    base = planner.plan(make_operation(operation_id=operation_id)).plan.operation_hash
    renamed = planner.plan(
        make_operation(operation_id=operation_id, name="FB_Other")
    ).plan.operation_hash
    controller = planner.plan(
        make_operation(operation_id=operation_id, controller_name="PLC_1")
    ).plan.operation_hash

    assert base != renamed
    assert base != controller


def test_plan_rejects_invalid_operation():
    result = _planner().plan(make_operation(name=" "))

    assert not result.is_success
    assert "Block name is required." in result.errors


def _preview_service(audit_sink=None) -> SclBlockPreviewService:
    return SclBlockPreviewService(_planner(), DefaultEngineeringPolicy(), audit_sink)


def test_preview_renders_declarations_and_assignments():
    audit_sink = InMemoryEngineeringAuditSink()
    operation = make_operation(
        inputs=(BlockParameter("Start", "Bool"),),
        outputs=(BlockParameter("Running", "Bool"),),
        statements=(SclAssignment("Running", "Start"),),
    )

    result = _preview_service(audit_sink).generate(operation, IDENTITY)

    assert result.is_success
    normalized = result.source_text.replace("\r\n", "\n")
    assert normalized == (
        'FUNCTION_BLOCK "FB_Motor"\n'
        "VAR_INPUT\n    Start : Bool;\nEND_VAR\n"
        "VAR_OUTPUT\n    Running : Bool;\nEND_VAR\n"
        "BEGIN\n    Running := Start;\nEND_FUNCTION_BLOCK"
    )
    assert audit_sink.events[0].event_type == EngineeringAuditEventType.SclPreviewGenerated


def test_preview_rejects_non_scl_language():
    audit_sink = InMemoryEngineeringAuditSink()

    result = _preview_service(audit_sink).generate(
        make_operation(language=ProgrammingLanguage.Lad), IDENTITY
    )

    assert not result.is_success
    assert "SCL source generation requires the Scl programming language." in result.errors
    assert audit_sink.events[0].event_type == EngineeringAuditEventType.SclPreviewRejected


def test_preview_rejects_unsafe_data_type_token():
    result = _preview_service().generate(
        make_operation(inputs=(BlockParameter("Start", "Bool; DROP"),)), IDENTITY
    )

    assert "Input parameter 'Start' has an unsupported SCL data type token." in result.errors


def test_preview_rejects_assignment_to_undeclared_output():
    result = _preview_service().generate(
        make_operation(
            inputs=(BlockParameter("Start", "Bool"),),
            statements=(SclAssignment("Ghost", "Start"),),
        ),
        IDENTITY,
    )

    assert "SCL assignment target 'Ghost' must be a declared output." in result.errors


def test_renderer_emits_function_form():
    source = SclSourceRenderer.render(
        make_operation(name="FC_Safety", block_type=BlockType.Function)
    ).replace("\r\n", "\n")

    assert source.startswith('FUNCTION "FC_Safety" : Void\n')
    assert source.endswith("END_FUNCTION")


class RecordingTiaAdapter:
    def __init__(self) -> None:
        self.received_scl_source_text: str | None = None
        self._writes = 0

    async def get_project_context_async(self, project_id: str):
        return ProjectContext(project_id, "snapshot-1")

    async def create_block_async(self, operation, scl_source_text=None):
        self.received_scl_source_text = scl_source_text
        self._writes += 1
        return TiaAdapterExecutionResult(
            ProjectContext(operation.project_context.project_id, f"snapshot-{self._writes + 1}"), ()
        )


def _workflow(adapter=None, audit_sink=None) -> CreateBlockWorkflow:
    return CreateBlockWorkflow(
        _planner(),
        DefaultEngineeringPolicy(),
        ApprovalService(),
        TransactionStateMachine(),
        adapter or MockTiaAdapter([ProjectContext("project-1", "snapshot-1")]),
        audit_sink,
    )


def _approve(workflow, submission):
    approval = HumanApproval(
        approval_id=uuid.uuid4(),
        transaction_id=submission.transaction.transaction_id,
        operation_hash=submission.transaction.operation_hash,
        project_snapshot_hash=submission.transaction.project_context.snapshot_hash,
        approver=IDENTITY,
        expires_at_utc=NOW_UTC + timedelta(minutes=5),
    )
    return workflow.approve(submission.transaction, approval, IDENTITY, NOW_UTC)


async def test_workflow_commits_an_approved_matching_operation():
    audit_sink = InMemoryEngineeringAuditSink()
    workflow = _workflow(audit_sink=audit_sink)
    operation = make_operation(inputs=(BlockParameter("Start", "Bool"),))

    submission = workflow.submit(operation, NOW_UTC)
    approved = _approve(workflow, submission)
    execution = await workflow.execute_async(approved.transaction, operation)

    assert submission.is_awaiting_approval
    assert approved.is_approved
    assert execution.is_committed
    assert execution.updated_project_context.snapshot_hash != "snapshot-1"
    assert [event.event_type for event in audit_sink.events] == [
        EngineeringAuditEventType.PlanAwaitingApproval,
        EngineeringAuditEventType.ApprovalGranted,
        EngineeringAuditEventType.ExecutionStarted,
        EngineeringAuditEventType.ExecutionCommitted,
    ]


async def test_workflow_refuses_execution_without_approval():
    adapter = MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])
    workflow = _workflow(adapter)
    operation = make_operation()

    submission = workflow.submit(operation, NOW_UTC)
    execution = await workflow.execute_async(submission.transaction, operation)
    context = await adapter.get_project_context_async("project-1")

    assert not execution.is_committed
    assert "Transaction must be approved before execution." in execution.errors
    assert context == operation.project_context


async def test_workflow_refuses_execution_when_operation_no_longer_matches():
    workflow = _workflow()
    operation = make_operation()

    submission = workflow.submit(operation, NOW_UTC)
    approved = _approve(workflow, submission)
    tampered = make_operation(name="FB_Tampered", operation_id=operation.operation_id)
    execution = await workflow.execute_async(approved.transaction, tampered)

    assert not execution.is_committed
    assert "Operation does not match the approved transaction." in execution.errors


async def test_workflow_passes_rendered_scl_to_the_adapter():
    adapter = RecordingTiaAdapter()
    workflow = _workflow(adapter)
    operation = make_operation(inputs=(BlockParameter("Start", "Bool"),))

    submission = workflow.submit(operation, NOW_UTC)
    approved = _approve(workflow, submission)
    await workflow.execute_async(approved.transaction, operation)

    assert adapter.received_scl_source_text is not None
    assert 'FUNCTION_BLOCK "FB_Motor"' in adapter.received_scl_source_text


async def test_workflow_marks_failure_when_the_adapter_rejects():
    class FailingAdapter:
        async def get_project_context_async(self, project_id):
            return None

        async def create_block_async(self, operation, scl_source_text=None):
            return TiaAdapterExecutionResult(None, ("Execution is unavailable.",))

    audit_sink = InMemoryEngineeringAuditSink()
    workflow = _workflow(FailingAdapter(), audit_sink)
    operation = make_operation()

    submission = workflow.submit(operation, NOW_UTC)
    approved = _approve(workflow, submission)
    execution = await workflow.execute_async(approved.transaction, operation)

    assert not execution.is_committed
    assert execution.transaction.state == TransactionState.Failed
    assert "Execution is unavailable." in execution.errors
    assert audit_sink.events[-1].event_type == EngineeringAuditEventType.ExecutionFailed


class FailingContextAdapter:
    async def get_project_context_async(self, project_id):
        raise RuntimeError("Worker diagnostics must not escape the audit boundary.")

    async def create_block_async(self, operation, scl_source_text=None):
        raise NotImplementedError


async def test_project_context_read_scrubs_adapter_diagnostics():
    audit_sink = InMemoryEngineeringAuditSink()
    service = ProjectContextReadService(FailingContextAdapter(), audit_sink)

    result = await service.get_project_context_async("project-1", IDENTITY, uuid.uuid4())

    assert not result.is_success
    assert "Project context read failed." in result.errors
    event = audit_sink.events[0]
    assert event.event_type == EngineeringAuditEventType.ProjectContextReadFailed
    assert event.detail == "Project context read failed: RuntimeError."


async def test_project_context_read_requires_a_project_id():
    service = ProjectContextReadService(MockTiaAdapter())

    result = await service.get_project_context_async("  ", IDENTITY, uuid.uuid4())

    assert "Project ID is required." in result.errors


class StaticCatalogReader:
    def __init__(self, page=None, error: Exception | None = None) -> None:
        self.page = page
        self.error = error
        self.requested: tuple | None = None

    async def get_block_catalog_page_async(
        self, project_id, start_index, maximum_block_count, expected_snapshot_hash
    ):
        self.requested = (project_id, start_index, maximum_block_count, expected_snapshot_hash)
        if self.error:
            raise self.error
        return self.page


def _page(total=6, next_start=6):
    return ProjectBlockCatalogPage(
        project_context=ProjectContext("project-1", "snapshot-1"),
        blocks=(ProjectBlock("PLC_1", "FB_Motor", "", 1, "Scl"),),
        total_block_count=total,
        next_start_index=next_start,
    )


async def test_block_catalog_read_reports_truncation():
    reader = StaticCatalogReader(_page())
    service = ProjectBlockCatalogReadService(reader)

    result = await service.get_block_catalog_async(
        "project-1", 5, 1, "snapshot-1", IDENTITY, uuid.uuid4()
    )

    assert result.is_success
    assert result.total_block_count == 6
    assert result.is_truncated
    assert result.next_start_index == 6
    assert reader.requested == ("project-1", 5, 1, "snapshot-1")


async def test_block_catalog_read_caps_the_requested_page_size():
    reader = StaticCatalogReader(_page(total=1, next_start=None))
    service = ProjectBlockCatalogReadService(reader)

    await service.get_block_catalog_async("project-1", 0, 10_000, None, IDENTITY, uuid.uuid4())

    assert reader.requested[2] == 500


async def test_block_catalog_read_requires_snapshot_for_continuation():
    service = ProjectBlockCatalogReadService(StaticCatalogReader(_page()))

    result = await service.get_block_catalog_async(
        "project-1", 5, 1, None, IDENTITY, uuid.uuid4()
    )

    assert "Expected snapshot hash is required for catalog continuation." in result.errors


async def test_block_catalog_read_surfaces_stale_snapshot():
    service = ProjectBlockCatalogReadService(
        StaticCatalogReader(error=ProjectBlockCatalogSnapshotChangedError())
    )

    result = await service.get_block_catalog_async(
        "project-1", 1, 1, "snapshot-1", IDENTITY, uuid.uuid4()
    )

    assert "PLC block catalog snapshot has changed. Restart the catalog read." in result.errors


async def test_block_catalog_read_requires_a_configured_reader():
    service = ProjectBlockCatalogReadService(None)

    result = await service.get_block_catalog_async(
        "project-1", 0, 10, None, IDENTITY, uuid.uuid4()
    )

    assert "PLC block catalog reader is not configured." in result.errors
