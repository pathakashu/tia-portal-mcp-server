"""Siemens-free mock adapter (port of ``EngineerPc.Tia.Mock``)."""

from __future__ import annotations

import asyncio
import hashlib
from typing import Iterable

from ..contracts import ProjectContext
from ..ir import CreateBlockOperation
from .abstractions import TiaAdapterExecutionResult


class _MockProject:
    def __init__(self, snapshot_hash: str) -> None:
        self.snapshot_hash = snapshot_hash
        # Case-insensitive membership (C# HashSet with OrdinalIgnoreCase), while the
        # originally-supplied casing is what gets hashed.
        self.block_names: dict[str, str] = {}

    def add_block(self, name: str) -> bool:
        key = name.lower()
        if key in self.block_names:
            return False
        self.block_names[key] = name
        return True


class MockTiaAdapter:
    def __init__(self, initial_projects: Iterable[ProjectContext] | None = None) -> None:
        self._projects: dict[str, _MockProject] = {}
        self._locks: dict[str, asyncio.Lock] = {}
        for project_context in initial_projects or ():
            self._projects.setdefault(
                project_context.project_id, _MockProject(project_context.snapshot_hash)
            )

    async def get_project_context_async(self, project_id: str) -> ProjectContext | None:
        project = self._projects.get(project_id)
        return ProjectContext(project_id, project.snapshot_hash) if project else None

    async def create_block_async(
        self,
        operation: CreateBlockOperation,
        scl_source_text: str | None = None,
    ) -> TiaAdapterExecutionResult:
        if operation is None:
            raise ValueError("operation is required.")

        project_id = operation.project_context.project_id
        write_lock = self._locks.setdefault(project_id, asyncio.Lock())
        async with write_lock:
            project = self._projects.get(project_id)
            if project is None:
                project = _MockProject(operation.project_context.snapshot_hash)
                self._projects[project_id] = project

            if project.snapshot_hash != operation.project_context.snapshot_hash:
                return TiaAdapterExecutionResult(None, ("Project snapshot is stale.",))

            if not project.add_block(operation.name):
                return TiaAdapterExecutionResult(
                    None, (f"Block '{operation.name}' already exists.",)
                )

            project.snapshot_hash = calculate_snapshot_hash(
                project_id, project.block_names.values()
            )
            return TiaAdapterExecutionResult(
                ProjectContext(project_id, project.snapshot_hash), ()
            )


def calculate_snapshot_hash(project_id: str, block_names: Iterable[str]) -> str:
    """Ordinal-sorted canonical form, SHA-256, upper-case hex (matches Convert.ToHexString)."""
    canonical = project_id + "\n" + "\n".join(sorted(block_names))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest().upper()
