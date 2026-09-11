"""Stage 9 full-loop acceptance test: agent plans, human approves/executes, agent verifies.

Drives the real MCP wire protocol (raw JSON-RPC over HTTPS, no `mcp` SDK dependency) against
a running ``engineerpc.host`` deployment -- local, tunneled, or the real Azure path -- using
two separate mTLS identities to prove the authority separation the whole approval gate rests
on: the agent's certificate can plan and read, but a direct attempt to approve or execute must
be denied by the server's own authorization check, not merely by the agent choosing not to.

Usage (production-shaped, two certificates):
    python tools/full_loop_acceptance.py \
        --base-url https://engineer-pc.internal.example:7443/mcp \
        --ca-bundle ca.crt \
        --agent-cert agent.crt --agent-key agent.key \
        --operator-cert operator.crt --operator-key operator.key \
        --project-id test-project

Dry run against a dev host (AllowInsecureLocalhost=true) -- mechanics only, the negative
authorization check is skipped because dev mode grants one principal every scope:
    python tools/full_loop_acceptance.py --insecure --base-url http://localhost:7443/mcp \
        --project-id test-project --controller-name PLC_1
"""
from __future__ import annotations

import argparse
import sys
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from engineerpc.mcp_client import McpClientError, McpConnection, connect

NOT_AUTHORIZED_MARKER = "does not satisfy the required role and scope"


class AcceptanceError(McpClientError):
    pass


@dataclass
class Check:
    name: str
    passed: bool
    detail: str
    elapsed_seconds: float


@dataclass
class Report:
    checks: list[Check] = field(default_factory=list)

    def record(self, name: str, passed: bool, detail: str, elapsed_seconds: float) -> None:
        self.checks.append(Check(name, passed, detail, elapsed_seconds))
        status = "PASS" if passed else "FAIL"
        print(f"[{status}] {name} ({elapsed_seconds:.1f}s) -- {detail}")

    @property
    def all_passed(self) -> bool:
        return all(check.passed for check in self.checks)

    def print_summary(self) -> None:
        print("\n" + "=" * 72)
        print("STAGE 9 FULL-LOOP ACCEPTANCE -- SUMMARY")
        print("=" * 72)
        for check in self.checks:
            status = "PASS" if check.passed else "FAIL"
            print(f"  [{status}] {check.name}")
        total = sum(check.elapsed_seconds for check in self.checks)
        print(f"\n{sum(c.passed for c in self.checks)}/{len(self.checks)} checks passed, {total:.1f}s total")
        print("RESULT: " + ("PASS" if self.all_passed else "FAIL"))


def _fetch_full_catalog(connection: McpConnection, project_id: str) -> list[dict[str, Any]]:
    blocks: list[dict[str, Any]] = []
    start_index = 0
    expected_snapshot_hash: str | None = None
    while True:
        arguments: dict[str, Any] = {"projectId": project_id, "startIndex": start_index, "maxBlocks": 500}
        if expected_snapshot_hash is not None:
            arguments["expectedSnapshotHash"] = expected_snapshot_hash
        structured, is_error = connection.call_tool("get_block_catalog", arguments)
        if is_error:
            raise AcceptanceError(f"get_block_catalog failed: {structured.get('errors')}")
        catalog = structured["blockCatalog"]
        blocks.extend(catalog["blocks"])
        expected_snapshot_hash = catalog["projectContext"]["snapshotHash"]
        next_start = catalog.get("nextStartIndex")
        if next_start is None:
            break
        start_index = next_start
    return blocks


def _build_operation(
    project_id: str, snapshot_hash: str, controller_name: str, block_name: str
) -> dict[str, Any]:
    return {
        "operationId": str(uuid.uuid4()),
        "projectContext": {"projectId": project_id, "snapshotHash": snapshot_hash},
        "idempotencyKey": str(uuid.uuid4()),
        "name": block_name,
        "blockType": "FunctionBlock",
        "language": "Scl",
        "controllerName": controller_name,
        "interface": {
            "inputs": [{"name": "Start", "dataType": "Bool"}],
            "outputs": [{"name": "Running", "dataType": "Bool"}],
        },
        "statements": [{"target": "Running", "source": "Start"}],
    }


