"""Mock adapter, security authorization, audit sinks and project intelligence."""

from __future__ import annotations

import json
import uuid
from datetime import datetime, timezone

import pytest
from conftest import IDENTITY, NOW_UTC, make_operation, make_principal

from engineerpc.audit import (
    EngineeringAuditEvent,
    EngineeringAuditEventType,
    InMemoryEngineeringAuditSink,
    JsonLinesEngineeringAuditSink,
)
from engineerpc.contracts import ProjectContext
from engineerpc.project_intelligence import (
    DeterministicProjectSearchProvider,
    ProjectArtifact,
    ProjectArtifactType,
    ProjectGraphEdge,
    ProjectGraphRelationship,
    ProjectSearchQuery,
    create_project_graph,
    create_project_snapshot,
)
from engineerpc.security import (
    AuthorizationRule,
    InMemorySecurityEventSink,
    JsonLinesSecurityEventSink,
    ScopeAuthorizationService,
    SecurityEventType,
)
from engineerpc.tia.mock import MockTiaAdapter


# -- mock adapter ----------------------------------------------------------------


async def test_mock_adapter_creates_a_block_and_advances_the_snapshot():
    adapter = MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])

    result = await adapter.create_block_async(make_operation("FB_Motor"))

    assert result.is_success
    assert result.updated_project_context.snapshot_hash != "snapshot-1"


async def test_mock_adapter_detects_duplicate_block_names_case_insensitively():
    adapter = MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])

    first = await adapter.create_block_async(make_operation("FB_Motor"))
    duplicate = await adapter.create_block_async(
        make_operation("fb_motor", snapshot_hash=first.updated_project_context.snapshot_hash)
    )

    assert first.is_success
    assert not duplicate.is_success
    assert "Block 'fb_motor' already exists." in duplicate.errors


async def test_mock_adapter_rejects_a_stale_snapshot():
    adapter = MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])
    await adapter.create_block_async(make_operation("FB_Motor"))

    result = await adapter.create_block_async(make_operation("FC_Safety"))

    assert not result.is_success
    assert "Project snapshot is stale." in result.errors


# -- authorization ---------------------------------------------------------------


