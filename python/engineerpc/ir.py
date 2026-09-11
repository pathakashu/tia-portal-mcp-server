"""Engineering IR v1 (port of ``EngineerPc.Engineering.Ir``).

Property order in :func:`hash_json_obj` is not cosmetic: it reproduces how
System.Text.Json orders a derived record's members (derived-declared properties first,
then the ``OperationType`` override, then the base record's properties). The operation
hash depends on it.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Any, Mapping, Sequence

from .contracts import ProjectContext
from .dotnet_json import get_ci


class BlockType(IntEnum):
    Function = 0
    FunctionBlock = 1
    OrganizationBlock = 2


class ProgrammingLanguage(IntEnum):
    Scl = 0
    Lad = 1
    Fbd = 2


class EngineeringOperationType(IntEnum):
    CreateBlock = 0


@dataclass(frozen=True)
class BlockParameter:
    name: str
    data_type: str

    def to_json_obj(self) -> dict[str, Any]:
        return {"name": self.name, "dataType": self.data_type}


@dataclass(frozen=True)
class BlockInterface:
    inputs: tuple[BlockParameter, ...] = ()
    outputs: tuple[BlockParameter, ...] | None = None

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "inputs": list(self.inputs),
            "outputs": list(self.outputs) if self.outputs is not None else None,
        }


@dataclass(frozen=True)
class SclAssignment:
    target: str
    source: str

    def to_json_obj(self) -> dict[str, Any]:
        return {"target": self.target, "source": self.source}


@dataclass(frozen=True)
class CreateBlockOperation:
    operation_id: uuid.UUID
    project_context: ProjectContext
    idempotency_key: str
    name: str
    block_type: BlockType
    language: ProgrammingLanguage
    interface: BlockInterface
    statements: tuple[SclAssignment, ...] | None = None
    controller_name: str | None = None
    ir_version: str = field(default="1.0", init=False)

    @property
    def operation_type(self) -> EngineeringOperationType:
        return EngineeringOperationType.CreateBlock

    def to_json_obj(self) -> dict[str, Any]:
        """API form: enums as strings (matches the MCP host's serializer options)."""
        return _operation_json_obj(self, string_enums=True)


def hash_json_obj(operation: CreateBlockOperation) -> dict[str, Any]:
    """Hash form: enums as integers (matches the planner's serializer options)."""
    return _operation_json_obj(operation, string_enums=False)


def _operation_json_obj(operation: CreateBlockOperation, *, string_enums: bool) -> dict[str, Any]:
    def enum_value(value: IntEnum) -> Any:
        return value.name if string_enums else int(value)

    return {
        # Derived-record declared properties, in declaration order.
        "name": operation.name,
        "blockType": enum_value(operation.block_type),
        "language": enum_value(operation.language),
        "interface": operation.interface,
        "statements": list(operation.statements) if operation.statements is not None else None,
        "controllerName": operation.controller_name,
        # The abstract property overridden on the derived record.
        "operationType": enum_value(operation.operation_type),
        # Base-record properties come last.
        "irVersion": operation.ir_version,
        "operationId": str(operation.operation_id),
        "projectContext": operation.project_context,
        "idempotencyKey": operation.idempotency_key,
    }


def _parse_enum(enum_cls: type[IntEnum], raw: Any, default: IntEnum) -> IntEnum:
    """Accept either the string name or the integer value, as System.Text.Json does."""
    if raw is None:
        return default
    if isinstance(raw, bool):
        raise ValueError(f"Invalid {enum_cls.__name__}: {raw!r}")
    if isinstance(raw, int):
        return enum_cls(raw)
    text = str(raw)
    for member in enum_cls:
        if member.name.lower() == text.lower():
            return member
    raise ValueError(f"Invalid {enum_cls.__name__}: {raw!r}")


def _parse_uuid(raw: Any) -> uuid.UUID:
    if isinstance(raw, uuid.UUID):
        return raw
    try:
        return uuid.UUID(str(raw))
    except (ValueError, AttributeError, TypeError) as error:
        raise ValueError(f"Invalid GUID: {raw!r}") from error


def _parse_parameters(raw: Any) -> tuple[BlockParameter, ...]:
    if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes)):
        return ()
    return tuple(
        BlockParameter(
            name=get_ci(item, "name", "") or "",
            data_type=get_ci(item, "dataType", "") or "",
        )
        for item in raw
        if isinstance(item, Mapping)
    )


def operation_from_json_obj(source: Mapping[str, Any]) -> CreateBlockOperation:
    """Bind an MCP tool-arguments payload to a :class:`CreateBlockOperation`."""
    if not isinstance(source, Mapping):
        raise ValueError("Operation payload must be a JSON object.")

    interface_raw = get_ci(source, "interface")
    outputs_raw = get_ci(interface_raw, "outputs") if isinstance(interface_raw, Mapping) else None
    interface = BlockInterface(
        inputs=_parse_parameters(get_ci(interface_raw, "inputs") if isinstance(interface_raw, Mapping) else None),
        outputs=_parse_parameters(outputs_raw) if outputs_raw is not None else None,
    )

    statements_raw = get_ci(source, "statements")
    statements = None
    if isinstance(statements_raw, Sequence) and not isinstance(statements_raw, (str, bytes)):
        statements = tuple(
            SclAssignment(
                target=get_ci(item, "target", "") or "",
                source=get_ci(item, "source", "") or "",
            )
            for item in statements_raw
            if isinstance(item, Mapping)
        )

    return CreateBlockOperation(
        operation_id=_parse_uuid(get_ci(source, "operationId")),
        project_context=ProjectContext.from_json_obj(get_ci(source, "projectContext")),
        idempotency_key=get_ci(source, "idempotencyKey", "") or "",
        name=get_ci(source, "name", "") or "",
        block_type=_parse_enum(BlockType, get_ci(source, "blockType"), BlockType.Function),
        language=_parse_enum(ProgrammingLanguage, get_ci(source, "language"), ProgrammingLanguage.Scl),
        interface=interface,
        statements=statements,
        controller_name=get_ci(source, "controllerName"),
    )


def validate_ir(operation: CreateBlockOperation) -> list[str]:
    """Port of ``EngineeringIrValidator.Validate``."""
    if operation is None:
        raise ValueError("operation is required.")

    errors: list[str] = []
    if operation.operation_id == uuid.UUID(int=0):
        errors.append("Operation ID is required.")
    if not (operation.project_context.project_id or "").strip():
        errors.append("Project ID is required.")
    if not (operation.project_context.snapshot_hash or "").strip():
        errors.append("Project snapshot hash is required.")
    if not (operation.idempotency_key or "").strip():
        errors.append("Idempotency key is required.")
    if not (operation.name or "").strip():
        errors.append("Block name is required.")
    if operation.interface is None:
        errors.append("Block interface is required.")
    return errors
