"""TIA adapter contracts (port of ``EngineerPc.Tia.Abstractions``)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Protocol

from ..contracts import ProjectContext
from ..ir import CreateBlockOperation


@dataclass(frozen=True)
class TiaAdapterExecutionResult:
    updated_project_context: ProjectContext | None
    errors: tuple[str, ...]

    @property
    def is_success(self) -> bool:
        return self.updated_project_context is not None and len(self.errors) == 0


@dataclass(frozen=True)
class ProjectBlock:
    controller_name: str
    name: str
    namespace: str
    number: int
    programming_language: str

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "controllerName": self.controller_name,
            "name": self.name,
            "namespace": self.namespace,
            "number": self.number,
            "programmingLanguage": self.programming_language,
        }


@dataclass(frozen=True)
class ProjectBlockCatalogPage:
    project_context: ProjectContext
    blocks: tuple[ProjectBlock, ...]
    total_block_count: int
    next_start_index: int | None

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "projectContext": self.project_context,
            "blocks": list(self.blocks),
            "totalBlockCount": self.total_block_count,
            "nextStartIndex": self.next_start_index,
        }


class ProjectBlockCatalogSnapshotChangedError(Exception):
    def __init__(self) -> None:
        super().__init__("PLC block catalog snapshot has changed.")


class TiaAdapter(Protocol):
    async def get_project_context_async(self, project_id: str) -> ProjectContext | None: ...

    async def create_block_async(
        self,
        operation: CreateBlockOperation,
        scl_source_text: str | None = None,
    ) -> TiaAdapterExecutionResult: ...


class ProjectBlockCatalogReader(Protocol):
    async def get_block_catalog_page_async(
        self,
        project_id: str,
        start_index: int,
        maximum_block_count: int,
        expected_snapshot_hash: str | None,
    ) -> ProjectBlockCatalogPage | None: ...
