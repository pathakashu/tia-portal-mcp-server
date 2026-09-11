"""MCP session lifecycle with expiry and replay protection."""

from __future__ import annotations

import threading
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Callable

from ..contracts import AuthenticatedPrincipal


class McpSessionState(IntEnum):
    Connected = 0
    Authenticated = 1
    Ready = 2
    Disconnected = 3
    Expired = 4


@dataclass(frozen=True)
class McpSession:
    session_id: uuid.UUID
    principal: AuthenticatedPrincipal | None
    state: McpSessionState
    created_at_utc: datetime
    expires_at_utc: datetime


class McpSessionError(Exception):
    """Mirrors the InvalidOperationException raised by the C# session manager."""


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class McpSessionManager:
    def __init__(self, time_provider: Callable[[], datetime] | None = None) -> None:
        self._sessions: dict[uuid.UUID, McpSession] = {}
        self._processed_request_ids: dict[uuid.UUID, set[uuid.UUID]] = {}
        self._now = time_provider or _utc_now
        self._lock = threading.RLock()

    def connect(self, duration: timedelta) -> McpSession:
        if duration <= timedelta(0):
            raise ValueError("Session duration must be positive.")

        now_utc = self._now()
        session = McpSession(
            session_id=uuid.uuid4(),
            principal=None,
            state=McpSessionState.Connected,
            created_at_utc=now_utc,
            expires_at_utc=now_utc + duration,
        )
        with self._lock:
            self._sessions[session.session_id] = session
        return session

    def authenticate(
        self, session_id: uuid.UUID, validated_principal: AuthenticatedPrincipal
    ) -> McpSession:
        if validated_principal is None:
            raise ValueError("validatedPrincipal is required.")
        return self._transition(
            session_id, McpSessionState.Connected, McpSessionState.Authenticated, validated_principal
        )

    def initialize(self, session_id: uuid.UUID) -> McpSession:
        return self._transition(
            session_id, McpSessionState.Authenticated, McpSessionState.Ready, None
        )

    def try_get_ready_session(self, session_id: uuid.UUID) -> tuple[McpSession | None, str | None]:
        with self._lock:
            stored = self._sessions.get(session_id)
            if stored is None:
                return None, "MCP session was not found."

            if stored.expires_at_utc <= self._now():
                self._sessions[session_id] = replace(stored, state=McpSessionState.Expired)
                return None, "MCP session has expired."

            if stored.state != McpSessionState.Ready:
                return None, "MCP session is not ready for tool calls."

            return stored, None

    def try_record_request(
        self, session_id: uuid.UUID, request_id: uuid.UUID
    ) -> tuple[bool, str | None]:
        if request_id == uuid.UUID(int=0):
            return False, "Request ID is required."

        with self._lock:
            processed = self._processed_request_ids.setdefault(session_id, set())
            if request_id in processed:
                return False, "Request ID has already been processed for this session."
            processed.add(request_id)
            return True, None

    def _transition(
        self,
        session_id: uuid.UUID,
        expected_state: McpSessionState,
        next_state: McpSessionState,
        principal: AuthenticatedPrincipal | None,
    ) -> McpSession:
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                raise McpSessionError("MCP session was not found.")

            if session.expires_at_utc <= self._now():
                self._sessions[session_id] = replace(session, state=McpSessionState.Expired)
                raise McpSessionError("MCP session has expired.")

            if session.state != expected_state:
                raise McpSessionError(
                    f"MCP session cannot transition from '{session.state.name}' "
                    f"to '{next_state.name}'."
                )

            next_session = replace(
                session, state=next_state, principal=principal or session.principal
            )
            self._sessions[session_id] = next_session
            return next_session
