"""Controlled bridge to the .NET Framework V19 worker (port of ``EngineerPc.Tia.V19.Client``).

Spawns exactly the deployment-configured worker executable with exactly the configured
``--configuration`` path — neither is ever derived from MCP input — sends one JSON request
line, and reads one response line. Requests are serialised so the worker stays sequential.
"""

from __future__ import annotations

import asyncio
import json
import os
import uuid
from dataclasses import dataclass
from datetime import timedelta
from pathlib import Path
from typing import Any, Protocol

from .. import dotnet_json
from ..contracts import ProjectContext
from ..ir import CreateBlockOperation
from . import v19_protocol as protocol
from .abstractions import (
    ProjectBlock,
    ProjectBlockCatalogPage,
    ProjectBlockCatalogSnapshotChangedError,
    TiaAdapterExecutionResult,
)


@dataclass(frozen=True)
class TiaV19WorkerClientOptions:
    worker_executable_path: str
    configuration_path: str
    request_timeout: timedelta


@dataclass(frozen=True)
class OptionsValidationResult:
    errors: tuple[str, ...]

    @property
    def is_valid(self) -> bool:
        return len(self.errors) == 0


def _validate_path(path: str, display_name: str, required_extension: str | None, errors: list[str]) -> None:
    if not (path or "").strip() or not os.path.isabs(path):
        errors.append(f"{display_name} path must be absolute.")
        return

    if os.path.normpath(path).lower() != path.lower():
        errors.append(f"{display_name} path must be canonical.")

    if required_extension is not None and Path(path).suffix.lower() != required_extension.lower():
        errors.append(f"{display_name} path must use the {required_extension} extension.")

    if not os.path.isfile(path):
        errors.append(f"{display_name} was not found.")


def validate_options(options: TiaV19WorkerClientOptions) -> OptionsValidationResult:
    if options is None:
        raise ValueError("options is required.")

    errors: list[str] = []
    _validate_path(options.worker_executable_path, "TIA V19 worker executable", ".exe", errors)
    _validate_path(options.configuration_path, "TIA V19 worker configuration", None, errors)

    if options.request_timeout < timedelta(seconds=1) or options.request_timeout > timedelta(minutes=5):
        errors.append("TIA V19 worker request timeout must be between 1 second and 5 minutes.")

    return OptionsValidationResult(tuple(errors))


class WorkerTransport(Protocol):
    async def send_async(
        self, options: TiaV19WorkerClientOptions, request: protocol.WorkerRequest
    ) -> str: ...


class WorkerProcessTransport:
    """One worker process per request, exactly as the C# transport does."""

    async def send_async(
        self, options: TiaV19WorkerClientOptions, request: protocol.WorkerRequest
    ) -> str:
        validation = validate_options(options)
        if not validation.is_valid:
            raise ValueError(" ".join(validation.errors))

        process = await asyncio.create_subprocess_exec(
            options.worker_executable_path,
            "--configuration",
            options.configuration_path,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )

        payload = (dotnet_json.write(request) + "\n").encode("utf-8")
        try:
            stdout, stderr = await asyncio.wait_for(
                process.communicate(payload),
                timeout=options.request_timeout.total_seconds(),
            )
        except asyncio.TimeoutError:
            _terminate(process)
            raise TimeoutError("TIA V19 worker request timed out.") from None
        except BaseException:
            _terminate(process)
            raise

        text = stdout.decode("utf-8", errors="replace")
        error_text = stderr.decode("utf-8", errors="replace")
        lines = [line for line in text.splitlines() if line.strip()]

        if not lines:
            raise RuntimeError(_failure_message("did not return a response", error_text))
        if process.returncode != 0:
            raise RuntimeError(
                _failure_message(f"exited with code {process.returncode}", error_text)
            )
        if len(lines) > 1:
            raise RuntimeError("TIA V19 worker process returned more than one response.")

        return lines[0]


def _failure_message(failure: str, standard_error: str) -> str:
    sanitized = standard_error.replace("\r\n", " ").replace("\n", " ").strip()
    if not sanitized:
        return f"TIA V19 worker process {failure}."
    return f"TIA V19 worker process {failure}: {sanitized[:512]}"


def _terminate(process: asyncio.subprocess.Process) -> None:
    try:
        if process.returncode is None:
            process.kill()
    except ProcessLookupError:
        pass


def _parse(payload: str) -> dict[str, Any]:
    try:
        parsed = json.loads(payload)
    except json.JSONDecodeError as error:
        raise RuntimeError("TIA V19 worker returned invalid JSON.") from error
    if not isinstance(parsed, dict):
        raise RuntimeError("TIA V19 worker returned an empty response.")
    return parsed


