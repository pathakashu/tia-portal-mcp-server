"""Client-certificate plumbing for mTLS (port of the C# certificate validator/mapper).

uvicorn does not surface the peer certificate in the ASGI scope, so
:class:`TlsAwareH11Protocol` injects it under the ASGI TLS extension key. Everything else
here is pure logic and unit-tested: issuer/expiry validation and the thumbprint-to-principal
allow-list, which is local deployment configuration and never taken from MCP input.
"""

from __future__ import annotations

import hashlib
import ssl
from datetime import datetime, timezone
from typing import Any, Callable, Iterable, Mapping

from ..contracts import AuthenticatedIdentity, AuthenticatedPrincipal
from .options import ClientCertificatePrincipalMapping

try:  # pragma: no cover - exercised only when the host actually runs
    from uvicorn.protocols.http.h11_impl import H11Protocol
except ImportError:  # pragma: no cover
    H11Protocol = object  # type: ignore[assignment,misc]


def _normalize_thumbprint(thumbprint: str) -> str:
    return "".join(ch for ch in thumbprint if not ch.isspace()).upper()


def certificate_thumbprint(certificate_der: bytes) -> str:
    """SHA-1 thumbprint, upper-case hex — the same value Windows shows for a certificate."""
    return hashlib.sha1(certificate_der).hexdigest().upper()


class ClientCertificatePrincipalMapper:
    def __init__(self, mappings: Iterable[ClientCertificatePrincipalMapping]) -> None:
        if mappings is None:
            raise ValueError("mappings is required.")
        self._mappings = {
            _normalize_thumbprint(mapping.certificate_thumbprint): mapping for mapping in mappings
        }

    def try_map(
        self, certificate_der: bytes | None, subject: str | None = None
    ) -> AuthenticatedPrincipal | None:
        if not certificate_der:
            return None

        thumbprint = certificate_thumbprint(certificate_der)
        mapping = self._mappings.get(_normalize_thumbprint(thumbprint))
        if mapping is None:
            return None

        return AuthenticatedPrincipal(
            identity=AuthenticatedIdentity(subject or thumbprint, thumbprint),
            roles=frozenset(mapping.roles),
            scopes=frozenset(mapping.scopes),
        )


class ClientCertificateValidator:
    """Trusted-issuer and validity-window checks, mirroring the C# validator."""

    def __init__(
        self,
        trusted_issuers: Iterable[str],
        time_provider: Callable[[], datetime] | None = None,
    ) -> None:
        if trusted_issuers is None:
            raise ValueError("trustedIssuers is required.")
        self._trusted_issuers = set(trusted_issuers)
        self._now = time_provider or (lambda: datetime.now(timezone.utc))

    def validate(self, peer_cert: Mapping[str, Any] | None) -> bool:
        """``peer_cert`` is ssl's decoded ``getpeercert()`` dictionary."""
        if not peer_cert:
            return False

        now_utc = self._now()
        not_before = _parse_ssl_time(peer_cert.get("notBefore"))
        not_after = _parse_ssl_time(peer_cert.get("notAfter"))
        if not_before is None or not_after is None:
            return False
        if not_before > now_utc or not_after <= now_utc:
            return False

        return _format_issuer(peer_cert.get("issuer")) in self._trusted_issuers


def _parse_ssl_time(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.strptime(value, "%b %d %H:%M:%S %Y %Z").replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def _format_issuer(issuer: Any) -> str:
    """Render ssl's nested RDN tuples the way X509Certificate2.Issuer does."""
    if not issuer:
        return ""
    parts: list[str] = []
    for rdn in reversed(issuer):
        for key, value in rdn:
            parts.append(f"{_OID_SHORT_NAMES.get(key, key)}={value}")
    return ", ".join(parts)


_OID_SHORT_NAMES = {
    "commonName": "CN",
    "organizationName": "O",
    "organizationalUnitName": "OU",
    "countryName": "C",
    "stateOrProvinceName": "S",
    "localityName": "L",
    "emailAddress": "E",
}


class TlsAwareH11Protocol(H11Protocol):  # pragma: no cover - requires a live TLS socket
    """Injects the peer certificate into the ASGI scope under ``extensions.tls``.

    Pass via ``uvicorn.Config(..., http=TlsAwareH11Protocol)``.
    """

    def connection_made(self, transport):  # type: ignore[override]
        ssl_object: ssl.SSLObject | None = transport.get_extra_info("ssl_object")
        self._peer_cert_der = ssl_object.getpeercert(binary_form=True) if ssl_object else None
        self._peer_cert_dict = ssl_object.getpeercert() if ssl_object else None
        super().connection_made(transport)

    @property
    def scope(self):
        return getattr(self, "_engineerpc_scope", None)

    @scope.setter
    def scope(self, value):
        if value is not None and getattr(self, "_peer_cert_der", None) is not None:
            extensions = value.setdefault("extensions", {})
            extensions["tls"] = {
                "client_cert_der": self._peer_cert_der,
                "client_cert_dict": self._peer_cert_dict,
            }
        self._engineerpc_scope = value