def run(args: argparse.Namespace) -> Report:
    report = Report()

    step_start = time.monotonic()
    agent = connect(
        args.base_url, "agent",
        None if args.insecure else (args.agent_cert, args.agent_key),
        args.insecure, args.ca_bundle,
    )
    if args.insecure:
        operator = agent
        print("--insecure: agent and operator share one dev principal with every scope --")
        print("the negative authorization check below cannot be meaningful in this mode.\n")
    else:
        operator = connect(
            args.base_url, "operator", (args.operator_cert, args.operator_key),
            args.insecure, args.ca_bundle,
        )
    report.record("connect sessions", True, f"agent session {agent.session_id}", time.monotonic() - step_start)

    step_start = time.monotonic()
    agent_tools = [tool["name"] for tool in agent.list_tools()]
    report.record(
        "agent tools/list", True, f"advertised: {', '.join(agent_tools)}", time.monotonic() - step_start
    )
    catalog_read_available = "get_block_catalog" in agent_tools
    write_available = "approve_create_block" in agent_tools and "execute_create_block" in agent_tools

    step_start = time.monotonic()
    context_structured, context_is_error = agent.call_tool(
        "get_project_context", {"projectId": args.project_id}
    )
    if context_is_error:
        raise AcceptanceError(f"get_project_context failed: {context_structured.get('errors')}")
    snapshot_hash = context_structured["projectContext"]["snapshotHash"]
    report.record(
        "agent get_project_context", True, f"snapshotHash={snapshot_hash[:16]}...", time.monotonic() - step_start
    )

    controller_name = args.controller_name
    baseline_blocks: list[dict[str, Any]] = []
    if catalog_read_available:
        step_start = time.monotonic()
        baseline_blocks = _fetch_full_catalog(agent, args.project_id)
        if controller_name is None:
            if not baseline_blocks:
                raise AcceptanceError("Catalog is empty and no --controller-name was given.")
            controller_name = baseline_blocks[0]["controllerName"]
        report.record(
            "agent get_block_catalog (baseline)", True,
            f"{len(baseline_blocks)} blocks, controller={controller_name}", time.monotonic() - step_start,
        )
    elif controller_name is None:
        raise AcceptanceError(
            "get_block_catalog is not available in this deployment; pass --controller-name explicitly."
        )

    block_name = args.block_name or f"FB_Acceptance_{int(time.time())}"
    operation = _build_operation(args.project_id, snapshot_hash, controller_name, block_name)

    step_start = time.monotonic()
    preview_structured, preview_is_error = agent.call_tool("preview_scl_block", operation)
    if preview_is_error:
        raise AcceptanceError(f"preview_scl_block failed: {preview_structured.get('errors')}")
    print("--- SCL preview for human review ---")
    print(preview_structured["sourceText"])
    print("-------------------------------------")
    report.record("agent preview_scl_block", True, "rendered SCL above", time.monotonic() - step_start)

    if not write_available:
        report.record(
            "write path", False,
            "approve_create_block/execute_create_block not advertised (EnableBlockWrite off) -- "
            "stopping after preview, per Stage 3/7 partial-run scope",
            0.0,
        )
        report.print_summary()
        return report

    step_start = time.monotonic()
    plan_structured, plan_is_error = agent.call_tool("plan_create_block", operation)
    if plan_is_error or not plan_structured.get("isAwaitingApproval"):
        raise AcceptanceError(f"plan_create_block did not reach AwaitingApproval: {plan_structured.get('errors')}")
    transaction = plan_structured["transaction"]
    report.record(
        "agent plan_create_block", True,
        f"transaction {transaction['transactionId']} -> {transaction['state']}", time.monotonic() - step_start,
    )

    expires_at_utc = (datetime.now(timezone.utc) + timedelta(minutes=10)).isoformat()

    step_start = time.monotonic()
    agent_approve_structured, agent_approve_is_error = agent.call_tool(
        "approve_create_block", {"transaction": transaction, "expiresAtUtc": expires_at_utc}
    )
    agent_approve_denied = agent_approve_is_error and any(
        NOT_AUTHORIZED_MARKER in err for err in agent_approve_structured.get("errors", [])
    )
    agent_execute_structured, agent_execute_is_error = agent.call_tool(
        "execute_create_block", {"transaction": transaction, "operation": operation}
    )
    agent_execute_denied = agent_execute_is_error and any(
        NOT_AUTHORIZED_MARKER in err for err in agent_execute_structured.get("errors", [])
    )
    if args.insecure:
        report.record(
            "agent cannot self-approve/execute", True,
            "skipped (--insecure has no under-scoped identity to test)", time.monotonic() - step_start,
        )
    else:
        report.record(
            "agent cannot self-approve/execute",
            agent_approve_denied and agent_execute_denied,
            f"approve denied={agent_approve_denied}, execute denied={agent_execute_denied}",
            time.monotonic() - step_start,
        )

    step_start = time.monotonic()
    approve_structured, approve_is_error = operator.call_tool(
        "approve_create_block", {"transaction": transaction, "expiresAtUtc": expires_at_utc}
    )
    if approve_is_error or not approve_structured.get("isApproved"):
        raise AcceptanceError(f"operator approve_create_block failed: {approve_structured.get('errors')}")
    approved_transaction = approve_structured["transaction"]
    report.record(
        "operator approve_create_block", True,
        f"transaction -> {approved_transaction['state']}", time.monotonic() - step_start,
    )

    step_start = time.monotonic()
    execute_structured, execute_is_error = operator.call_tool(
        "execute_create_block", {"transaction": approved_transaction, "operation": operation}
    )
    if execute_is_error or not execute_structured.get("isCommitted"):
        raise AcceptanceError(f"execute_create_block did not commit: {execute_structured.get('errors')}")
    updated_snapshot_hash = execute_structured["updatedProjectContext"]["snapshotHash"]
    report.record(
        "operator execute_create_block", True,
        f"committed, new snapshotHash={updated_snapshot_hash[:16]}...", time.monotonic() - step_start,
    )

    if catalog_read_available:
        step_start = time.monotonic()
        final_blocks = _fetch_full_catalog(agent, args.project_id)
        match = next(
            (b for b in final_blocks if b["name"] == block_name and b["controllerName"] == controller_name),
            None,
        )
        found = match is not None and match["programmingLanguage"] == "SCL"
        report.record(
            "verify new block via get_block_catalog",
            found and len(final_blocks) == len(baseline_blocks) + 1,
            f"found={match is not None}, blocks {len(baseline_blocks)} -> {len(final_blocks)}",
            time.monotonic() - step_start,
        )
    else:
        report.record(
            "verify new block via get_block_catalog", False,
            "get_block_catalog not available in this deployment -- cannot verify independently", 0.0,
        )

    report.print_summary()
    return report


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", default="https://localhost:7443/mcp")
    parser.add_argument("--project-id", required=True)
    parser.add_argument("--controller-name", default=None, help="required if get_block_catalog is unavailable")
    parser.add_argument("--block-name", default=None, help="default: FB_Acceptance_<unix-timestamp>")
    parser.add_argument("--ca-bundle", default=None, help="CA bundle to verify the server certificate")
    parser.add_argument("--agent-cert")
    parser.add_argument("--agent-key")
    parser.add_argument("--operator-cert")
    parser.add_argument("--operator-key")
    parser.add_argument(
        "--insecure", action="store_true",
        help="dev-mode dry run against AllowInsecureLocalhost=true; skips mTLS and the authority-separation check",
    )
    args = parser.parse_args(argv)
    if not args.insecure and not all([args.agent_cert, args.agent_key, args.operator_cert, args.operator_key]):
        parser.error("--agent-cert/--agent-key/--operator-cert/--operator-key are required unless --insecure")
    return args


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    try:
        report = run(args)
    except McpClientError as error:
        print(f"\nACCEPTANCE TEST ABORTED: {error}")
        return 1
    return 0 if report.all_passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
