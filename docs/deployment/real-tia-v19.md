# Real TIA Portal V19 Integration

Run real integration tests only on a workstation where TIA Portal V19 is installed and licensed/configured.

The V19 adapter is compiled for .NET Framework 4.8 and is intentionally isolated from the .NET 8 MCP host. Set the `TiaV19PublicApiDirectory` MSBuild property when the V19 PublicAPI directory is not installed at the default Siemens path.

`EngineerPc.Tia.V19.Worker` is started with `--configuration <absolute-path>`. Its configuration contains only locally approved `projectId` and absolute `projectFilePath` definitions. Requests contain a project ID only; the worker rejects arbitrary project paths and processes requests sequentially.

`EngineerPc.Tia.V19.Client` is the .NET 8 bridge used by the platform. Its deployment-owned options must identify an existing canonical worker `.exe`, an existing canonical worker configuration file, and a deadline between one second and five minutes. The client invokes exactly that executable with exactly the configured `--configuration` path; neither path is derived from MCP input. Ensure the deployment account restricts write access to the worker executable, its configuration, and the configured project directories.

To expose the read-only MCP `get_project_context` tool, configure the host locally with explicit paths and set `Enabled` to `true`. The default is disabled. To additionally expose the PLC `get_block_catalog` tool, set the separate `EnableBlockCatalogRead` switch to `true`; it is also disabled by default. The authenticated certificate mapping must include the `Engineer` role and `engineering.read` scope; both tools remain unavailable to clients without both.

```json
{
	"TiaV19Worker": {
		"Enabled": true,
		"EnableBlockCatalogRead": true,
		"EnableBlockWrite": false,
		"WorkerExecutablePath": "C:\\EngineerPc\\EngineerPc.Tia.V19.Worker.exe",
		"ConfigurationPath": "C:\\EngineerPc\\tia-v19-worker.json",
		"RequestTimeoutSeconds": 30
	}
}
```

The host fails to start when `Enabled` is true but the paths do not meet worker-client validation. Do not include these paths in MCP tool arguments or any remotely managed configuration.

The worker expects lower-camel-case JSON configuration, for example:

```json
{
	"projects": [
		{
			"projectId": "test-project",
			"projectFilePath": "C:\\Engineering\\TestProject.ap19"
		}
	]
}
```

The installed Siemens V19 PublicAPI directory defaults to `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19`. Set the deployment-owned environment variable `TIA_V19_PUBLIC_API_DIRECTORY` only when the V19 PublicAPI has been installed elsewhere. Do not put this directory in worker request data.

The worker protocol and optional MCP `get_block_catalog` tool accept only a configured project ID plus bounded page coordinates. The MCP tool accepts an optional non-negative `startIndex` and a positive `maxBlocks` value. A continuation must supply the successful prior page's `expectedSnapshotHash`; if the current project snapshot differs, the engine rejects the page and the client restarts at zero. The Engineering Engine caps each returned block list at 500 and returns the total count, truncation state, and optional next start index. The host advertises the MCP tool only when both `Enabled` and `EnableBlockCatalogRead` are true. It does not expose project paths, raw Siemens objects, export/import operations, compilation, or any block mutation.

## Enabling writes (`EnableBlockWrite`)

Setting `EnableBlockWrite` to `true` exposes `approve_create_block` and `execute_create_block` (see `docs/mcp/protocol.md`) and lets `execute_create_block` reach `TiaV19Adapter.CreateBlock`, which writes a real block into the configured project. That write path's Siemens member usage (`PlcSoftware.ExternalSourceGroup`, `SourceFiles.CreateFromFile`, `GenerateBlocksFromSource`) is a best-effort implementation, not a verified one — see `docs/tia/adapter-architecture.md`'s "Write path" section. Do not set `EnableBlockWrite` to `true` against a project that matters until you have confirmed those members against your installed V19 PublicAPI assembly (for example by reflecting `Siemens.Engineering.dll` or checking the Openness help that ships with your installation) and completed the checklist below on a disposable test project.

Before enabling writes:

1. Verify V19 project-context reads using a configured local project ID and file path.
2. Verify block discovery.
3. Confirm the exact `ExternalSourceGroup`/`SourceFiles`/`GenerateBlocksFromSource`/`Project.Save` members against your installed V19 assembly; fix `TiaV19Adapter.CreateBlock` if they differ.
4. Verify controlled write end-to-end (`plan_create_block` → `preview_scl_block` → approve → `execute_create_block`) against a disposable test project.
5. Verify post-write validation: the returned snapshot hash changes and a repeated `get_block_catalog` read shows the new block.
6. Verify compilation and diagnostics for the generated block.
7. Verify approval enforcement: execution fails when the transaction is not approved or the operation no longer matches the approved hash.
8. Verify audit trail: plan, approval, execution-started, and commit/failure events all appear.

Keep test projects isolated from production engineering projects.
