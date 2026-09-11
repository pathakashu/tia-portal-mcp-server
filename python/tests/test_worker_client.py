"""Worker-client bridge: request serialisation, response validation, fail-closed writes."""

from __future__ import annotations

import asyncio
import json
import uuid
from datetime import timedelta

import pytest
from conftest import make_operation

from engineerpc import dotnet_json
from engineerpc.contracts import ProjectContext
from engineerpc.tia import v19_protocol as protocol
from engineerpc.tia.abstractions import ProjectBlockCatalogSnapshotChangedError
from engineerpc.tia.v19_client import (
    TiaV19WorkerClient,
    TiaV19WorkerClientOptions,
    validate_options,
)


@pytest.fixture
def worker_paths(tmp_path):
    exe = tmp_path / "EngineerPc.Tia.V19.Worker.exe"
    exe.write_text("", encoding="utf-8")
    config = tmp_path / "tia-v19-worker.json"
    config.write_text("{}", encoding="utf-8")
    return str(exe), str(config)


@pytest.fixture
def options(worker_paths):
    exe, config = worker_paths
    return TiaV19WorkerClientOptions(exe, config, timedelta(seconds=5))


class ControlledTransport:
    """Records requests and returns canned responses, like the C# test transport."""

    def __init__(self, response_factory, delay: float | None = None) -> None:
        self._response_factory = response_factory
        self._delay = delay
        self.requests: list[protocol.WorkerRequest] = []
        self._active = 0
        self.max_concurrent_requests = 0

    async def send_async(self, options, request):
        self._active += 1
        self.max_concurrent_requests = max(self.max_concurrent_requests, self._active)
        try:
            if self._delay:
                await asyncio.sleep(self._delay)
            self.requests.append(request)
            return self._response_factory(request)
        finally:
            self._active -= 1


def _context_response(request, project_id="project-1", snapshot_hash="snapshot-1", error=None):
    return json.dumps(
        {
            "requestId": request.request_id,
            "projectId": project_id,
            "snapshotHash": snapshot_hash,
            "error": error,
        }
    )


def _catalog_response(request, **overrides):
    payload = {
        "requestId": request.request_id,
        "projectId": "project-1",
        "snapshotHash": "snapshot-1",
        "blocks": [
            {
                "controllerName": "PLC_1",
                "name": "FB_Motor",
                "namespace": "",
                "number": 1,
                "programmingLanguage": "Scl",
            }
        ],
        "totalBlockCount": 2,
        "nextStartIndex": 1,
        "errorCode": 0,
        "error": None,
    }
    payload.update(overrides)
    return json.dumps(payload)


def _create_block_response(request, **overrides):
    payload = {
        "requestId": request.request_id,
        "projectId": "project-1",
        "snapshotHash": "snapshot-2",
        "errorCode": 0,
        "error": None,
    }
    payload.update(overrides)
    return json.dumps(payload)


def test_options_validator_rejects_noncanonical_or_missing_paths(tmp_path):
    result = validate_options(
        TiaV19WorkerClientOptions(str(tmp_path / "." / "missing.exe"), "relative.json", timedelta(0))
    )

    assert not result.is_valid
    assert "TIA V19 worker configuration path must be absolute." in result.errors
    assert "TIA V19 worker request timeout must be between 1 second and 5 minutes." in result.errors


async def test_get_project_context_returns_context(options):
    transport = ControlledTransport(_context_response)
    client = TiaV19WorkerClient(options, transport)

    result = await client.get_project_context_async("project-1")

    assert result == ProjectContext("project-1", "snapshot-1")
    assert transport.requests[0].protocol_version == protocol.VERSION
    assert transport.requests[0].method == protocol.GET_PROJECT_CONTEXT_METHOD


async def test_get_project_context_returns_none_on_worker_error(options):
    transport = ControlledTransport(lambda r: _context_response(r, None, None, "not found"))
    client = TiaV19WorkerClient(options, transport)

    assert await client.get_project_context_async("unknown") is None


async def test_get_project_context_rejects_mismatched_request_id(options):
    transport = ControlledTransport(
        lambda r: json.dumps(
            {"requestId": "other", "projectId": "project-1", "snapshotHash": "s", "error": None}
        )
    )
    client = TiaV19WorkerClient(options, transport)

    with pytest.raises(RuntimeError, match="did not match the request ID"):
        await client.get_project_context_async("project-1")


