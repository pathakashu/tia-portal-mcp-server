"""Engineering Engine (port of ``EngineerPc.Engineering.Engine``).

Covers deterministic planning and operation hashing, constrained SCL source rendering,
the approval-gated create-block workflow, and the two read services.
"""

from __future__ import annotations

import hashlib
import re
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable, Protocol

from . import dotnet_json
from .audit import (
    EngineeringAuditEvent,
    EngineeringAuditEventType,
    EngineeringAuditSink,
    NullEngineeringAuditSink,
)
from .approvals import ApprovalResult, ApprovalServiceProtocol, HumanApproval
from .contracts import AuthenticatedIdentity, ProjectContext
from .ir import BlockType, CreateBlockOperation, ProgrammingLanguage, hash_json_obj
from .policy import EngineeringPolicy, PolicyDecision
from .tia.abstractions import (
    ProjectBlockCatalogPage,
    ProjectBlockCatalogReader,
    ProjectBlockCatalogSnapshotChangedError,
    TiaAdapter,
)
from .transactions import (
    EngineeringTransaction,
    TransactionState,
    TransactionStateMachineProtocol,
)
from .validation import EngineeringOperationValidator

_NEWLINE = "\r\n"
_UNAVAILABLE_SNAPSHOT_HASH = "unavailable"
MAXIMUM_BLOCK_COUNT = 500


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# --------------------------------------------------------------------------------------
# Planning
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class EngineeringOperationPlan:
    operation_id: uuid.UUID
    operation_hash: str
    preview: str

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "operationId": str(self.operation_id),
            "operationHash": self.operation_hash,
            "preview": self.preview,
        }


@dataclass(frozen=True)
class PlanningResult:
    plan: EngineeringOperationPlan | None
    errors: tuple[str, ...]

    @property
    def is_success(self) -> bool:
        return self.plan is not None and len(self.errors) == 0


class EngineeringOperationPlannerProtocol(Protocol):
    def plan(self, operation: CreateBlockOperation) -> PlanningResult: ...


class EngineeringOperationPlanner:
    def __init__(self, validator: EngineeringOperationValidator) -> None:
        if validator is None:
            raise ValueError("validator is required.")
        self._validator = validator

    def plan(self, operation: CreateBlockOperation) -> PlanningResult:
        validation = self._validator.validate(operation)
        if not validation.is_valid:
            return PlanningResult(None, tuple(validation.errors))

        serialized = dotnet_json.write(hash_json_obj(operation))
        operation_hash = hashlib.sha256(serialized.encode("utf-8")).hexdigest().upper()
        preview = (
            f"Create {operation.block_type.name} '{operation.name}' "
            f"using {operation.language.name}."
        )
        return PlanningResult(
            EngineeringOperationPlan(operation.operation_id, operation_hash, preview), ()
        )


# --------------------------------------------------------------------------------------
# Constrained SCL source rendering
# --------------------------------------------------------------------------------------

_SCL_TOKEN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


class SclSourceRenderer:
    """Shared by the preview tool and real execution so both emit identical source."""

    @staticmethod
    def validate(operation: CreateBlockOperation) -> list[str]:
        if operation is None:
            raise ValueError("operation is required.")

        errors: list[str] = []
        if operation.language != ProgrammingLanguage.Scl:
            errors.append("SCL source generation requires the Scl programming language.")

        if operation.block_type not in (BlockType.Function, BlockType.FunctionBlock):
            errors.append(
                "SCL source generation supports only Function and FunctionBlock block types."
            )

        for parameter in operation.interface.inputs:
            if not _SCL_TOKEN.match(parameter.data_type or ""):
                errors.append(
                    f"Input parameter '{parameter.name}' has an unsupported SCL data type token."
                )

        for parameter in operation.interface.outputs or ():
            if not _SCL_TOKEN.match(parameter.data_type or ""):
                errors.append(
                    f"Output parameter '{parameter.name}' has an unsupported SCL data type token."
                )

        input_names = {parameter.name for parameter in operation.interface.inputs}
        output_names = {parameter.name for parameter in (operation.interface.outputs or ())}

        for statement in operation.statements or ():
            if not _SCL_TOKEN.match(statement.target or ""):
                errors.append("SCL assignment target must be an identifier.")
            elif statement.target not in output_names:
                errors.append(
                    f"SCL assignment target '{statement.target}' must be a declared output."
                )

            if not _SCL_TOKEN.match(statement.source or ""):
                errors.append("SCL assignment source must be an identifier.")
            elif statement.source not in input_names and statement.source not in output_names:
                errors.append(
                    f"SCL assignment source '{statement.source}' must be a declared input or output."
                )

        return errors

    @staticmethod
    def render(operation: CreateBlockOperation) -> str:
        if operation is None:
            raise ValueError("operation is required.")

        parts: list[str] = []
        if operation.block_type == BlockType.Function:
            parts.append(f'FUNCTION "{operation.name}" : Void{_NEWLINE}')
        else:
            parts.append(f'FUNCTION_BLOCK "{operation.name}"{_NEWLINE}')

        parts.append(_declaration_section("VAR_INPUT", operation.interface.inputs))
        if operation.interface.outputs:
            parts.append(_declaration_section("VAR_OUTPUT", operation.interface.outputs))

        parts.append(f"BEGIN{_NEWLINE}")
        for statement in operation.statements or ():
            parts.append(f"    {statement.target} := {statement.source};{_NEWLINE}")

        parts.append(
            "END_FUNCTION" if operation.block_type == BlockType.Function else "END_FUNCTION_BLOCK"
        )
        return "".join(parts)


