"""Canonical project and identity contracts (port of ``EngineerPc.Contracts``)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from .dotnet_json import get_ci


@dataclass(frozen=True)
class AuthenticatedIdentity:
    subject_id: str
    client_id: str

    def to_json_obj(self) -> dict[str, Any]:
        return {"subjectId": self.subject_id, "clientId": self.client_id}


@dataclass(frozen=True)
class AuthenticatedPrincipal:
    identity: AuthenticatedIdentity
    roles: frozenset[str]
    scopes: frozenset[str]


@dataclass(frozen=True)
class ProjectContext:
    project_id: str
    snapshot_hash: str

    def to_json_obj(self) -> dict[str, Any]:
        return {"projectId": self.project_id, "snapshotHash": self.snapshot_hash}

    @staticmethod
    def from_json_obj(source: Mapping[str, Any] | None) -> "ProjectContext":
        return ProjectContext(
            project_id=get_ci(source, "projectId", "") or "",
            snapshot_hash=get_ci(source, "snapshotHash", "") or "",
        )
