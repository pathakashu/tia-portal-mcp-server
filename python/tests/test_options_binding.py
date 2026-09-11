"""Configuration binding: appsettings.json defaults plus environment overrides."""

from __future__ import annotations

import json

from engineerpc.host.options import load_options


def test_env_overrides_bind_case_insensitively(tmp_path, monkeypatch):
    """Windows upper-cases os.environ keys, so binding must ignore case."""
    monkeypatch.setenv("MCPTRANSPORT__ALLOWINSECURELOCALHOST", "true")
    monkeypatch.setenv("MCPTRANSPORT__PORT", "9443")
    monkeypatch.setenv("TIAV19WORKER__ENABLED", "true")
    monkeypatch.setenv("TIAV19WORKER__ENABLEBLOCKCATALOGREAD", "true")

    transport, worker, _ = load_options(tmp_path)

    assert transport.allow_insecure_localhost is True
    assert transport.port == 9443
    assert worker.enabled is True
    assert worker.enable_block_catalog_read is True
    assert worker.enable_block_write is False


def test_appsettings_supplies_defaults_that_env_overrides(tmp_path, monkeypatch):
    (tmp_path / "appsettings.json").write_text(
        json.dumps(
            {
                "McpTransport": {"Port": 7443, "AllowInsecureLocalhost": False},
                "TiaV19Worker": {"Enabled": False, "RequestTimeoutSeconds": 30},
            }
        ),
        encoding="utf-8",
    )
    monkeypatch.setenv("McpTransport__AllowInsecureLocalhost", "true")

    transport, worker, _ = load_options(tmp_path)

    assert transport.port == 7443
    assert transport.allow_insecure_localhost is True
    assert worker.request_timeout_seconds == 30


def test_indexed_env_arrays_bind_in_order(tmp_path, monkeypatch):
    monkeypatch.setenv("McpTransport__TrustedClientIssuers__0", "CN=First")
    monkeypatch.setenv("McpTransport__TrustedClientIssuers__1", "CN=Second")
    monkeypatch.setenv("McpTransport__ClientPrincipalMappings__0__CertificateThumbprint", "A" * 40)
    monkeypatch.setenv("McpTransport__ClientPrincipalMappings__0__Roles__0", "Engineer")
    monkeypatch.setenv("McpTransport__ClientPrincipalMappings__0__Scopes__0", "engineering.plan")

    transport, _, _ = load_options(tmp_path)

    assert transport.trusted_client_issuers == ("CN=First", "CN=Second")
    mapping = transport.client_principal_mappings[0]
    assert mapping.certificate_thumbprint == "A" * 40
    assert mapping.roles == ("Engineer",)
    assert mapping.scopes == ("engineering.plan",)