def _declaration_section(section_name: str, parameters) -> str:
    parts = [f"{section_name}{_NEWLINE}"]
    for parameter in parameters:
        parts.append(f"    {parameter.name} : {parameter.data_type};{_NEWLINE}")
    parts.append(f"END_VAR{_NEWLINE}")
    return "".join(parts)


# --------------------------------------------------------------------------------------
# SCL preview service
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class SclBlockPreviewResult:
    plan: EngineeringOperationPlan | None
    policy_decision: PolicyDecision | None
    source_text: str | None
    errors: tuple[str, ...]

    @property
    def is_success(self) -> bool:
        return (
            self.plan is not None
            and self.policy_decision is not None
            and self.policy_decision.is_allowed
            and self.source_text is not None
            and len(self.errors) == 0
        )

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "plan": self.plan,
            "policyDecision": self.policy_decision,
            "sourceText": self.source_text,
            "errors": list(self.errors),
            "isSuccess": self.is_success,
        }


class SclBlockPreviewServiceProtocol(Protocol):
    def generate(
        self, operation: CreateBlockOperation, identity: AuthenticatedIdentity
    ) -> SclBlockPreviewResult: ...


class SclBlockPreviewService:
    def __init__(
        self,
        planner: EngineeringOperationPlannerProtocol,
        policy: EngineeringPolicy,
        audit_sink: EngineeringAuditSink | None = None,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        if planner is None:
            raise ValueError("planner is required.")
        if policy is None:
            raise ValueError("policy is required.")
        self._planner = planner
        self._policy = policy
        self._audit_sink = audit_sink or NullEngineeringAuditSink()
        self._now = time_provider or _utc_now

    def generate(
        self, operation: CreateBlockOperation, identity: AuthenticatedIdentity
    ) -> SclBlockPreviewResult:
        if operation is None:
            raise ValueError("operation is required.")
        if identity is None:
            raise ValueError("identity is required.")

        planning = self._planner.plan(operation)
        if not planning.is_success:
            return self._reject(
                operation, identity, None, None, planning.errors, "SCL preview validation failed."
            )

        policy_decision = self._policy.evaluate(operation)
        if not policy_decision.is_allowed:
            return self._reject(
                operation,
                identity,
                planning.plan,
                policy_decision,
                (policy_decision.denial_reason or "Operation is not permitted by policy.",),
                "SCL preview was denied by policy.",
            )

        errors = SclSourceRenderer.validate(operation)
        if errors:
            return self._reject(
                operation,
                identity,
                planning.plan,
                policy_decision,
                tuple(errors),
                "SCL preview source constraints failed.",
            )

        source_text = SclSourceRenderer.render(operation)
        self._record(
            EngineeringAuditEventType.SclPreviewGenerated,
            operation,
            identity,
            "Constrained SCL source preview was generated.",
        )
        return SclBlockPreviewResult(planning.plan, policy_decision, source_text, ())

    def _reject(
        self,
        operation: CreateBlockOperation,
        identity: AuthenticatedIdentity,
        plan: EngineeringOperationPlan | None,
        policy_decision: PolicyDecision | None,
        errors: tuple[str, ...],
        detail: str,
    ) -> SclBlockPreviewResult:
        self._record(EngineeringAuditEventType.SclPreviewRejected, operation, identity, detail)
        return SclBlockPreviewResult(plan, policy_decision, None, tuple(errors))

    def _record(
        self,
        event_type: EngineeringAuditEventType,
        operation: CreateBlockOperation,
        identity: AuthenticatedIdentity,
        detail: str,
    ) -> None:
        self._audit_sink.record(
            EngineeringAuditEvent(
                event_type,
                self._now(),
                operation.operation_id,
                None,
                operation.project_context.project_id,
                operation.project_context.snapshot_hash,
                identity,
                detail,
            )
        )


# --------------------------------------------------------------------------------------
# Create-block workflow
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class CreateBlockSubmissionResult:
    plan: EngineeringOperationPlan | None
    policy_decision: PolicyDecision | None
    transaction: EngineeringTransaction | None
    errors: tuple[str, ...]

    @property
    def is_awaiting_approval(self) -> bool:
        return (
            self.transaction is not None
            and self.transaction.state == TransactionState.AwaitingApproval
            and len(self.errors) == 0
        )

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "plan": self.plan,
            "policyDecision": self.policy_decision,
            "transaction": self.transaction,
            "errors": list(self.errors),
            "isAwaitingApproval": self.is_awaiting_approval,
        }


