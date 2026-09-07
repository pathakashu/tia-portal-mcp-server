# Copilot Agent Instructions — TIA Portal V18 Only

You are working on an industrial automation engineering runtime for Siemens TIA Portal V18.

Before changing code:

1. Read `README.md`.
2. Read `.github/copilot-instructions.md`.
3. Read the relevant path-specific instruction file.
4. Read the relevant design document under `docs/`.
5. Inspect existing interfaces before adding new ones.

## Non-negotiable constraints

- TIA Portal V18 is the only supported TIA version.
- TIA Openness V18 is the only Siemens API target.
- Never add V19/V20/V21 code, documentation, or configuration.
- Never invent Siemens API members.
- Never expose Siemens.Engineering types outside the V18 adapter boundary.
- Never let MCP handlers directly call TIA Openness.
- Never let remote AI input bypass policy, approval, validation, or transactions.
- Never execute arbitrary shell commands, scripts, DLLs, or filesystem operations supplied by AI input.

## Build discipline

Implement one architectural layer at a time.

After meaningful changes:

1. Build.
2. Run tests.
3. Inspect failures.
4. Fix only relevant issues.
5. Update documentation.

Do not rewrite unrelated code.
