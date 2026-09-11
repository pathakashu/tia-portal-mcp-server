"""JSON writing that is byte-compatible with .NET's ``JsonSerializerDefaults.Web``.

The C# implementation hashes operations by serialising them and taking SHA-256 of the
UTF-8 bytes, so any divergence in property order, number formatting or string escaping
silently breaks approval matching between a plan and its execution. .NET's default
``JavaScriptEncoder`` escapes considerably more than Python's :mod:`json` does, and
uses upper-case hex, so this module writes JSON by hand instead.

The exact rules here were captured from the running C# implementation; see
``tools/ParityVectors`` and ``tests/test_parity.py``.
"""

from __future__ import annotations

from typing import Any, Mapping, Protocol, Sequence, runtime_checkable

# Characters inside printable ASCII that .NET escapes anyway (HTML/JS sensitive).
_FORCED_ESCAPES = {'"', "&", "'", "+", "<", ">", "`", "\\"}

# Short escape forms .NET emits rather than \uXXXX.
_SHORT_ESCAPES = {
    "\b": "\\b",
    "\t": "\\t",
    "\n": "\\n",
    "\f": "\\f",
    "\r": "\\r",
    "\\": "\\\\",
}


@runtime_checkable
class JsonSerializable(Protocol):
    """Anything that can lower itself to plain JSON-ready primitives."""

    def to_json_obj(self) -> Any: ...


class PreformattedString(str):
    """A string written verbatim inside quotes, without escaping.

    .NET writes ``DateTimeOffset`` through a dedicated converter that emits the formatted
    token straight to the writer, so the ``+`` in ``2026-09-07T12:00:00+00:00`` survives
    unescaped — whereas a ``+`` inside an ordinary string becomes ``\\u002B``.
    """

    __slots__ = ()


def escape_string(value: str) -> str:
    """Escape ``value`` the way .NET's default ``JavaScriptEncoder`` does."""
    out: list[str] = ['"']
    for char in value:
        short = _SHORT_ESCAPES.get(char)
        if short is not None:
            out.append(short)
            continue

        code = ord(char)
        if char in _FORCED_ESCAPES:
            out.append(f"\\u{code:04X}")
        elif 0x20 <= code <= 0x7E:
            out.append(char)
        elif code > 0xFFFF:
            # .NET writes non-BMP characters as UTF-16 surrogate pairs.
            adjusted = code - 0x10000
            high = 0xD800 + (adjusted >> 10)
            low = 0xDC00 + (adjusted & 0x3FF)
            out.append(f"\\u{high:04X}\\u{low:04X}")
        else:
            out.append(f"\\u{code:04X}")
    out.append('"')
    return "".join(out)


def to_json_obj(value: Any) -> Any:
    """Lower an object graph to primitives, preserving property order."""
    if isinstance(value, JsonSerializable) and not isinstance(value, (str, bytes)):
        return to_json_obj(value.to_json_obj())
    if value is None or isinstance(value, (str, bool, int, float)):
        return value
    if isinstance(value, Mapping):
        return {str(key): to_json_obj(item) for key, item in value.items()}
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
        return [to_json_obj(item) for item in value]
    raise TypeError(f"Cannot serialise {type(value)!r} to .NET-compatible JSON.")


def write(value: Any) -> str:
    """Serialise ``value`` to a compact JSON string matching .NET's Web defaults."""
    return _write_node(to_json_obj(value))


def _write_node(node: Any) -> str:
    if node is None:
        return "null"
    if node is True:
        return "true"
    if node is False:
        return "false"
    if isinstance(node, PreformattedString):
        return '"' + str(node) + '"'
    if isinstance(node, str):
        return escape_string(node)
    if isinstance(node, int):
        return str(node)
    if isinstance(node, float):
        # .NET writes the shortest round-trippable form; Python's repr matches for
        # every value this codebase produces (it has no float-valued contract fields).
        return repr(node)
    if isinstance(node, Mapping):
        body = ",".join(f"{escape_string(str(k))}:{_write_node(v)}" for k, v in node.items())
        return "{" + body + "}"
    if isinstance(node, (list, tuple)):
        return "[" + ",".join(_write_node(item) for item in node) + "]"
    raise TypeError(f"Cannot write {type(node)!r} as JSON.")


def get_ci(source: Mapping[str, Any] | None, name: str, default: Any = None) -> Any:
    """Case-insensitive property lookup.

    ``JsonSerializerDefaults.Web`` sets ``PropertyNameCaseInsensitive``, so inbound MCP
    payloads bind regardless of casing; this keeps the Python host equally permissive.
    """
    if not source:
        return default
    if name in source:
        return source[name]
    lowered = name.lower()
    for key, value in source.items():
        if key.lower() == lowered:
            return value
    return default