@dataclass(frozen=True)
class CreateBlockExecutionResult:
    transaction: EngineeringTransaction
    updated_project_context: ProjectContext | None
    errors: tuple[str, ...]

    @property
    def is_committed(self) -> bool:
        return self.transaction.state == TransactionState.Committed and len(self.errors) == 0

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "transaction": self.transaction,
            "updatedProjectContext": self.updated_project_context,
            "errors": list(self.errors),
            "isCommitted": self.is_committed,
        }


class CreateBlockWorkflowProtocol(Protocol):
    def submit(
        self, operation: CreateBlockOperation, now_utc: datetime
    ) -> CreateBlockSubmissionResult: ...

    def approve(
        self,
        transaction: EngineeringTransaction,
        approval: HumanApproval,
        authenticated_identity: AuthenticatedIdentity,
        now_utc: datetime,
    ) -> ApprovalResult: ...

    async def execute_async(
        self, transaction: EngineeringTransaction, operation: CreateBlockOperation
    ) -> CreateBlockExecutionResult: ...


class CreateBlockWorkflow:
    def __init__(
        self,
        planner: EngineeringOperationPlannerProtocol,
        policy: EngineeringPolicy,
        approval_service: ApprovalServiceProtocol,
        state_machine: TransactionStateMachineProtocol,
        tia_adapter: TiaAdapter,
        audit_sink: EngineeringAuditSink | None = None,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        for name, value in (
            ("planner", planner),
            ("policy", policy),
            ("approvalService", approval_service),
            ("stateMachine", state_machine),
            ("tiaAdapter", tia_adapter),
        ):
            if value is None:
                raise ValueError(f"{name} is required.")

        self._planner = planner
        self._policy = policy
        self._approval_service = approval_service
        self._state_machine = state_machine
        self._tia_adapter = tia_adapter
        self._audit_sink = audit_sink or NullEngineeringAuditSink()
        self._now = time_provider or _utc_now

    def submit(
        self, operation: CreateBlockOperation, now_utc: datetime
    ) -> CreateBlockSubmissionResult:
        if operation is None:
            raise ValueError("operation is required.")

        planning = self._planner.plan(operation)
        if not planning.is_success:
            self._record_operation(
                operation, None, EngineeringAuditEventType.PlanRejected, None,
                "Planning validation failed.", now_utc,
            )
            return CreateBlockSubmissionResult(None, None, None, tuple(planning.errors))

        policy_decision = self._policy.evaluate(operation)
        if not policy_decision.is_allowed:
            self._record_operation(
                operation, None, EngineeringAuditEventType.PlanRejected, None,
                "Planning was denied by policy.", now_utc,
            )
            return CreateBlockSubmissionResult(
                planning.plan,
                policy_decision,
                None,
                (policy_decision.denial_reason or "Operation is not permitted by policy.",),
            )

        transaction = EngineeringTransaction.create(
            operation.operation_id,
            planning.plan.operation_hash,
            operation.project_context,
            operation.idempotency_key,
            now_utc,
        )
        transaction = self._state_machine.transition(transaction, TransactionState.Validating)

        if policy_decision.requires_approval:
            transaction = self._state_machine.transition(
                transaction, TransactionState.AwaitingApproval
            )
            self._record_operation(
                operation, transaction, EngineeringAuditEventType.PlanAwaitingApproval, None,
                "Planning requires human approval.", now_utc,
            )
            return CreateBlockSubmissionResult(planning.plan, policy_decision, transaction, ())

        self._record_operation(
            operation, transaction, EngineeringAuditEventType.PlanRejected, None,
            "Automatic execution is unavailable.", now_utc,
        )
        return CreateBlockSubmissionResult(
            planning.plan,
            policy_decision,
            transaction,
            ("Automatic execution without approval is not implemented.",),
        )

    def approve(
        self,
        transaction: EngineeringTransaction,
        approval: HumanApproval,
        authenticated_identity: AuthenticatedIdentity,
        now_utc: datetime,
    ) -> ApprovalResult:
        result = self._approval_service.approve(
            transaction, approval, authenticated_identity, now_utc
        )
        event_type = (
            EngineeringAuditEventType.ApprovalGranted
            if result.is_approved
            else EngineeringAuditEventType.ApprovalRejected
        )
        detail = (
            "Human approval was granted." if result.is_approved else "Human approval was rejected."
        )
        self._record_transaction(transaction, event_type, authenticated_identity, detail, now_utc)
        return result

    async def execute_async(
        self, transaction: EngineeringTransaction, operation: CreateBlockOperation
    ) -> CreateBlockExecutionResult:
        if transaction is None:
            raise ValueError("transaction is required.")
        if operation is None:
            raise ValueError("operation is required.")

        if transaction.state != TransactionState.Approved:
            self._record_operation(
                operation, transaction, EngineeringAuditEventType.ExecutionRejected, None,
                "Execution requires an approved transaction.", self._now(),
            )
            return CreateBlockExecutionResult(
                transaction, None, ("Transaction must be approved before execution.",)
            )

        planning = self._planner.plan(operation)
        if (
            not planning.is_success
            or planning.plan.operation_hash != transaction.operation_hash
            or transaction.operation_id != operation.operation_id
            or transaction.project_context != operation.project_context
        ):
            self._record_operation(
                operation, transaction, EngineeringAuditEventType.ExecutionRejected, None,
                "Execution operation does not match its approved transaction.", self._now(),
            )
            return CreateBlockExecutionResult(
                transaction, None, ("Operation does not match the approved transaction.",)
            )

        executing = self._state_machine.transition(transaction, TransactionState.Executing)
        self._record_operation(
            operation, executing, EngineeringAuditEventType.ExecutionStarted, None,
            "Execution started.", self._now(),
        )

        scl_source_text: str | None = None
        if (
            operation.language == ProgrammingLanguage.Scl
            and operation.block_type in (BlockType.Function, BlockType.FunctionBlock)
            and not SclSourceRenderer.validate(operation)
        ):
            scl_source_text = SclSourceRenderer.render(operation)

        execution = await self._tia_adapter.create_block_async(operation, scl_source_text)

        if not execution.is_success:
            failed = self._state_machine.transition(executing, TransactionState.Failed)
            self._record_operation(
                operation, failed, EngineeringAuditEventType.ExecutionFailed, None,
                "Execution failed.", self._now(),
            )
            return CreateBlockExecutionResult(failed, None, tuple(execution.errors))

        validating = self._state_machine.transition(executing, TransactionState.ValidatingResult)
        committed = self._state_machine.transition(validating, TransactionState.Committed)
        self._record_operation(
            operation, committed, EngineeringAuditEventType.ExecutionCommitted, None,
            "Execution committed after validation.", self._now(),
        )
        return CreateBlockExecutionResult(committed, execution.updated_project_context, ())

    def _record_operation(
        self,
        operation: CreateBlockOperation,
        transaction: EngineeringTransaction | None,
        event_type: EngineeringAuditEventType,
        identity: AuthenticatedIdentity | None,
        detail: str,
        occurred_at_utc: datetime,
    ) -> None:
        self._audit_sink.record(
            EngineeringAuditEvent(
                event_type,
                occurred_at_utc,
                operation.operation_id,
                transaction.transaction_id if transaction else None,
                operation.project_context.project_id,
                operation.project_context.snapshot_hash,
                identity,
                detail,
            )
        )

    def _record_transaction(
        self,
        transaction: EngineeringTransaction,
        event_type: EngineeringAuditEventType,
        identity: AuthenticatedIdentity,
        detail: str,
        occurred_at_utc: datetime,
    ) -> None:
        self._audit_sink.record(
            EngineeringAuditEvent(
                event_type,
                occurred_at_utc,
                transaction.operation_id,
                transaction.transaction_id,
                transaction.project_context.project_id,
                transaction.project_context.snapshot_hash,
                identity,
                detail,
            )
        )


# --------------------------------------------------------------------------------------
# Read services
# --------------------------------------------------------------------------------------


@dataclass(frozen=True)
class ProjectContextReadResult:
    project_context: ProjectContext | None
    errors: tuple[str, ...]

    @property
    def is_success(self) -> bool:
        return self.project_context is not None and len(self.errors) == 0

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "projectContext": self.project_context,
            "errors": list(self.errors),
            "isSuccess": self.is_success,
        }


