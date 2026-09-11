"""Engineering audit trail (port of ``EngineerPc.Audit``).

The JSON Lines sinks serialise with plain Web defaults — no string-enum converter — so
``eventType`` is written as an integer, matching the existing C# audit files.
"""

from __future__ import annotations

import os
import threading
import uuid
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from pathlib import Path
from typing import Any, Protocol

from . import dotnet_json
from .contracts import AuthenticatedIdentity
from .transactions import format_datetime


class EngineeringAuditEventType(IntEnum):
    BlockCatalogReadSucceeded = 0
    BlockCatalogReadRejected = 1
    BlockCatalogReadFailed = 2
    ProjectContextReadSucceeded = 3
    ProjectContextReadRejected = 4
    ProjectContextReadFailed = 5
    SclPreviewGenerated = 6
    SclPreviewRejected = 7
    PlanAwaitingApproval = 8
    PlanRejected = 9
    ApprovalGranted = 10
    ApprovalRejected = 11
    ExecutionStarted = 12
    ExecutionRejected = 13
    ExecutionFailed = 14
    ExecutionCommitted = 15


@dataclass(frozen=True)
class EngineeringAuditEvent:
    event_type: EngineeringAuditEventType
    occurred_at_utc: datetime
    operation_id: uuid.UUID
    transaction_id: uuid.UUID | None
    project_id: str
    project_snapshot_hash: str
    identity: AuthenticatedIdentity | None
    detail: str

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "eventType": int(self.event_type),
            "occurredAtUtc": format_datetime(self.occurred_at_utc),
            "operationId": str(self.operation_id),
            "transactionId": str(self.transaction_id) if self.transaction_id else None,
            "projectId": self.project_id,
            "projectSnapshotHash": self.project_snapshot_hash,
            "identity": self.identity,
            "detail": self.detail,
        }


class EngineeringAuditSink(Protocol):
    def record(self, audit_event: EngineeringAuditEvent) -> None: ...


class NullEngineeringAuditSink:
    def record(self, audit_event: EngineeringAuditEvent) -> None:
        if audit_event is None:
            raise ValueError("auditEvent is required.")


class InMemoryEngineeringAuditSink:
    def __init__(self) -> None:
        self._events: list[EngineeringAuditEvent] = []
        self._lock = threading.Lock()

    @property
    def events(self) -> tuple[EngineeringAuditEvent, ...]:
        with self._lock:
            return tuple(self._events)

    def record(self, audit_event: EngineeringAuditEvent) -> None:
        if audit_event is None:
            raise ValueError("auditEvent is required.")
        with self._lock:
            self._events.append(audit_event)


class _JsonLinesSink:
    """Append-only, write-through JSON Lines persistence."""

    def __init__(self, file_path: str) -> None:
        if not (file_path or "").strip():
            raise ValueError("An audit file path is required.")
        self._file_path = Path(file_path).resolve()
        self._lock = threading.Lock()

    def _append(self, payload: Any) -> None:
        directory = self._file_path.parent
        if not str(directory):
            raise RuntimeError("The audit file path must include a directory.")

        entry = dotnet_json.write(payload) + os.linesep
        with self._lock:
            directory.mkdir(parents=True, exist_ok=True)
            with open(self._file_path, "ab", buffering=0) as stream:
                stream.write(entry.encode("utf-8"))
                stream.flush()
                os.fsync(stream.fileno())


class JsonLinesEngineeringAuditSink(_JsonLinesSink):
    def record(self, audit_event: EngineeringAuditEvent) -> None:
        if audit_event is None:
            raise ValueError("auditEvent is required.")
        self._append(audit_event)
