"""Local OpenAI-powered MCP agent for engineerpc.host.

A plain-English REPL: you describe an engineering change, the model calls MCP tools (via the
shared client in engineerpc.mcp_client) to read project state and plan/preview it, and a human
operator completes the approve/execute step separately (e.g. the dev console at
http://<host>:7443/ with an execute-scoped certificate). This agent is never configured with
`engineering.execute` scope, and never even offers approve/execute to the model as callable
tools -- belt-and-suspenders on top of the server's own authorization check, matching the
platform's principle that the AI proposes intent and a human executes it.

Setup:
    pip install -e ".[agent]"   # from python/, adds the `openai` dependency
    export OPENAI_API_KEY=...

Usage against a real mTLS deployment:
    python agent/openai_mcp_agent.py \
        --base-url https://engineer-pc.internal.example:7443/mcp \
        --ca-bundle ca.crt --agent-cert agent.crt --agent-key agent.key \
        --project-id test-project

Dry run against a dev host (AllowInsecureLocalhost=true):
    python agent/openai_mcp_agent.py --insecure --base-url http://localhost:7443/mcp \
        --project-id test-project
"""
from __future__ import annotations

import argparse
import json
import sys
import uuid
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from engineerpc.mcp_client import McpClientError, McpConnection, connect

ALLOWED_TOOLS = frozenset(
    {"plan_create_block", "preview_scl_block", "get_project_context", "get_block_catalog"}
)
DEFAULT_MODEL = "gpt-4o-mini"
SYSTEM_PROMPT = """\
You are an engineering assistant for Siemens TIA Portal V19, working through a gated MCP \
server. You may read project state and propose engineering changes, but you can never \
approve or execute anything -- a human operator does that separately after reviewing your \
plan and the rendered SCL preview. Always read the current project context before planning \
or previewing a block, since a plan built on stale project state will be rejected. Be \
concise, and always show the user the rendered SCL from preview_scl_block before saying a \
change is ready for human approval.\
"""


class Agent:
    def __init__(self, connection: McpConnection, project_id: str, model: str) -> None:
        self._connection = connection
        self._project_id = project_id
        self._model = model
        self._snapshot_hash: str | None = None
        self._messages: list[dict[str, Any]] = [{"role": "system", "content": SYSTEM_PROMPT}]

        from openai import OpenAI  # deferred: only required once an agent actually runs

        self._client = OpenAI()
        self._tool_defs = self._build_tool_defs()

    def _build_tool_defs(self) -> list[dict[str, Any]]:
        tools = self._connection.list_tools()
        defs = []
        for tool in tools:
            if tool["name"] not in ALLOWED_TOOLS:
                continue
            defs.append(
                {
                    "type": "function",
                    "function": {
                        "name": tool["name"],
                        "description": tool.get("description", ""),
                        "parameters": tool["inputSchema"],
                    },
                }
            )
        if not defs:
            raise McpClientError(
                "None of plan_create_block/preview_scl_block/get_project_context/"
                "get_block_catalog are advertised by this deployment."
            )
        return defs

    def _refresh_snapshot(self) -> None:
        structured, is_error = self._connection.call_tool(
            "get_project_context", {"projectId": self._project_id}
        )
        if is_error:
            raise McpClientError(f"get_project_context failed: {structured.get('errors')}")
        self._snapshot_hash = structured["projectContext"]["snapshotHash"]

    def _inject_fields(self, tool_name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        arguments = dict(arguments)
        arguments.setdefault("projectId", self._project_id)

        if tool_name in ("plan_create_block", "preview_scl_block"):
            # Never trust the model with protocol plumbing or a stale snapshot -- refresh right
            # before every plan/preview call, per the README's "snapshot discipline" guidance.
            self._refresh_snapshot()
            arguments["operationId"] = str(uuid.uuid4())
            arguments["idempotencyKey"] = str(uuid.uuid4())
            arguments["projectContext"] = {"projectId": self._project_id, "snapshotHash": self._snapshot_hash}
            arguments.pop("projectId", None)

        return arguments

    def _run_tool_call(self, tool_call: Any) -> str:
        name = tool_call.function.name
        try:
            raw_arguments = json.loads(tool_call.function.arguments or "{}")
        except json.JSONDecodeError as error:
            return json.dumps({"error": f"Model produced invalid JSON arguments: {error}"})

        if name not in ALLOWED_TOOLS:
            return json.dumps({"error": f"Tool '{name}' is not available to this agent."})

        arguments = self._inject_fields(name, raw_arguments)
        print(f"  -> calling {name}({json.dumps(arguments)[:200]})")
        try:
            structured, is_error = self._connection.call_tool(name, arguments)
        except McpClientError as error:
            return json.dumps({"error": str(error)})

        if name == "get_project_context" and not is_error:
            self._snapshot_hash = structured["projectContext"]["snapshotHash"]

        return json.dumps(structured)

    def ask(self, user_text: str) -> str:
        self._messages.append({"role": "user", "content": user_text})

        while True:
            response = self._client.chat.completions.create(
                model=self._model, messages=self._messages, tools=self._tool_defs,
            )
            message = response.choices[0].message
            self._messages.append(message.model_dump(exclude_none=True))

            if not message.tool_calls:
                return message.content or ""

            for tool_call in message.tool_calls:
                result_text = self._run_tool_call(tool_call)
                self._messages.append(
                    {"role": "tool", "tool_call_id": tool_call.id, "content": result_text}
                )


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", default="https://localhost:7443/mcp")
    parser.add_argument("--project-id", required=True)
    parser.add_argument("--ca-bundle", default=None, help="CA bundle to verify the server certificate")
    parser.add_argument("--agent-cert")
    parser.add_argument("--agent-key")
    parser.add_argument("--model", default=DEFAULT_MODEL, help=f"OpenAI model (default: {DEFAULT_MODEL})")
    parser.add_argument(
        "--insecure", action="store_true",
        help="dev-mode dry run against AllowInsecureLocalhost=true; skips mTLS",
    )
    args = parser.parse_args(argv)
    if not args.insecure and not (args.agent_cert and args.agent_key):
        parser.error("--agent-cert/--agent-key are required unless --insecure")
    return args


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    connection = connect(
        args.base_url, "agent",
        None if args.insecure else (args.agent_cert, args.agent_key),
        args.insecure, args.ca_bundle,
    )
    agent = Agent(connection, args.project_id, args.model)
    print(f"Connected. Tools available: {', '.join(t['function']['name'] for t in agent._tool_defs)}")
    print("Type a request (or 'exit').\n")

    while True:
        try:
            user_text = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return 0
        if not user_text:
            continue
        if user_text.lower() in ("exit", "quit"):
            return 0
        try:
            reply = agent.ask(user_text)
        except McpClientError as error:
            print(f"[MCP error] {error}")
            continue
        print(reply, "\n")


if __name__ == "__main__":
    raise SystemExit(main())
