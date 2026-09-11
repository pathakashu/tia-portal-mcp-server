"""Deployment configuration (port of the C# host's options + validators).

Binding mirrors ASP.NET Core: ``appsettings.json`` supplies defaults and environment
variables of the form ``Section__Key`` (arrays as ``Section__Key__0``) override them, so
existing deployment scripts keep working unchanged.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping


@dataclass(frozen=True)
class ClientCertificatePrincipalMapping:
    certificate_thumbprint: str = ""
    roles: tuple[str, ...] = ()
    scopes: tuple[str, ...] = ()


@dataclass(frozen=True)
class McpTransportOptions:
    port: int = 7443
    allow_insecure_localhost: bool = False
    server_certificate_path: str | None = None
    server_certificate_password: str | None = None
    trusted_client_issuers: tuple[str, ...] = ()
    max_request_body_size_bytes: int = 1_048_576
    request_timeout_seconds: int = 30
    session_duration_seconds: int = 300
    client_principal_mappings: tuple[ClientCertificatePrincipalMapping, ...] = ()


@dataclass(frozen=True)
class TiaV19WorkerHostOptions:
    enabled: bool = False
    enable_block_catalog_read: bool = False
    enable_block_write: bool = False
    worker_executable_path: str | None = None
    configuration_path: str | None = None
    request_timeout_seconds: int = 30


def _default_audit_dir() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    base = Path(local_app_data) if local_app_data else Path.home() / ".local" / "share"
    return base / "EngineerPc" / "audit"


@dataclass(frozen=True)
class McpAuditOptions:
    file_path: str = field(default_factory=lambda: str(_default_audit_dir() / "security-events.jsonl"))
    engineering_file_path: str = field(
        default_factory=lambda: str(_default_audit_dir() / "engineering-events.jsonl")
    )


@dataclass(frozen=True)
class ValidationResult:
    errors: tuple[str, ...]

    @property
    def is_valid(self) -> bool:
        return len(self.errors) == 0


_THUMBPRINT = re.compile(r"^[0-9A-F]{40}$")


def is_valid_thumbprint(thumbprint: str | None) -> bool:
    if not (thumbprint or "").strip():
        return False
    normalized = "".join(ch for ch in thumbprint if not ch.isspace()).upper()
    return _THUMBPRINT.match(normalized) is not None


def validate_transport_options(options: McpTransportOptions) -> ValidationResult:
    if options is None:
        raise ValueError("options is required.")

    errors: list[str] = []

    if not 1 <= options.port <= 65535:
        errors.append("MCP transport port must be between 1 and 65535.")
    if not 1 <= options.max_request_body_size_bytes <= 10_485_760:
        errors.append("MCP transport message size must be between 1 and 10485760 bytes.")
    if not 1 <= options.request_timeout_seconds <= 300:
        errors.append("MCP transport request timeout must be between 1 and 300 seconds.")
    if not 1 <= options.session_duration_seconds <= 3_600:
        errors.append("MCP session duration must be between 1 and 3600 seconds.")

    if not options.allow_insecure_localhost:
        if not (options.server_certificate_path or "").strip():
            errors.append(
                "A server certificate path is required when insecure localhost mode is disabled."
            )
        if len(options.trusted_client_issuers) == 0:
            errors.append(
                "At least one trusted client issuer is required when insecure localhost mode is disabled."
            )
        if len(options.client_principal_mappings) == 0:
            errors.append(
                "At least one client certificate principal mapping is required when insecure localhost mode is disabled."
            )

        seen: set[str] = set()
        for mapping in options.client_principal_mappings:
            if not is_valid_thumbprint(mapping.certificate_thumbprint):
                errors.append(
                    "Each client certificate principal mapping must contain a SHA-1 certificate thumbprint."
                )
            elif mapping.certificate_thumbprint.upper() in seen:
                errors.append(
                    "Client certificate principal mappings must not contain duplicate thumbprints."
                )
            else:
                seen.add(mapping.certificate_thumbprint.upper())

            if len(mapping.roles) == 0 or any(not (r or "").strip() for r in mapping.roles):
                errors.append(
                    "Each client certificate principal mapping must contain at least one role."
                )
            if len(mapping.scopes) == 0 or any(not (s or "").strip() for s in mapping.scopes):
                errors.append(
                    "Each client certificate principal mapping must contain at least one scope."
                )

    return ValidationResult(tuple(errors))


# -- configuration binding -------------------------------------------------------


def _load_appsettings(directory: Path) -> dict[str, Any]:
    path = directory / "appsettings.json"
    if not path.is_file():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError:
        return {}


def _env_overrides(section: str) -> dict[str, Any]:
    """Collect ``Section__Key`` / ``Section__Key__0`` environment variables.

    Matched case-insensitively: Windows upper-cases the keys in ``os.environ``, so an
    exact-case prefix check would silently ignore every variable on the target platform.
    """
    prefix = f"{section}__".lower()
    collected: dict[str, Any] = {}
    for name, value in os.environ.items():
        if not name.lower().startswith(prefix):
            continue
        path = name[len(prefix) :].split("__")
        cursor: dict[str, Any] = collected
        for part in path[:-1]:
            cursor = cursor.setdefault(part, {})
        cursor[path[-1]] = value
    return collected


def _merge(base: Mapping[str, Any] | None, override: Mapping[str, Any] | None) -> dict[str, Any]:
    """Case-insensitive deep merge.

    Environment keys arrive upper-cased on Windows while appsettings.json uses PascalCase;
    matching case-sensitively would leave both copies in place and let the file win.
    """
    merged: dict[str, Any] = dict(base or {})
    for key, value in (override or {}).items():
        existing_key = next((k for k in merged if k.lower() == key.lower()), key)
        existing = merged.get(existing_key)
        if isinstance(value, Mapping) and isinstance(existing, Mapping):
            merged[existing_key] = _merge(existing, value)
        else:
            merged[existing_key] = value
    return merged


def _get(source: Mapping[str, Any], name: str, default: Any = None) -> Any:
    for key, value in source.items():
        if key.lower() == name.lower():
            return value
    return default


def _as_bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"true", "1", "yes", "on"}


def _as_int(value: Any, default: int) -> int:
    if value is None:
        return default
    try:
        return int(str(value))
    except ValueError:
        return default


def _as_list(value: Any) -> tuple[Any, ...]:
    """Accept both a JSON array and ASP.NET Core's indexed-key env var form."""
    if value is None:
        return ()
    if isinstance(value, (list, tuple)):
        return tuple(value)
    if isinstance(value, Mapping):
        def sort_key(item: tuple[str, Any]) -> tuple[int, str]:
            return (int(item[0]), "") if item[0].isdigit() else (1 << 30, item[0])

        return tuple(v for _, v in sorted(value.items(), key=sort_key))
    return (value,)


