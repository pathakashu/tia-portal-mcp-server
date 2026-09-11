"""Human approval binding (port of ``EngineerPc.Engineering.Approvals``)."""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Protocol

from .contracts import AuthenticatedIdentity
from .transactions import EngineeringTransaction, TransactionState


@dataclass(frozen=True)
class HumanApproval:
    approval_id: uuid.UUID
    transaction_id: uuid.UUID
    operation_hash: str
    project_snapshot_hash: str
    approver: AuthenticatedIdentity
    expires_at_utc: datetime


@dataclass(frozen=True)
class ApprovalResult:
    transaction: EngineeringTransaction | None
    errors: tuple[str, ...]

    @property
    def is_approved(self) -> bool:
        return (
            self.transaction is not None
            and self.transaction.state == TransactionState.Approved
            and len(self.errors) == 0
        )

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "transaction": self.transaction,
            "errors": list(self.errors),
            "isApproved": self.is_approved,
        }


class ApprovalServiceProtocol(Protocol):
    def approve(
        self,
        transaction: EngineeringTransaction,
        approval: HumanApproval,
        authenticated_identity: AuthenticatedIdentity,
        now_utc: datetime,
    ) -> ApprovalResult: ...


class ApprovalService:
    def approve(
        self,
        transaction: EngineeringTransaction,
        approval: HumanApproval,
        authenticated_identity: AuthenticatedIdentity,
        now_utc: datetime,
    ) -> ApprovalResult:
        if transaction is None:
            raise ValueError("transaction is required.")
        if approval is None:
            raise ValueError("approval is required.")
        if authenticated_identity is None:
            raise ValueError("authenticatedIdentity is required.")

        errors: list[str] = []

        if transaction.state != TransactionState.AwaitingApproval:
            errors.append("Transaction is not awaiting approval.")

        if approval.approval_id == uuid.UUID(int=0):
            errors.append("Approval ID is required.")

        if approval.transaction_id != transaction.transaction_id:
            errors.append("Approval transaction ID does not match the transaction.")

        if approval.operation_hash != transaction.operation_hash:
            errors.append("Approval operation hash does not match the transaction.")

        if approval.project_snapshot_hash != transaction.project_context.snapshot_hash:
            errors.append("Approval project snapshot hash does not match the transaction.")

        if approval.approver != authenticated_identity:
            errors.append("Approval identity does not match the authenticated identity.")

        if approval.expires_at_utc <= now_utc:
            errors.append("Approval has expired.")

        if errors:
            return ApprovalResult(None, tuple(errors))

        return ApprovalResult(transaction.with_state(TransactionState.Approved), ())