class TiaV19WorkerClient:
    """Implements both ``TiaAdapter`` and ``ProjectBlockCatalogReader``."""

    def __init__(
        self,
        options: TiaV19WorkerClientOptions,
        transport: WorkerTransport | None = None,
    ) -> None:
        validation = validate_options(options)
        if not validation.is_valid:
            raise ValueError(" ".join(validation.errors))

        self._options = options
        self._transport = transport or WorkerProcessTransport()
        self._request_gate = asyncio.Lock()

    async def get_project_context_async(self, project_id: str) -> ProjectContext | None:
        if not (project_id or "").strip():
            raise ValueError("Project ID is required.")

        async with self._request_gate:
            request = protocol.WorkerRequest(
                protocol.VERSION,
                uuid.uuid4().hex,
                protocol.GET_PROJECT_CONTEXT_METHOD,
                project_id,
            )
            response = protocol.ProjectContextResponse.from_json_obj(
                _parse(await self._transport.send_async(self._options, request))
            )

            if response.request_id != request.request_id:
                raise RuntimeError("TIA V19 worker response did not match the request ID.")
            if response.error is not None:
                return None
            if response.project_id != project_id or not (response.snapshot_hash or "").strip():
                raise RuntimeError("TIA V19 worker response is not a valid project context.")

            return ProjectContext(response.project_id, response.snapshot_hash or "")

    async def create_block_async(
        self,
        operation: CreateBlockOperation,
        scl_source_text: str | None = None,
    ) -> TiaAdapterExecutionResult:
        if operation is None:
            raise ValueError("operation is required.")

        if not (operation.controller_name or "").strip():
            return TiaAdapterExecutionResult(
                None,
                ("TIA V19 block creation requires the operation to declare a controller name.",),
            )

        if not (scl_source_text or "").strip():
            return TiaAdapterExecutionResult(
                None,
                (
                    "TIA V19 block creation only supports the constrained Scl "
                    "Function/FunctionBlock source surface.",
                ),
            )

        async with self._request_gate:
            request = protocol.WorkerRequest(
                protocol.VERSION,
                uuid.uuid4().hex,
                protocol.CREATE_BLOCK_METHOD,
                operation.project_context.project_id,
                create_block_controller_name=operation.controller_name,
                create_block_name=operation.name,
                create_block_type=operation.block_type.name,
                create_block_source_text=scl_source_text,
                create_block_expected_snapshot_hash=operation.project_context.snapshot_hash,
            )
            response = protocol.CreateBlockResponse.from_json_obj(
                _parse(await self._transport.send_async(self._options, request))
            )

            if response.request_id != request.request_id:
                raise RuntimeError("TIA V19 worker response did not match the request ID.")

            if response.error is not None or response.error_code != protocol.CreateBlockErrorCode.NoError:
                return TiaAdapterExecutionResult(
                    None, (response.error or "TIA V19 block creation failed.",)
                )

            if (
                response.project_id != operation.project_context.project_id
                or not (response.snapshot_hash or "").strip()
            ):
                raise RuntimeError("TIA V19 worker response is not a valid block creation result.")

            return TiaAdapterExecutionResult(
                ProjectContext(response.project_id, response.snapshot_hash or ""), ()
            )

    async def get_block_catalog_page_async(
        self,
        project_id: str,
        start_index: int,
        maximum_block_count: int,
        expected_snapshot_hash: str | None,
    ) -> ProjectBlockCatalogPage | None:
        if not (project_id or "").strip():
            raise ValueError("Project ID is required.")
        if start_index < 0:
            raise ValueError("startIndex must be non-negative.")
        if maximum_block_count <= 0 or maximum_block_count > protocol.MAXIMUM_BLOCK_CATALOG_BLOCK_COUNT:
            raise ValueError("maximumBlockCount is out of range.")
        if start_index > 0 and not (expected_snapshot_hash or "").strip():
            raise ValueError("Expected snapshot hash is required for catalog continuation.")

        async with self._request_gate:
            request = protocol.WorkerRequest(
                protocol.VERSION,
                uuid.uuid4().hex,
                protocol.GET_BLOCK_CATALOG_METHOD,
                project_id,
                block_catalog_start_index=start_index,
                block_catalog_maximum_block_count=maximum_block_count,
                block_catalog_expected_snapshot_hash=expected_snapshot_hash,
            )
            response = protocol.BlockCatalogResponse.from_json_obj(
                _parse(await self._transport.send_async(self._options, request))
            )

            if response.request_id != request.request_id:
                raise RuntimeError("TIA V19 worker response did not match the request ID.")

            if response.error is not None:
                if response.error_code == protocol.BlockCatalogErrorCode.SnapshotChanged:
                    raise ProjectBlockCatalogSnapshotChangedError()
                return None

            if response.error_code != protocol.BlockCatalogErrorCode.NoError:
                raise RuntimeError("TIA V19 worker returned an invalid block catalog error code.")

            total = response.total_block_count
            if (
                response.project_id != project_id
                or not (response.snapshot_hash or "").strip()
                or response.blocks is None
                or total is None
                or total < 0
                or start_index > total
                or len(response.blocks) > maximum_block_count
                or len(response.blocks) > total - start_index
                or any(
                    not (block.controller_name or "").strip()
                    or not (block.name or "").strip()
                    or block.namespace is None
                    or block.number < 0
                    or not (block.programming_language or "").strip()
                    for block in response.blocks
                )
            ):
                raise RuntimeError("TIA V19 worker response is not a valid block catalog.")

            expected_next = (
                start_index + len(response.blocks)
                if start_index + len(response.blocks) < total
                else None
            )
            if response.next_start_index != expected_next or (
                response.next_start_index is not None and response.next_start_index <= start_index
            ):
                raise RuntimeError(
                    "TIA V19 worker response has an invalid block catalog continuation."
                )

            return ProjectBlockCatalogPage(
                project_context=ProjectContext(response.project_id, response.snapshot_hash or ""),
                blocks=tuple(
                    ProjectBlock(
                        controller_name=block.controller_name,
                        name=block.name,
                        namespace=block.namespace,
                        number=block.number,
                        programming_language=block.programming_language,
                    )
                    for block in response.blocks
                ),
                total_block_count=total,
                next_start_index=response.next_start_index,
            )
