"""Host transport behaviour: options validation, static console, and the /mcp endpoint."""

from __future__ import annotations

import json

import pytest
from starlette.testclient import TestClient

from engineerpc.host.app import create_app
from engineerpc.host.jsonrpc import PROTOCOL_VERSION
from engineerpc.host.options import (
    ClientCertificatePrincipalMapping,
    McpAuditOptions,
    McpTransportOptions,
    TiaV19WorkerHostOptions,
    is_valid_thumbprint,
    validate_transport_options,
)
from engineerpc.host.tls import ClientCertificatePrincipalMapper, certificate_thumbprint


@pytest.fixture
def client(tmp_path):
    app = create_app(
        McpTransportOptions(allow_insecure_localhost=True, request_timeout_seconds=30),
        TiaV19WorkerHostOptions(),
        McpAuditOptions(
            file_path=str(tmp_path / "security.jsonl"),
            engineering_file_path=str(tmp_path / "engineering.jsonl"),
        ),
    )
    with TestClient(app) as test_client:
        yield test_client


def test_health_reports_ready(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ready"}


def test_dev_console_is_served_at_root(client):
    response = client.get("/")

    assert response.status_code == 200
    assert "Engineer-PC MCP Console" in response.text


def test_mcp_rejects_get(client):
    response = client.get("/mcp")

    assert response.status_code == 405
    assert response.headers["allow"] == "POST"


def test_mcp_rejects_non_json_content_type(client):
    response = client.post("/mcp", content="{}", headers={"Content-Type": "text/plain"})

    assert response.status_code == 415


def test_mcp_rejects_cross_origin_requests(client):
    response = client.post(
        "/mcp",
        content="{}",
        headers={"Content-Type": "application/json", "Origin": "https://evil.example"},
    )

    assert response.status_code == 403


def test_mcp_handshake_and_tools_list(client):
    initialize = client.post(
        "/mcp",
        content=json.dumps(
            {
                "jsonrpc": "2.0",
                "id": "1",
                "method": "initialize",
                "params": {
                    "protocolVersion": PROTOCOL_VERSION,
                    "capabilities": {},
                    "clientInfo": {"name": "test", "version": "1.0.0"},
                },
            }
        ),
        headers={"Content-Type": "application/json"},
    )
    session_id = initialize.headers["mcp-session-id"]
    headers = {
        "Content-Type": "application/json",
        "Mcp-Session-Id": session_id,
        "MCP-Protocol-Version": PROTOCOL_VERSION,
    }

    notified = client.post(
        "/mcp",
        content=json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        headers=headers,
    )
    listed = client.post(
        "/mcp",
        content=json.dumps({"jsonrpc": "2.0", "id": "2", "method": "tools/list", "params": {}}),
        headers=headers,
    )

    assert initialize.status_code == 200
    assert session_id
    assert notified.status_code == 202
    assert listed.status_code == 200
    body = listed.json()
    names = [tool["name"] for tool in body["result"]["tools"]]
    assert names == ["plan_create_block", "preview_scl_block"]


# -- fail-closed configuration ---------------------------------------------------


def test_production_options_require_certificates_and_mappings():
    result = validate_transport_options(McpTransportOptions())

    assert not result.is_valid
    assert (
        "A server certificate path is required when insecure localhost mode is disabled."
        in result.errors
    )
    assert (
        "At least one trusted client issuer is required when insecure localhost mode is disabled."
        in result.errors
    )
    assert (
        "At least one client certificate principal mapping is required when insecure localhost mode is disabled."
        in result.errors
    )


def test_production_options_reject_invalid_mappings():
    result = validate_transport_options(
        McpTransportOptions(
            server_certificate_path="C:/certs/server.pfx",
            trusted_client_issuers=("CN=Issuer",),
            client_principal_mappings=(
                ClientCertificatePrincipalMapping("not-a-thumbprint", ("Engineer",), ("engineering.plan",)),
                ClientCertificatePrincipalMapping("A" * 40, (), ()),
            ),
        )
    )

    assert not result.is_valid
    assert (
        "Each client certificate principal mapping must contain a SHA-1 certificate thumbprint."
        in result.errors
    )
    assert "Each client certificate principal mapping must contain at least one role." in result.errors
    assert "Each client certificate principal mapping must contain at least one scope." in result.errors


def test_out_of_range_transport_values_are_rejected():
    result = validate_transport_options(
        McpTransportOptions(
            allow_insecure_localhost=True,
            port=0,
            max_request_body_size_bytes=0,
            request_timeout_seconds=0,
            session_duration_seconds=0,
        )
    )

    assert set(result.errors) == {
        "MCP transport port must be between 1 and 65535.",
        "MCP transport message size must be between 1 and 10485760 bytes.",
        "MCP transport request timeout must be between 1 and 300 seconds.",
        "MCP session duration must be between 1 and 3600 seconds.",
    }


def test_creating_an_app_with_invalid_options_fails_closed():
    with pytest.raises(RuntimeError, match="server certificate path is required"):
        create_app(McpTransportOptions(), TiaV19WorkerHostOptions(), McpAuditOptions())


def test_thumbprint_validation():
    assert is_valid_thumbprint("a" * 40)
    assert is_valid_thumbprint("AB CD " + "0" * 36)  # whitespace is stripped before checking
    assert not is_valid_thumbprint("short")
    assert not is_valid_thumbprint(None)


def test_certificate_mapper_binds_only_allow_listed_thumbprints():
    certificate = b"pretend-der-bytes"
    thumbprint = certificate_thumbprint(certificate)
    mapper = ClientCertificatePrincipalMapper(
        [ClientCertificatePrincipalMapping(thumbprint, ("Engineer",), ("engineering.plan",))]
    )

    mapped = mapper.try_map(certificate, "CN=engineer")
    unmapped = mapper.try_map(b"other-certificate", "CN=other")

    assert mapped is not None
    assert mapped.roles == frozenset({"Engineer"})
    assert mapped.scopes == frozenset({"engineering.plan"})
    assert mapped.identity.client_id == thumbprint
    assert unmapped is None
    assert mapper.try_map(None) is None


def test_worker_options_gate_tool_exposure(tmp_path):
    """Block-write tools stay hidden unless the worker is enabled too."""
    app = create_app(
        McpTransportOptions(allow_insecure_localhost=True),
        TiaV19WorkerHostOptions(enabled=False, enable_block_write=True),
        McpAuditOptions(
            file_path=str(tmp_path / "s.jsonl"),
            engineering_file_path=str(tmp_path / "e.jsonl"),
        ),
    )

    processor = app.state.request_processor
    assert processor._block_write_enabled is False
