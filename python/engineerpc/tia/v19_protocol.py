"""Worker wire protocol (port of ``EngineerPc.Tia.V19.Protocol``).

This is the language boundary: the .NET Framework worker that owns Siemens.Engineering
parses exactly these payloads, so the field set, ordering and integer error codes must
stay identical to the C# records.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Any, Mapping

from ..dotnet_json import get_ci

VERSION = "1.3"
GET_PROJECT_CONTEXT_METHOD = "get_project_context"
GET_BLOCK_CATALOG_METHOD = "get_block_catalog"
CREATE_BLOCK_METHOD = "create_block"
MAXIMUM_BLOCK_CATALOG_BLOCK_COUNT = 500


class BlockCatalogErrorCode(IntEnum):
    NoError = 0
    SnapshotChanged = 1


class CreateBlockErrorCode(IntEnum):
    NoError = 0
    SnapshotChanged = 1
    ControllerNotFound = 2
    BlockAlreadyExists = 3
    GenerationFailed = 4


@dataclass(frozen=True)
class WorkerRequest:
    protocol_version: str
    request_id: str
    method: str
    project_id: str | None
    block_catalog_start_index: int | None = None
    block_catalog_maximum_block_count: int | None = None
    block_catalog_expected_snapshot_hash: str | None = None
    create_block_controller_name: str | None = None
    create_block_name: str | None = None
    create_block_type: str | None = None
    create_block_source_text: str | None = None
    create_block_expected_snapshot_hash: str | None = None

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "protocolVersion": self.protocol_version,
            "requestId": self.request_id,
            "method": self.method,
            "projectId": self.project_id,
            "blockCatalogStartIndex": self.block_catalog_start_index,
            "blockCatalogMaximumBlockCount": self.block_catalog_maximum_block_count,
            "blockCatalogExpectedSnapshotHash": self.block_catalog_expected_snapshot_hash,
            "createBlockControllerName": self.create_block_controller_name,
            "createBlockName": self.create_block_name,
            "createBlockType": self.create_block_type,
            "createBlockSourceText": self.create_block_source_text,
            "createBlockExpectedSnapshotHash": self.create_block_expected_snapshot_hash,
        }


@dataclass(frozen=True)
class ProjectContextResponse:
    request_id: str
    project_id: str | None
    snapshot_hash: str | None
    error: str | None

    @staticmethod
    def from_json_obj(source: Mapping[str, Any]) -> "ProjectContextResponse":
        return ProjectContextResponse(
            request_id=get_ci(source, "requestId", "") or "",
            project_id=get_ci(source, "projectId"),
            snapshot_hash=get_ci(source, "snapshotHash"),
            error=get_ci(source, "error"),
        )


@dataclass(frozen=True)
class BlockDefinition:
    controller_name: str
    name: str
    namespace: str
    number: int
    programming_language: str


@dataclass(frozen=True)
class BlockCatalogResponse:
    request_id: str
    project_id: str | None
    snapshot_hash: str | None
    blocks: tuple[BlockDefinition, ...]
    total_block_count: int | None
    next_start_index: int | None
    error_code: BlockCatalogErrorCode
    error: str | None

    @staticmethod
    def from_json_obj(source: Mapping[str, Any]) -> "BlockCatalogResponse":
        raw_blocks = get_ci(source, "blocks") or []
        blocks = tuple(
            BlockDefinition(
                controller_name=get_ci(item, "controllerName", "") or "",
                name=get_ci(item, "name", "") or "",
                namespace=get_ci(item, "namespace") or "",
                number=int(get_ci(item, "number", 0) or 0),
                programming_language=get_ci(item, "programmingLanguage", "") or "",
            )
            for item in raw_blocks
        )
        return BlockCatalogResponse(
            request_id=get_ci(source, "requestId", "") or "",
            project_id=get_ci(source, "projectId"),
            snapshot_hash=get_ci(source, "snapshotHash"),
            blocks=blocks,
            total_block_count=get_ci(source, "totalBlockCount"),
            next_start_index=get_ci(source, "nextStartIndex"),
            error_code=BlockCatalogErrorCode(get_ci(source, "errorCode", 0) or 0),
            error=get_ci(source, "error"),
        )


@dataclass(frozen=True)
class CreateBlockResponse:
    request_id: str
    project_id: str | None
    snapshot_hash: str | None
    error_code: CreateBlockErrorCode
    error: str | None

    @staticmethod
    def from_json_obj(source: Mapping[str, Any]) -> "CreateBlockResponse":
        return CreateBlockResponse(
            request_id=get_ci(source, "requestId", "") or "",
            project_id=get_ci(source, "projectId"),
            snapshot_hash=get_ci(source, "snapshotHash"),
            error_code=CreateBlockErrorCode(get_ci(source, "errorCode", 0) or 0),
            error=get_ci(source, "error"),
        )


_DOTNET_EPOCH = datetime(1, 1, 1, tzinfo=timezone.utc)


def datetime_to_ticks(value: datetime) -> int:
    """.NET ``DateTime.Ticks``: 100-nanosecond intervals since 0001-01-01."""
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    delta = value.astimezone(timezone.utc) - _DOTNET_EPOCH
    return (delta.days * 86_400 + delta.seconds) * 10_000_000 + delta.microseconds * 10


def calculate_project_snapshot(
    project_id: str,
    project_name: str,
    project_file_path: str,
    last_modified_utc_ticks: int,
    version: str,
) -> str:
    """Port of ``TiaV19ProjectSnapshot.Calculate`` (length-prefixed fields, upper-case hex).

    ``version`` may legitimately be empty: real V19 projects report an empty
    ``Project.Version``, which is why only ``None`` is rejected.

    ``Project.Size`` is deliberately excluded — TIA appends a log entry on every Openness
    open, so the size grows on each read even when nothing changed, which made every
    snapshot differ and permanently failed catalog continuation and approved writes.
    """
    for name, value in (
        ("projectId", project_id),
        ("projectName", project_name),
        ("projectFilePath", project_file_path),
    ):
        if not (value or "").strip():
            raise ValueError(f"A non-empty value is required. (Parameter '{name}')")
    if version is None:
        raise ValueError("version is required.")

    def encode(value: str) -> str:
        return f"{len(value)}:{value}"

    canonical = "\n".join(
        [
            encode(project_id),
            encode(project_name),
            encode(project_file_path),
            encode(str(last_modified_utc_ticks)),
            encode(version),
        ]
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()
