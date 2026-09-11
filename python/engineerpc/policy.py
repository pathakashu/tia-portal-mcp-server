"""Default engineering policy (port of ``EngineerPc.Engineering.Policy``)."""

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
from typing import Any, Protocol

from .ir import CreateBlockOperation


class OperationRisk(IntEnum):
    Low = 0
    Medium = 1
    High = 2
    Critical = 3


@dataclass(frozen=True)
class PolicyDecision:
    is_allowed: bool
    risk: OperationRisk
    requires_approval: bool
    denial_reason: str | None

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "isAllowed": self.is_allowed,
            "risk": self.risk.name,
            "requiresApproval": self.requires_approval,
            "denialReason": self.denial_reason,
        }


class EngineeringPolicy(Protocol):
    def evaluate(self, operation: Any) -> PolicyDecision: ...


class DefaultEngineeringPolicy:
    def evaluate(self, operation: Any) -> PolicyDecision:
        if operation is None:
            raise ValueError("operation is required.")

        if isinstance(operation, CreateBlockOperation):
            return PolicyDecision(True, OperationRisk.Medium, True, None)

        operation_type = getattr(operation, "operation_type", None)
        name = getattr(operation_type, "name", str(operation_type))
        return PolicyDecision(
            False,
            OperationRisk.Critical,
            False,
            f"Operation type '{name}' is not permitted by the default policy.",
        )