async def test_requests_are_serialised(options):
    transport = ControlledTransport(
        lambda r: _context_response(r, r.project_id, "snapshot"), delay=0.02
    )
    client = TiaV19WorkerClient(options, transport)

    await asyncio.gather(
        client.get_project_context_async("project-1"),
        client.get_project_context_async("project-2"),
    )

    assert len(transport.requests) == 2
    assert transport.max_concurrent_requests == 1


async def test_block_catalog_maps_read_only_metadata(options):
    transport = ControlledTransport(_catalog_response)
    client = TiaV19WorkerClient(options, transport)

    page = await client.get_block_catalog_page_async("project-1", 0, 1, None)

    assert page.project_context == ProjectContext("project-1", "snapshot-1")
    assert page.blocks[0].name == "FB_Motor"
    assert page.total_block_count == 2
    assert page.next_start_index == 1


async def test_block_catalog_rejects_non_progressing_continuation(options):
    transport = ControlledTransport(lambda r: _catalog_response(r, nextStartIndex=0))
    client = TiaV19WorkerClient(options, transport)

    with pytest.raises(RuntimeError, match="invalid block catalog continuation"):
        await client.get_block_catalog_page_async("project-1", 0, 1, None)


async def test_block_catalog_rejects_page_beyond_declared_total(options):
    transport = ControlledTransport(
        lambda r: _catalog_response(
            r,
            blocks=[
                {"controllerName": "PLC_1", "name": "A", "namespace": "", "number": 1, "programmingLanguage": "Scl"},
                {"controllerName": "PLC_1", "name": "B", "namespace": "", "number": 2, "programmingLanguage": "Scl"},
            ],
            totalBlockCount=1,
            nextStartIndex=None,
        )
    )
    client = TiaV19WorkerClient(options, transport)

    with pytest.raises(RuntimeError, match="not a valid block catalog"):
        await client.get_block_catalog_page_async("project-1", 0, 2, None)


async def test_block_catalog_surfaces_changed_snapshot(options):
    transport = ControlledTransport(
        lambda r: _catalog_response(
            r, projectId=None, snapshotHash=None, blocks=[], totalBlockCount=None,
            nextStartIndex=None, errorCode=1, error="snapshot changed",
        )
    )
    client = TiaV19WorkerClient(options, transport)

    with pytest.raises(ProjectBlockCatalogSnapshotChangedError):
        await client.get_block_catalog_page_async("project-1", 1, 1, "snapshot-1")


async def test_create_block_requires_a_controller_name(options):
    transport = ControlledTransport(_create_block_response)
    client = TiaV19WorkerClient(options, transport)

    result = await client.create_block_async(make_operation(), "FUNCTION_BLOCK \"FB_Motor\"")

    assert not result.is_success
    assert (
        "TIA V19 block creation requires the operation to declare a controller name."
        in result.errors
    )
    assert transport.requests == []


async def test_create_block_requires_rendered_source(options):
    transport = ControlledTransport(_create_block_response)
    client = TiaV19WorkerClient(options, transport)

    result = await client.create_block_async(make_operation(controller_name="PLC_1"))

    assert not result.is_success
    assert transport.requests == []


async def test_create_block_dispatches_and_returns_updated_context(options):
    transport = ControlledTransport(_create_block_response)
    client = TiaV19WorkerClient(options, transport)

    result = await client.create_block_async(
        make_operation(controller_name="PLC_1"), 'FUNCTION_BLOCK "FB_Motor"'
    )

    assert result.is_success
    assert result.updated_project_context == ProjectContext("project-1", "snapshot-2")
    request = transport.requests[0]
    assert request.method == protocol.CREATE_BLOCK_METHOD
    assert request.create_block_controller_name == "PLC_1"
    assert request.create_block_name == "FB_Motor"
    assert request.create_block_type == "FunctionBlock"
    assert request.create_block_expected_snapshot_hash == "snapshot-1"


async def test_create_block_surfaces_worker_errors_without_raising(options):
    transport = ControlledTransport(
        lambda r: _create_block_response(
            r, projectId=None, snapshotHash=None, errorCode=3,
            error="Block 'FB_Motor' already exists under controller 'PLC_1'.",
        )
    )
    client = TiaV19WorkerClient(options, transport)

    result = await client.create_block_async(
        make_operation(controller_name="PLC_1"), 'FUNCTION_BLOCK "FB_Motor"'
    )

    assert not result.is_success
    assert "Block 'FB_Motor' already exists under controller 'PLC_1'." in result.errors


async def test_invalid_worker_json_is_rejected(options):
    transport = ControlledTransport(lambda r: "not json")
    client = TiaV19WorkerClient(options, transport)

    with pytest.raises(RuntimeError, match="invalid JSON"):
        await client.get_project_context_async("project-1")