def load_options(
    content_root: Path | None = None,
) -> tuple[McpTransportOptions, TiaV19WorkerHostOptions, McpAuditOptions]:
    directory = content_root or Path(__file__).parent
    settings = _load_appsettings(directory)

    transport_raw = _merge(_get(settings, "McpTransport", {}) or {}, _env_overrides("McpTransport"))
    worker_raw = _merge(_get(settings, "TiaV19Worker", {}) or {}, _env_overrides("TiaV19Worker"))
    audit_raw = _merge(_get(settings, "McpAudit", {}) or {}, _env_overrides("McpAudit"))

    mappings = []
    for raw in _as_list(_get(transport_raw, "ClientPrincipalMappings")):
        if not isinstance(raw, Mapping):
            continue
        mappings.append(
            ClientCertificatePrincipalMapping(
                certificate_thumbprint=str(_get(raw, "CertificateThumbprint", "") or ""),
                roles=tuple(str(r) for r in _as_list(_get(raw, "Roles"))),
                scopes=tuple(str(s) for s in _as_list(_get(raw, "Scopes"))),
            )
        )

    transport = McpTransportOptions(
        port=_as_int(_get(transport_raw, "Port"), 7443),
        allow_insecure_localhost=_as_bool(_get(transport_raw, "AllowInsecureLocalhost"), False),
        server_certificate_path=_get(transport_raw, "ServerCertificatePath"),
        server_certificate_password=_get(transport_raw, "ServerCertificatePassword"),
        trusted_client_issuers=tuple(
            str(i) for i in _as_list(_get(transport_raw, "TrustedClientIssuers"))
        ),
        max_request_body_size_bytes=_as_int(
            _get(transport_raw, "MaxRequestBodySizeBytes"), 1_048_576
        ),
        request_timeout_seconds=_as_int(_get(transport_raw, "RequestTimeoutSeconds"), 30),
        session_duration_seconds=_as_int(_get(transport_raw, "SessionDurationSeconds"), 300),
        client_principal_mappings=tuple(mappings),
    )

    worker = TiaV19WorkerHostOptions(
        enabled=_as_bool(_get(worker_raw, "Enabled"), False),
        enable_block_catalog_read=_as_bool(_get(worker_raw, "EnableBlockCatalogRead"), False),
        enable_block_write=_as_bool(_get(worker_raw, "EnableBlockWrite"), False),
        worker_executable_path=_get(worker_raw, "WorkerExecutablePath"),
        configuration_path=_get(worker_raw, "ConfigurationPath"),
        request_timeout_seconds=_as_int(_get(worker_raw, "RequestTimeoutSeconds"), 30),
    )

    defaults = McpAuditOptions()
    audit = McpAuditOptions(
        file_path=str(_get(audit_raw, "FilePath", defaults.file_path) or defaults.file_path),
        engineering_file_path=str(
            _get(audit_raw, "EngineeringFilePath", defaults.engineering_file_path)
            or defaults.engineering_file_path
        ),
    )

    return transport, worker, audit
