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

Before enabling writes:

1. Verify V19 project-context reads using a configured local project ID and file path.
2. Verify block discovery.
3. Verify compilation.
4. Verify diagnostics.
5. Verify preview/diff.
6. Verify approval enforcement.
7. Verify controlled write.
8. Verify post-write validation.
9. Verify audit trail.

Keep test projects isolated from production engineering projects.
