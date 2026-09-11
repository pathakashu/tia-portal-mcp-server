"""Behavioural parity against golden vectors captured from the C# implementation.

Regenerate with:
    dotnet run --project tools/ParityVectors -- python/tests/parity_vectors.json

These assertions are the contract that the port did not change functionality: the
operation hash, project snapshot hash, deterministic MCP request id, rendered SCL and
worker wire format must all match the C# server byte for byte.
"""

from __future__ import annotations

import json
import uuid
from pathlib import Path

import pytest

from engineerpc import dotnet_json
from engineerpc.engine import (
    EngineeringOperationPlanner,
    SclSourceRenderer,
)
from engineerpc.ir import hash_json_obj, operation_from_json_obj
from engineerpc.tia import v19_protocol
from engineerpc.tia.mock import MockTiaAdapter, calculate_snapshot_hash
from engineerpc.validation import CreateBlockOperationValidator

VECTORS = json.loads((Path(__file__).parent / "parity_vectors.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("vector", VECTORS["stringEscaping"], ids=lambda v: v["raw"][:20])
def test_string_escaping_matches_dotnet(vector):
    assert dotnet_json.escape_string(vector["raw"]) == vector["serialized"]


@pytest.mark.parametrize("vector", VECTORS["operations"], ids=lambda v: v["key"])
def test_operation_hash_json_matches_dotnet(vector):
    operation = operation_from_json_obj(json.loads(vector["apiJson"]))
    assert dotnet_json.write(hash_json_obj(operation)) == vector["hashJson"]


@pytest.mark.parametrize("vector", VECTORS["operations"], ids=lambda v: v["key"])
def test_operation_api_json_matches_dotnet(vector):
    operation = operation_from_json_obj(json.loads(vector["apiJson"]))
    assert dotnet_json.write(operation) == vector["apiJson"]


@pytest.mark.parametrize("vector", VECTORS["operations"], ids=lambda v: v["key"])
def test_planner_hash_and_preview_match_dotnet(vector):
    operation = operation_from_json_obj(json.loads(vector["apiJson"]))
    planner = EngineeringOperationPlanner(CreateBlockOperationValidator())

    result = planner.plan(operation)

    assert list(result.errors) == vector["planErrors"]
    if vector["operationHash"] is not None:
        assert result.plan.operation_hash == vector["operationHash"]
        assert result.plan.preview == vector["preview"]


@pytest.mark.parametrize("vector", VECTORS["operations"], ids=lambda v: v["key"])
def test_scl_rendering_matches_dotnet(vector):
    operation = operation_from_json_obj(json.loads(vector["apiJson"]))

    assert SclSourceRenderer.validate(operation) == vector["sclValidationErrors"]
    if vector["sclSource"] is not None:
        assert SclSourceRenderer.render(operation) == vector["sclSource"]


@pytest.mark.parametrize(
    "vector",
    VECTORS["projectSnapshots"],
    ids=lambda v: f"{v['projectId']}-{v['lastModifiedUtcTicks']}",
)
def test_project_snapshot_hash_matches_dotnet(vector):
    assert (
        v19_protocol.calculate_project_snapshot(
            vector["projectId"],
            vector["projectName"],
            vector["projectFilePath"],
            vector["lastModifiedUtcTicks"],
            vector["version"],
        )
        == vector["snapshotHash"]
    )


def test_project_snapshot_ignores_growing_project_size():
    """TIA logs on every open, so Project.Size drifts; the snapshot must not depend on it."""
    first = v19_protocol.calculate_project_snapshot("p", "n", "C:\\x.ap19", 1234, "")
    second = v19_protocol.calculate_project_snapshot("p", "n", "C:\\x.ap19", 1234, "")
    changed = v19_protocol.calculate_project_snapshot("p", "n", "C:\\x.ap19", 1235, "")

    assert first == second
    assert first != changed


@pytest.mark.parametrize(
    "vector", VECTORS["deterministicRequestIds"], ids=lambda v: v["rawJsonRpcId"]
)
def test_deterministic_request_id_matches_dotnet(vector):
    from engineerpc.host.jsonrpc import create_deterministic_request_id

    session_id = uuid.UUID(vector["sessionId"])
    assert (
        str(create_deterministic_request_id(session_id, vector["rawJsonRpcId"]))
        == vector["requestId"]
    )


async def test_mock_adapter_snapshot_hashes_match_dotnet():
    from engineerpc.contracts import ProjectContext
    from engineerpc.ir import (
        BlockInterface,
        BlockType,
        CreateBlockOperation,
        ProgrammingLanguage,
    )

    adapter = MockTiaAdapter([ProjectContext("project-1", "snapshot-1")])
    for vector in VECTORS["mockAdapterSnapshots"]:
        context = await adapter.get_project_context_async("project-1")
        result = await adapter.create_block_async(
            CreateBlockOperation(
                operation_id=uuid.uuid4(),
                project_context=context,
                idempotency_key="k",
                name=vector["addedBlock"],
                block_type=BlockType.FunctionBlock,
                language=ProgrammingLanguage.Scl,
                interface=BlockInterface(),
            )
        )
        assert result.updated_project_context.snapshot_hash == vector["snapshotHash"]


def test_mock_snapshot_hash_is_ordinal_sorted():
    assert calculate_snapshot_hash("p", ["b", "a"]) == calculate_snapshot_hash("p", ["a", "b"])


@pytest.mark.parametrize("vector", VECTORS["workerRequests"], ids=lambda v: v["key"])
def test_worker_request_wire_format_matches_dotnet(vector):
    expected = json.loads(vector["json"])
    request = v19_protocol.WorkerRequest(
        protocol_version=expected["protocolVersion"],
        request_id=expected["requestId"],
        method=expected["method"],
        project_id=expected["projectId"],
        block_catalog_start_index=expected["blockCatalogStartIndex"],
        block_catalog_maximum_block_count=expected["blockCatalogMaximumBlockCount"],
        block_catalog_expected_snapshot_hash=expected["blockCatalogExpectedSnapshotHash"],
        create_block_controller_name=expected["createBlockControllerName"],
        create_block_name=expected["createBlockName"],
        create_block_type=expected["createBlockType"],
        create_block_source_text=expected["createBlockSourceText"],
        create_block_expected_snapshot_hash=expected["createBlockExpectedSnapshotHash"],
    )
    assert dotnet_json.write(request) == vector["json"]


def test_transaction_api_json_matches_dotnet():
    from engineerpc.transactions import EngineeringTransaction

    expected = VECTORS["transactionApiJson"]
    transaction = EngineeringTransaction.from_json_obj(json.loads(expected))
    assert dotnet_json.write(transaction) == expected


def test_worker_protocol_version_matches_dotnet():
    assert v19_protocol.VERSION == VECTORS["protocolVersion"]