class ProjectContextReadServiceProtocol(Protocol):
    async def get_project_context_async(
        self, project_id: str, identity: AuthenticatedIdentity, operation_id: uuid.UUID
    ) -> ProjectContextReadResult: ...


class ProjectContextReadService:
    def __init__(
        self,
        tia_adapter: TiaAdapter,
        audit_sink: EngineeringAuditSink | None = None,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        if tia_adapter is None:
            raise ValueError("tiaAdapter is required.")
        self._tia_adapter = tia_adapter
        self._audit_sink = audit_sink or NullEngineeringAuditSink()
        self._now = time_provider or _utc_now

    async def get_project_context_async(
        self, project_id: str, identity: AuthenticatedIdentity, operation_id: uuid.UUID
    ) -> ProjectContextReadResult:
        if identity is None:
            raise ValueError("identity is required.")
        if operation_id == uuid.UUID(int=0):
            raise ValueError("Operation ID is required.")

        if not (project_id or "").strip():
            self._record(
                EngineeringAuditEventType.ProjectContextReadRejected, operation_id, "unknown",
                _UNAVAILABLE_SNAPSHOT_HASH, identity,
                "Project context read requires a project ID.",
            )
            return ProjectContextReadResult(None, ("Project ID is required.",))

        try:
            project_context = await self._tia_adapter.get_project_context_async(project_id)
            if project_context is None:
                self._record(
                    EngineeringAuditEventType.ProjectContextReadRejected, operation_id, project_id,
                    _UNAVAILABLE_SNAPSHOT_HASH, identity,
                    "Configured project context was not found.",
                )
                return ProjectContextReadResult(
                    None, ("Configured project context was not found.",)
                )

            self._record(
                EngineeringAuditEventType.ProjectContextReadSucceeded, operation_id,
                project_context.project_id, project_context.snapshot_hash, identity,
                "Project context was read.",
            )
            return ProjectContextReadResult(project_context, ())
        except Exception as error:  # noqa: BLE001 - matches the C# catch-all boundary
            self._record(
                EngineeringAuditEventType.ProjectContextReadFailed, operation_id, project_id,
                _UNAVAILABLE_SNAPSHOT_HASH, identity,
                f"Project context read failed: {type(error).__name__}.",
            )
            return ProjectContextReadResult(None, ("Project context read failed.",))

    def _record(
        self,
        event_type: EngineeringAuditEventType,
        operation_id: uuid.UUID,
        project_id: str,
        snapshot_hash: str,
        identity: AuthenticatedIdentity,
        detail: str,
    ) -> None:
        self._audit_sink.record(
            EngineeringAuditEvent(
                event_type, self._now(), operation_id, None, project_id, snapshot_hash,
                identity, detail,
            )
        )


@dataclass(frozen=True)
class ProjectBlockCatalogReadResult:
    block_catalog: ProjectBlockCatalogPage | None
    total_block_count: int
    is_truncated: bool
    errors: tuple[str, ...]
    next_start_index: int | None = None

    @staticmethod
    def from_page(
        block_catalog: ProjectBlockCatalogPage | None, errors: tuple[str, ...]
    ) -> "ProjectBlockCatalogReadResult":
        return ProjectBlockCatalogReadResult(
            block_catalog,
            block_catalog.total_block_count if block_catalog else 0,
            block_catalog.next_start_index is not None if block_catalog else False,
            errors,
            block_catalog.next_start_index if block_catalog else None,
        )

    @property
    def is_success(self) -> bool:
        return self.block_catalog is not None and len(self.errors) == 0

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "blockCatalog": self.block_catalog,
            "totalBlockCount": self.total_block_count,
            "isTruncated": self.is_truncated,
            "errors": list(self.errors),
            "nextStartIndex": self.next_start_index,
            "isSuccess": self.is_success,
        }


