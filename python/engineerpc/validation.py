"""Deterministic create-block validation (port of ``EngineerPc.Engineering.Validation``)."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Protocol

from .ir import CreateBlockOperation, validate_ir

_IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


@dataclass(frozen=True)
class ValidationResult:
    errors: tuple[str, ...]

    @property
    def is_valid(self) -> bool:
        return len(self.errors) == 0


class EngineeringOperationValidator(Protocol):
    def validate(self, operation: CreateBlockOperation) -> ValidationResult: ...


def _is_identifier(value: str | None) -> bool:
    return bool(value and value.strip()) and _IDENTIFIER.match(value) is not None


class CreateBlockOperationValidator:
    def validate(self, operation: CreateBlockOperation) -> ValidationResult:
        if operation is None:
            raise ValueError("operation is required.")

        errors = list(validate_ir(operation))
        if not _is_identifier(operation.name):
            errors.append(
                "Block name must start with a letter or underscore and contain only "
                "letters, digits, or underscores."
            )

        if operation.interface is None or operation.interface.inputs is None:
            return ValidationResult(tuple(errors))

        # OrdinalIgnoreCase set, matching the C# HashSet comparer.
        seen: set[str] = set()

        for parameter in operation.interface.inputs:
            if not _is_identifier(parameter.name):
                errors.append(f"Input parameter name '{parameter.name}' is invalid.")
            if parameter.name.lower() in seen:
                errors.append(f"Input parameter name '{parameter.name}' is duplicated.")
            else:
                seen.add(parameter.name.lower())
            if not (parameter.data_type or "").strip():
                errors.append(f"Input parameter '{parameter.name}' must declare a data type.")

        for parameter in operation.interface.outputs or ():
            if not _is_identifier(parameter.name):
                errors.append(f"Output parameter name '{parameter.name}' is invalid.")
            if parameter.name.lower() in seen:
                errors.append(f"Block interface parameter name '{parameter.name}' is duplicated.")
            else:
                seen.add(parameter.name.lower())
            if not (parameter.data_type or "").strip():
                errors.append(f"Output parameter '{parameter.name}' must declare a data type.")

        return ValidationResult(tuple(errors))
