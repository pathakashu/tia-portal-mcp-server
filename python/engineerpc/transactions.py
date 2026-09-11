"""Immutable transaction lifecycle (port of ``EngineerPc.Engineering.Transactions``)."""

from __future__ import annotations

import re
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from enum import IntEnum
from typing import Any, Mapping, Protocol

from .contracts import ProjectContext
from .dotnet_json import PreformattedString, get_ci


class TransactionState(IntEnum):
    Created = 0
    Validating = 1
    AwaitingApproval = 2
    Approved = 3
    Executing = 4
    ValidatingResult = 5
    Committed = 6
    Rejected = 7
    Failed = 8
    RolledBack = 9
    Expired = 10


def format_datetime(value: datetime) -> PreformattedString:
    """Format like .NET's DateTimeOffset round-trip form (``2026-09-07T12:00:00+00:00``).

    Returned pre-formatted so the writer does not escape the ``+`` in the offset, which
    is what .NET's dedicated DateTimeOffset converter does.
    """
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    return PreformattedString(value.isoformat())


_FRACTION = re.compile(r"\.(\d+)")


def parse_datetime(raw: Any) -> datetime:
    """Parse an ISO-8601 timestamp, tolerating .NET's 7-digit fractional seconds."""
    if isinstance(raw, datetime):
        return raw if raw.tzinfo else raw.replace(tzinfo=timezone.utc)
    text = str(raw).strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    # Python accepts at most 6 fractional digits; .NET can emit 7.
    text = _FRACTION.sub(lambda m: "." + m.group(1)[:6], text)
    parsed = datetime.fromisoformat(text)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


@dataclass(frozen=True)
class EngineeringTransaction:
    transaction_id: uuid.UUID
    operation_id: uuid.UUID
    operation_hash: str
    project_context: ProjectContext
    idempotency_key: str
    state: TransactionState
    created_at_utc: datetime

    @staticmethod
    def create(
        operation_id: uuid.UUID,
        operation_hash: str,
        project_context: ProjectContext,
        idempotency_key: str,
        created_at_utc: datetime,
    ) -> "EngineeringTransaction":
        return EngineeringTransaction(
            transaction_id=uuid.uuid4(),
            operation_id=operation_id,
            operation_hash=operation_hash,
            project_context=project_context,
            idempotency_key=idempotency_key,
            state=TransactionState.Created,
            created_at_utc=created_at_utc,
        )

    def with_state(self, state: TransactionState) -> "EngineeringTransaction":
        return replace(self, state=state)

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "transactionId": str(self.transaction_id),
            "operationId": str(self.operation_id),
            "operationHash": self.operation_hash,
            "projectContext": self.project_context,
            "idempotencyKey": self.idempotency_key,
            "state": self.state.name,
            "createdAtUtc": format_datetime(self.created_at_utc),
        }

    @staticmethod
    def from_json_obj(source: Mapping[str, Any]) -> "EngineeringTransaction":
        if not isinstance(source, Mapping):
            raise ValueError("Transaction payload must be a JSON object.")

        raw_state = get_ci(source, "state")
        if isinstance(raw_state, int) and not isinstance(raw_state, bool):
            state = TransactionState(raw_state)
        else:
            matches = [s for s in TransactionState if s.name.lower() == str(raw_state).lower()]
            if not matches:
                raise ValueError(f"Invalid transaction state: {raw_state!r}")
            state = matches[0]

        return EngineeringTransaction(
            transaction_id=uuid.UUID(str(get_ci(source, "transactionId"))),
            operation_id=uuid.UUID(str(get_ci(source, "operationId"))),
            operation_hash=get_ci(source, "operationHash", "") or "",
            project_context=ProjectContext.from_json_obj(get_ci(source, "projectContext")),
            idempotency_key=get_ci(source, "idempotencyKey", "") or "",
            state=state,
            created_at_utc=parse_datetime(get_ci(source, "createdAtUtc")),
        )


_ALLOWED_TRANSITIONS: frozenset[tuple[TransactionState, TransactionState]] = frozenset(
    {
        (TransactionState.Created, TransactionState.Validating),
        (TransactionState.Validating, TransactionState.AwaitingApproval),
        (TransactionState.Approved, TransactionState.Executing),
        (TransactionState.Executing, TransactionState.ValidatingResult),
        (TransactionState.ValidatingResult, TransactionState.Committed),
        (TransactionState.AwaitingApproval, TransactionState.Rejected),
        (TransactionState.Executing, TransactionState.Failed),
        (TransactionState.ValidatingResult, TransactionState.Failed),
        (TransactionState.Executing, TransactionState.RolledBack),
        (TransactionState.ValidatingResult, TransactionState.RolledBack),
        (TransactionState.Failed, TransactionState.RolledBack),
        (TransactionState.Created, TransactionState.Expired),
        (TransactionState.Validating, TransactionState.Expired),
        (TransactionState.AwaitingApproval, TransactionState.Expired),
        (TransactionState.Approved, TransactionState.Expired),
    }
)


class TransactionStateMachineProtocol(Protocol):
    def transition(
        self, transaction: EngineeringTransaction, next_state: TransactionState
    ) -> EngineeringTransaction: ...


class TransactionStateMachine:
    def transition(
        self, transaction: EngineeringTransaction, next_state: TransactionState
    ) -> EngineeringTransaction:
        if transaction is None:
            raise ValueError("transaction is required.")

        if (transaction.state, next_state) not in _ALLOWED_TRANSITIONS:
            raise InvalidTransactionTransitionError(
                f"Transaction cannot transition from '{transaction.state.name}' "
                f"to '{next_state.name}'."
            )
        return transaction.with_state(next_state)


class InvalidTransactionTransitionError(Exception):
    """Raised for a disallowed lifecycle transition (mirrors InvalidOperationException)."""
