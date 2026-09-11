"""Host entry point: ``python -m engineerpc.host`` (or the ``engineerpc-host`` script)."""

from __future__ import annotations

import ssl
import sys
from typing import Callable

import uvicorn

from .app import create_app
from .options import load_options
from .tls import TlsAwareH11Protocol


def _client_auth_ssl_context_factory(
    config: uvicorn.Config, default_factory: Callable[[], ssl.SSLContext]
) -> ssl.SSLContext:
    """Load the client-auth trust anchors Kestrel gets for free from the OS certificate store.

    ``uvicorn.Config``'s own ``ssl_ca_certs`` wants an explicit CA bundle file, which this
    deployment doesn't have a config key for. ``load_default_certs`` pulls the same trusted
    root/intermediate CAs Windows already trusts, matching .NET's default chain-building
    behaviour. The explicit issuer allow-list (``McpTransport__TrustedClientIssuers``) is what
    actually narrows that down to a specific internal CA — see ``ClientCertificateValidator``.
    """
    context = default_factory()
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_default_certs(ssl.Purpose.CLIENT_AUTH)
    return context


def main() -> int:
    transport_options, worker_options, audit_options = load_options()
    app = create_app(transport_options, worker_options, audit_options)

    config_kwargs = {
        "host": "127.0.0.1" if transport_options.allow_insecure_localhost else "0.0.0.0",
        "port": transport_options.port,
        "timeout_keep_alive": transport_options.request_timeout_seconds,
        "h11_max_incomplete_event_size": transport_options.max_request_body_size_bytes,
        "log_level": "info",
    }

    if not transport_options.allow_insecure_localhost:
        # Fail closed: TLS 1.2+, client certificate required and chain-validated (see
        # _client_auth_ssl_context_factory); the issuer allow-list + expiry narrowing then
        # runs in app.py against the already-validated peer certificate.
        config_kwargs.update(
            ssl_certfile=transport_options.server_certificate_path,
            ssl_keyfile=transport_options.server_certificate_path,
            ssl_keyfile_password=transport_options.server_certificate_password,
            ssl_cert_reqs=ssl.CERT_REQUIRED,
            ssl_version=ssl.PROTOCOL_TLS_SERVER,
            ssl_context_factory=_client_auth_ssl_context_factory,
            http=TlsAwareH11Protocol,
        )

    uvicorn.run(app, **config_kwargs)
    return 0


if __name__ == "__main__":
    sys.exit(main())