def _authorization_service(sink=None) -> ScopeAuthorizationService:
    return ScopeAuthorizationService(
        [AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan")],
        sink or InMemorySecurityEventSink(),
    )


def test_authorization_allows_matching_role_and_scope():
    sink = InMemorySecurityEventSink()

    decision = _authorization_service(sink).authorize(
        make_principal(), "PlanCreateBlock", uuid.uuid4(), uuid.uuid4()
    )

    assert decision.is_allowed
    assert sink.events[0].event_type == SecurityEventType.AuthorizationAllowed


@pytest.mark.parametrize(
    "roles,scopes",
    [(("Viewer",), ("engineering.plan",)), (("Engineer",), ("engineering.read",))],
)
def test_authorization_denies_missing_role_or_scope(roles, scopes):
    sink = InMemorySecurityEventSink()

    decision = _authorization_service(sink).authorize(
        make_principal(roles, scopes), "PlanCreateBlock", uuid.uuid4(), uuid.uuid4()
    )

    assert not decision.is_allowed
    assert "does not satisfy the required role and scope" in decision.denial_reason
    assert sink.events[0].event_type == SecurityEventType.AuthorizationDenied


def test_authorization_defaults_to_deny_for_unconfigured_operations():
    decision = _authorization_service().authorize(
        make_principal(), "SomethingElse", uuid.uuid4(), uuid.uuid4()
    )

    assert not decision.is_allowed
    assert decision.denial_reason == "Operation is not configured for authorization."


# -- audit sinks -----------------------------------------------------------------


def test_engineering_audit_sink_appends_json_lines(tmp_path):
    path = tmp_path / "nested" / "engineering-events.jsonl"
    sink = JsonLinesEngineeringAuditSink(str(path))

    for index in range(2):
        sink.record(
            EngineeringAuditEvent(
                EngineeringAuditEventType.PlanAwaitingApproval,
                NOW_UTC,
                uuid.uuid4(),
                None,
                "project-1",
                "snapshot-1",
                IDENTITY,
                f"entry-{index}",
            )
        )

    lines = path.read_text(encoding="utf-8").splitlines()
    assert len(lines) == 2
    first = json.loads(lines[0])
    # eventType is written as an integer, matching the C# sink's serializer options.
    assert first["eventType"] == int(EngineeringAuditEventType.PlanAwaitingApproval)
    assert first["identity"]["subjectId"] == "engineer-1"
    assert first["detail"] == "entry-0"


def test_security_event_sink_appends_json_lines(tmp_path):
    from engineerpc.security import SecurityEvent

    path = tmp_path / "security-events.jsonl"
    JsonLinesSecurityEventSink(str(path)).record(
        SecurityEvent(
            SecurityEventType.AuthorizationDenied,
            NOW_UTC,
            uuid.uuid4(),
            uuid.uuid4(),
            IDENTITY,
            "PlanCreateBlock",
            "denied",
        )
    )

    entry = json.loads(path.read_text(encoding="utf-8").strip())
    assert entry["eventType"] == int(SecurityEventType.AuthorizationDenied)
    assert entry["operation"] == "PlanCreateBlock"


# -- project intelligence --------------------------------------------------------


def _artifact(artifact_id: str, name: str, **kwargs) -> ProjectArtifact:
    return ProjectArtifact(
        artifact_id=artifact_id,
        name=name,
        path=kwargs.get("path", f"/{name}"),
        type=kwargs.get("type", ProjectArtifactType.Block),
        source_hash=kwargs.get("source_hash", "hash"),
        metadata=kwargs.get("metadata", {}),
    )


def test_snapshot_is_order_independent_and_content_sensitive():
    motor = _artifact("a1", "FB_Motor")
    safety = _artifact("a2", "FC_Safety")

    first = create_project_snapshot("project-1", "V19", NOW_UTC, [motor, safety])
    second = create_project_snapshot("project-1", "V19", NOW_UTC, [safety, motor])
    changed = create_project_snapshot(
        "project-1", "V19", NOW_UTC, [motor, _artifact("a2", "FC_Other")]
    )

    assert first.snapshot_hash == second.snapshot_hash
    assert first.snapshot_hash != changed.snapshot_hash
    assert [a.artifact_id for a in first.artifacts] == ["a1", "a2"]


def test_snapshot_rejects_duplicate_artifact_ids():
    with pytest.raises(ValueError, match="duplicated"):
        create_project_snapshot(
            "project-1", "V19", NOW_UTC, [_artifact("a1", "One"), _artifact("a1", "Two")]
        )


def test_graph_rejects_edges_outside_the_snapshot():
    snapshot = create_project_snapshot("project-1", "V19", NOW_UTC, [_artifact("a1", "FB_Motor")])

    with pytest.raises(ValueError, match="outside the snapshot"):
        create_project_graph(
            snapshot, [ProjectGraphEdge("a1", ProjectGraphRelationship.Calls, "ghost")]
        )


def test_graph_returns_deterministically_ordered_outgoing_edges():
    snapshot = create_project_snapshot(
        "project-1", "V19", NOW_UTC,
        [_artifact("a1", "FB_Motor"), _artifact("a2", "FC_Safety"), _artifact("a3", "DB_Motor")],
    )
    graph = create_project_graph(
        snapshot,
        [
            ProjectGraphEdge("a1", ProjectGraphRelationship.Reads, "a3"),
            ProjectGraphEdge("a1", ProjectGraphRelationship.Calls, "a2"),
        ],
    )

    outgoing = graph.get_outgoing_edges("a1")

    assert [edge.relationship for edge in outgoing] == [
        ProjectGraphRelationship.Calls,
        ProjectGraphRelationship.Reads,
    ]


def test_search_ranks_exact_then_prefix_then_substring():
    snapshot = create_project_snapshot(
        "project-1", "V19", NOW_UTC,
        [
            _artifact("a1", "Motor_Start"),
            _artifact("a2", "Motor"),
            _artifact("a3", "FB_Motor_Guard"),
        ],
    )

    results = DeterministicProjectSearchProvider().search(snapshot, ProjectSearchQuery("Motor"))

    assert [artifact.name for artifact in results] == ["Motor", "Motor_Start", "FB_Motor_Guard"]


def test_search_filters_by_artifact_type_and_ignores_blank_terms():
    snapshot = create_project_snapshot(
        "project-1", "V19", NOW_UTC,
        [
            _artifact("a1", "Motor", type=ProjectArtifactType.Block),
            _artifact("a2", "Motor_Tag", type=ProjectArtifactType.Tag),
        ],
    )
    provider = DeterministicProjectSearchProvider()

    typed = provider.search(
        snapshot, ProjectSearchQuery("Motor", ProjectArtifactType.Tag)
    )

    assert [artifact.artifact_id for artifact in typed] == ["a2"]
    assert provider.search(snapshot, ProjectSearchQuery("   ")) == ()
