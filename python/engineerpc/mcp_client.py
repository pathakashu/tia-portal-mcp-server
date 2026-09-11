"""A minimal MCP Streamable HTTP + mTLS client, shared by the acceptance test tool and any
local agent that needs to talk to ``engineerpc.host``. Speaks raw JSON-RPC directly (no ``mcp``
SDK dependency), matching exactly what ``engineerpc.host`` implements: ``initialize`` ->
``notifications/initialized`` -> ``tools/list`` / ``tools/call``, with ``Mcp-Session-Id`` and
``MCP-Protocol-Version`` headers on every call after the handshake.
"""
from __future__ import annotations

import itertools
import ssl
from typing import Any

import httpx

PROTOCOL_VERSION = "2025-06-18"


class McpClientError(Exception):
    pass


class McpConnection:
    """One authenticated MCP session over one mTLS identity."""

    def __init__(self, client: httpx.Client, base_url: str, label: str) -> None:
        self._client = client
        self._base_url = base_url
        self.label = label
        self.session_id: str | None = None
        self._ids = itertools.count(1)

    def _headers(self) -> dict[str, str]:
        headers = {"Content-Type": "application/json"}
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
            headers["MCP-Protocol-Version"] = PROTOCOL_VERSION
        return headers

    def _post(self, body: dict[str, Any]) -> httpx.Response:
        response = self._client.post(self._base_url, json=body, headers=self._headers())
        if response.status_code >= 400:
            raise McpClientError(
                f"{self.label}: HTTP {response.status_code} calling {body.get('method')}: {response.text[:300]}"
            )
        return response

    def initialize(self) -> None:
        response = self._post(
            {
                "jsonrpc": "2.0",
                "id": "init",
                "method": "initialize",
                "params": {
                    "protocolVersion": PROTOCOL_VERSION,
                    "capabilities": {},
                    "clientInfo": {"name": f"engineerpc-client-{self.label}", "version": "1.0.0"},
                },
            }
        )
        session_id = response.headers.get("mcp-session-id")
        if not session_id:
            raise McpClientError(f"{self.label}: initialize did not return an Mcp-Session-Id header.")
        self.session_id = session_id
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def list_tools(self) -> list[dict[str, Any]]:
        response = self._post(
            {"jsonrpc": "2.0", "id": str(next(self._ids)), "method": "tools/list", "params": {}}
        )
        body = response.json()
        if "error" in body:
            raise McpClientError(f"{self.label}: tools/list error: {body['error']}")
        return body["result"]["tools"]

    def call_tool(self, name: str, arguments: dict[str, Any]) -> tuple[dict[str, Any], bool]:
        response = self._post(
            {
                "jsonrpc": "2.0",
                "id": str(next(self._ids)),
                "method": "tools/call",
                "params": {"name": name, "arguments": arguments},
            }
        )
        body = response.json()
        if "error" in body:
            raise McpClientError(f"{self.label}: {name} JSON-RPC error: {body['error']}")
        result = body["result"]
        return result["structuredContent"], bool(result["isError"])


def build_ssl_context(ca_bundle: str | None, cert: tuple[str, str] | None) -> ssl.SSLContext:
    # httpx's own cert=/verify=<path> convenience builds its SSLContext in a way that has been
    # observed to silently break the TLS 1.3 handshake once a client certificate is loaded
    # (fails with "Server disconnected without sending a response", no server-side trace at
    # all -- the same server accepts the identical certificate fine via openssl s_client and
    # via a manually-built ssl.SSLContext). Building the context explicitly avoids it.
    context = ssl.create_default_context(cafile=ca_bundle)
    if cert is not None:
        context.load_cert_chain(cert[0], cert[1])
    return context


def connect(
    base_url: str,
    label: str,
    cert: tuple[str, str] | None = None,
    insecure: bool = False,
    ca_bundle: str | None = None,
    timeout: float = 90.0,
) -> McpConnection:
    verify: Any = False if insecure else build_ssl_context(ca_bundle, cert)
    client = httpx.Client(verify=verify, timeout=timeout)
    connection = McpConnection(client, base_url, label)
    connection.initialize()
    return connection