class ProjectBlockCatalogReadServiceProtocol(Protocol):
    async def get_block_catalog_async(
        self,
        project_id: str,
        start_index: int,
        maximum_block_count: int,
        expected_snapshot_hash: str | None,
        identity: AuthenticatedIdentity,
        operation_id: uuid.UUID,
    ) -> ProjectBlockCatalogReadResult: ...


class ProjectBlockCatalogReadService:
    def __init__(
        self,
        block_catalog_reader: ProjectBlockCatalogReader | None,
        audit_sink: EngineeringAuditSink | None = None,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        self._reader = block_catalog_reader
        self._audit_sink = audit_sink or NullEngineeringAuditSink()
        self._now = time_provider or _utc_now

    async def get_block_catalog_async(
        self,
        project_id: str,
        start_index: int,
        maximum_block_count: int,
        expected_snapshot_hash: str | None,
        identity: AuthenticatedIdentity,
        operation_id: uuid.UUID,
    ) -> ProjectBlockCatalogReadResult:
        if identity is None:
            raise ValueError("identity is required.")
        if operation_id == uuid.UUID(int=0):
            raise ValueError("Operation ID is required.")

        if not (project_id or "").strip():
            return self._reject(
                operation_id, "unknown", identity,
                "PLC block catalog read requires a project ID.", "Project ID is required.",
            )
        if maximum_block_count <= 0:
            return self._reject(
                operation_id, project_id, identity,
                "PLC block catalog read requires a positive block limit.",
                "Block limit must be positive.",
            )
        if start_index < 0:
            return self._reject(
                operation_id, project_id, identity,
                "PLC block catalog read requires a non-negative start index.",
                "Block catalog start index must be non-negative.",
            )
        if start_index > 0 and not (expected_snapshot_hash or "").strip():
            return self._reject(
                operation_id, project_id, identity,
                "PLC block catalog continuation requires an expected snapshot hash.",
                "Expected snapshot hash is required for catalog continuation.",
            )
        if self._reader is None:
            return self._reject(
                operation_id, project_id, identity,
                "PLC block catalog reader is not configured.",
                "PLC block catalog reader is not configured.",
            )

        try:
            effective = min(maximum_block_count, MAXIMUM_BLOCK_COUNT)
            block_catalog = await self._reader.get_block_catalog_page_async(
                project_id, start_index, effective, expected_snapshot_hash
            )
            if block_catalog is None:
                return self._reject(
                    operation_id, project_id, identity,
                    "Configured PLC block catalog was not found.",
                    "Configured PLC block catalog was not found.",
                )

            self._record(
                EngineeringAuditEventType.BlockCatalogReadSucceeded, operation_id,
                block_catalog.project_context.project_id,
                block_catalog.project_context.snapshot_hash, identity,
                f"PLC block catalog was read ({len(block_catalog.blocks)} of "
                f"{block_catalog.total_block_count} blocks).",
            )
            return ProjectBlockCatalogReadResult(
                block_catalog,
                block_catalog.total_block_count,
                block_catalog.next_start_index is not None,
                (),
                block_catalog.next_start_index,
            )
        except ProjectBlockCatalogSnapshotChangedError:
            return self._reject(
                operation_id, project_id, identity,
                "PLC block catalog snapshot changed before continuation.",
                "PLC block catalog snapshot has changed. Restart the catalog read.",
            )
        except Exception as error:  # noqa: BLE001 - matches the C# catch-all boundary
            self._record(
                EngineeringAuditEventType.BlockCatalogReadFailed, operation_id, project_id,
                _UNAVAILABLE_SNAPSHOT_HASH, identity,
                f"PLC block catalog read failed: {type(error).__name__}.",
            )
            return ProjectBlockCatalogReadResult.from_page(
                None, ("PLC block catalog read failed.",)
            )

    def _reject(
        self,
        operation_id: uuid.UUID,
        project_id: str,
        identity: AuthenticatedIdentity,
        detail: str,
        error: str,
    ) -> ProjectBlockCatalogReadResult:
        self._record(
            EngineeringAuditEventType.BlockCatalogReadRejected, operation_id, project_id,
            _UNAVAILABLE_SNAPSHOT_HASH, identity, detail,
        )
        return ProjectBlockCatalogReadResult.from_page(None, (error,))

    def _record(
        self,
        event_type: EngineeringAuditEventType,
        operation_id: uuid.UUID,
        project_id: str,
        snapshot_hash: str,
        identity: AuthenticatedIdentity,
        detail: str,
    ) -> None:
        self._audit_sink.record(
            EngineeringAuditEvent(
                event_type, self._now(), operation_id, None, project_id, snapshot_hash,
                identity, detail,
            )
        )
