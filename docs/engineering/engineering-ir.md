# Engineering IR v1

Engineering IR is the contract between AI-generated intent and deterministic engineering execution.

It is Siemens-independent.

Example:

```json
{
  "irVersion": "1.0",
  "operationType": "CreateBlock",
  "blockType": "FB",
  "name": "FB_Motor",
  "language": "SCL",
  "interface": {
    "inputs": [
      {"name": "Start", "dataType": "Bool"},
      {"name": "Stop", "dataType": "Bool"}
    ],
    "outputs": [
      {"name": "Running", "dataType": "Bool"}
    ]
  },
  "statements": [
    {"target": "Running", "source": "Start"}
  ]
}
```

`statements` is optional and currently supports only typed SCL assignments. The target must be a declared output; the source must be a declared input or output identifier. Free-form source text, expressions, and control flow are not part of the IR.

The IR is translated by the V19 adapter into Siemens-specific operations.
