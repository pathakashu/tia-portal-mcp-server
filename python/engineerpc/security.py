"""Role/scope authorization with default deny (port of ``EngineerPc.Security``)."""

from __future__ import annotations

import threading
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Any, Callable, Iterable, Protocol

from .audit import _JsonLinesSink
from .contracts import AuthenticatedIdentity, AuthenticatedPrincipal
from .transactions import format_datetime


class SecurityEventType(IntEnum):
    AuthorizationAllowed = 0
    AuthorizationDenied = 1


@dataclass(frozen=True)
class SecurityEvent:
    event_type: SecurityEventType
    occurred_at_utc: datetime
    request_id: uuid.UUID
    correlation_id: uuid.UUID
    identity: AuthenticatedIdentity
    operation: str
    detail: str

    def to_json_obj(self) -> dict[str, Any]:
        return {
            "eventType": int(self.event_type),
            "occurredAtUtc": format_datetime(self.occurred_at_utc),
            "requestId": str(self.request_id),
            "correlationId": str(self.correlation_id),
            "identity": self.identity,
            "operation": self.operation,
            "detail": self.detail,
        }


class SecurityEventSink(Protocol):
    def record(self, security_event: SecurityEvent) -> None: ...


class InMemorySecurityEventSink:
    def __init__(self) -> None:
        self._events: list[SecurityEvent] = []
        self._lock = threading.Lock()

    @property
    def events(self) -> tuple[SecurityEvent, ...]:
        with self._lock:
            return tuple(self._events)

    def record(self, security_event: SecurityEvent) -> None:
        if security_event is None:
            raise ValueError("securityEvent is required.")
        with self._lock:
            self._events.append(security_event)


class JsonLinesSecurityEventSink(_JsonLinesSink):
    def record(self, security_event: SecurityEvent) -> None:
        if security_event is None:
            raise ValueError("securityEvent is required.")
        self._append(security_event)


@dataclass(frozen=True)
class AuthorizationRule:
    operation: str
    required_role: str
    required_scope: str


@dataclass(frozen=True)
class AuthorizationDecision:
    is_allowed: bool
    denial_reason: str | None


class AuthorizationServiceProtocol(Protocol):
    def authorize(
        self,
        principal: AuthenticatedPrincipal,
        operation: str,
        request_id: uuid.UUID,
        correlation_id: uuid.UUID,
    ) -> AuthorizationDecision: ...


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class ScopeAuthorizationService:
    def __init__(
        self,
        rules: Iterable[AuthorizationRule],
        event_sink: SecurityEventSink,
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        if rules is None:
            raise ValueError("rules is required.")
        if event_sink is None:
            raise ValueError("eventSink is required.")

        self._rules = {rule.operation: rule for rule in rules}
        self._event_sink = event_sink
        self._now = time_provider or _utc_now

    def authorize(
        self,
        principal: AuthenticatedPrincipal,
        operation: str,
        request_id: uuid.UUID,
        correlation_id: uuid.UUID,
    ) -> AuthorizationDecision:
        if principal is None:
            raise ValueError("principal is required.")
        if not (operation or "").strip():
            raise ValueError("operation is required.")

        rule = self._rules.get(operation)
        if rule is None:
            return self._deny(
                principal, operation, request_id, correlation_id,
                "Operation is not configured for authorization.",
            )

        has_role = any(role == rule.required_role for role in principal.roles)
        has_scope = any(scope == rule.required_scope for scope in principal.scopes)
        if not has_role or not has_scope:
            return self._deny(
                principal, operation, request_id, correlation_id,
                "Authenticated principal does not satisfy the required role and scope.",
            )

        self._event_sink.record(
            SecurityEvent(
                SecurityEventType.AuthorizationAllowed,
                self._now(),
                request_id,
                correlation_id,
                principal.identity,
                operation,
                "Role and scope authorization succeeded.",
            )
        )
        return AuthorizationDecision(True, None)

    def _deny(
        self,
        principal: AuthenticatedPrincipal,
        operation: str,
        request_id: uuid.UUID,
        correlation_id: uuid.UUID,
        reason: str,
    ) -> AuthorizationDecision:
        self._event_sink.record(
            SecurityEvent(
                SecurityEventType.AuthorizationDenied,
                self._now(),
                request_id,
                correlation_id,
                principal.identity,
                operation,
                reason,
            )
        )
        return AuthorizationDecision(False, reason)
