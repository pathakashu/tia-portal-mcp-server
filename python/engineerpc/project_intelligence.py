"""Project model, graph and search (ports of the three project-intelligence projects).

All three are deterministic and Siemens-free: ordering is ordinal, the snapshot hash uses
length-prefixed fields, and search ranking is exact-match, then prefix, then substring.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from datetime import datetime
from enum import IntEnum
from typing import Iterable, Mapping


class ProjectArtifactType(IntEnum):
    Project = 0
    Device = 1
    Plc = 2
    Block = 3
    DataBlock = 4
    UserDataType = 5
    Tag = 6
    HardwareModule = 7
    HmiDevice = 8
    HmiScreen = 9
    Connection = 10
    CrossReference = 11
    Library = 12


@dataclass(frozen=True)
class ProjectArtifact:
    artifact_id: str
    name: str
    path: str
    type: ProjectArtifactType
    source_hash: str
    metadata: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class ProjectSnapshot:
    project_id: str
    tia_version: str
    captured_at_utc: datetime
    artifacts: tuple[ProjectArtifact, ...]
    snapshot_hash: str


def _append_field(parts: list[str], value: str | None) -> None:
    normalized = value or ""
    parts.append(f"{len(normalized)}:{normalized}|")


def create_project_snapshot(
    project_id: str,
    tia_version: str,
    captured_at_utc: datetime,
    artifacts: Iterable[ProjectArtifact],
) -> ProjectSnapshot:
    if not (project_id or "").strip():
        raise ValueError("projectId is required.")
    if not (tia_version or "").strip():
        raise ValueError("tiaVersion is required.")
    if artifacts is None:
        raise ValueError("artifacts is required.")

    artifact_list = tuple(sorted(artifacts, key=lambda artifact: artifact.artifact_id))

    seen: set[str] = set()
    for artifact in artifact_list:
        if not (artifact.artifact_id or "").strip():
            raise ValueError("Artifact ID is required.")
        if artifact.artifact_id in seen:
            raise ValueError(f"Artifact ID '{artifact.artifact_id}' is duplicated.")
        seen.add(artifact.artifact_id)

    parts: list[str] = []
    _append_field(parts, project_id)
    _append_field(parts, tia_version)
    for artifact in artifact_list:
        _append_field(parts, artifact.artifact_id)
        _append_field(parts, artifact.name)
        _append_field(parts, artifact.path)
        _append_field(parts, artifact.type.name)
        _append_field(parts, artifact.source_hash)
        for key in sorted(artifact.metadata):
            _append_field(parts, key)
            _append_field(parts, artifact.metadata[key])

    snapshot_hash = hashlib.sha256("".join(parts).encode("utf-8")).hexdigest().upper()
    return ProjectSnapshot(project_id, tia_version, captured_at_utc, artifact_list, snapshot_hash)


class ProjectGraphRelationship(IntEnum):
    Contains = 0
    Calls = 1
    Reads = 2
    Writes = 3
    Uses = 4
    References = 5
    DependsOn = 6
    ConnectedTo = 7
    BindsTo = 8


@dataclass(frozen=True)
class ProjectGraphEdge:
    source_artifact_id: str
    relationship: ProjectGraphRelationship
    target_artifact_id: str


@dataclass(frozen=True)
class ProjectGraph:
    snapshot: ProjectSnapshot
    edges: tuple[ProjectGraphEdge, ...]

    def get_outgoing_edges(self, source_artifact_id: str) -> tuple[ProjectGraphEdge, ...]:
        return tuple(
            sorted(
                (edge for edge in self.edges if edge.source_artifact_id == source_artifact_id),
                key=lambda edge: (int(edge.relationship), edge.target_artifact_id),
            )
        )


def create_project_graph(
    snapshot: ProjectSnapshot, edges: Iterable[ProjectGraphEdge]
) -> ProjectGraph:
    if snapshot is None:
        raise ValueError("snapshot is required.")
    if edges is None:
        raise ValueError("edges is required.")

    artifact_ids = {artifact.artifact_id for artifact in snapshot.artifacts}
    edge_list = tuple(
        sorted(
            edges,
            key=lambda edge: (
                edge.source_artifact_id,
                int(edge.relationship),
                edge.target_artifact_id,
            ),
        )
    )

    unique: set[ProjectGraphEdge] = set()
    for edge in edge_list:
        if edge.source_artifact_id not in artifact_ids or edge.target_artifact_id not in artifact_ids:
            raise ValueError(
                f"Graph edge '{edge.source_artifact_id}' -> '{edge.target_artifact_id}' "
                "references an artifact outside the snapshot."
            )
        if edge in unique:
            raise ValueError(
                f"Graph edge '{edge.source_artifact_id}' -> '{edge.target_artifact_id}' is duplicated."
            )
        unique.add(edge)

    return ProjectGraph(snapshot, edge_list)


@dataclass(frozen=True)
class ProjectSearchQuery:
    text: str
    artifact_type: ProjectArtifactType | None = None


def _contains(value: str, term: str) -> bool:
    return term.lower() in (value or "").lower()


def _match_rank(name: str, term: str) -> int:
    if name.lower() == term.lower():
        return 0
    return 1 if name.lower().startswith(term.lower()) else 2


class DeterministicProjectSearchProvider:
    def search(
        self, snapshot: ProjectSnapshot, query: ProjectSearchQuery
    ) -> tuple[ProjectArtifact, ...]:
        if snapshot is None:
            raise ValueError("snapshot is required.")
        if query is None:
            raise ValueError("query is required.")

        term = query.text.strip()
        if not term:
            return ()

        def matches(artifact: ProjectArtifact) -> bool:
            return (
                _contains(artifact.name, term)
                or _contains(artifact.path, term)
                or _contains(artifact.type.name, term)
                or any(
                    _contains(key, term) or _contains(value, term)
                    for key, value in artifact.metadata.items()
                )
            )

        candidates = [
            artifact
            for artifact in snapshot.artifacts
            if (query.artifact_type is None or artifact.type == query.artifact_type)
            and matches(artifact)
        ]
        return tuple(
            sorted(
                candidates,
                key=lambda artifact: (
                    _match_rank(artifact.name, term),
                    artifact.name.lower(),
                    artifact.artifact_id,
                ),
            )
        )
