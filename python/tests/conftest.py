from __future__ import annotations

import uuid
from datetime import datetime, timedelta, timezone

import pytest

from engineerpc.contracts import AuthenticatedIdentity, AuthenticatedPrincipal, ProjectContext
from engineerpc.ir import (
    BlockInterface,
    BlockParameter,
    BlockType,
    CreateBlockOperation,
    ProgrammingLanguage,
    SclAssignment,
)

NOW_UTC = datetime(2026, 9, 7, 12, 0, 0, tzinfo=timezone.utc)
IDENTITY = AuthenticatedIdentity("engineer-1", "client-1")


@pytest.fixture
def now_utc() -> datetime:
    return NOW_UTC


@pytest.fixture
def identity() -> AuthenticatedIdentity:
    return IDENTITY


def make_operation(
    name: str = "FB_Motor",
    *,
    project_id: str = "project-1",
    snapshot_hash: str = "snapshot-1",
    block_type: BlockType = BlockType.FunctionBlock,
    language: ProgrammingLanguage = ProgrammingLanguage.Scl,
    inputs: tuple[BlockParameter, ...] = (),
    outputs: tuple[BlockParameter, ...] | None = None,
    statements: tuple[SclAssignment, ...] | None = None,
    controller_name: str | None = None,
    operation_id: uuid.UUID | None = None,
    idempotency_key: str = "idempotency-1",
) -> CreateBlockOperation:
    return CreateBlockOperation(
        operation_id=operation_id or uuid.uuid4(),
        project_context=ProjectContext(project_id, snapshot_hash),
        idempotency_key=idempotency_key,
        name=name,
        block_type=block_type,
        language=language,
        interface=BlockInterface(inputs=inputs, outputs=outputs),
        statements=statements,
        controller_name=controller_name,
    )


def make_principal(
    roles: tuple[str, ...] = ("Engineer",),
    scopes: tuple[str, ...] = ("engineering.plan",),
) -> AuthenticatedPrincipal:
    return AuthenticatedPrincipal(IDENTITY, frozenset(roles), frozenset(scopes))


def advancing_clock(start: datetime = NOW_UTC):
    """A mutable clock so tests can move time forward (mirrors the C# TestTimeProvider)."""

    class Clock:
        def __init__(self) -> None:
            self.utc_now = start

        def __call__(self) -> datetime:
            return self.utc_now

        def advance(self, delta: timedelta) -> None:
            self.utc_now += delta

    return Clock()
