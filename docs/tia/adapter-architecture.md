# TIA V18 Adapter Architecture

This repository supports **one concrete Siemens adapter only**:

`TiaV18Adapter`

The abstraction remains:

```text
ITiaAdapter
      |
      v
TiaV18Adapter
      |
      v
TIA Openness V18
      |
      v
TIA Portal V18
```

The purpose of the interface is testability and clean business boundaries, not multi-version support.

## Runtime compatibility boundary

The installed TIA Openness V18 API targets .NET Framework 4.8, while the Engineer-PC platform targets .NET 8. `EngineerPc.Tia.V18` is therefore an isolated `net48` adapter assembly. It references `Siemens.Engineering.dll` directly and exposes only framework-neutral DTOs from `EngineerPc.Tia.V18.Protocol`. `EngineerPc.Tia.V18.Worker` hosts this adapter as a sequential stdin/stdout process. `EngineerPc.Tia.V18.Client` is the .NET 8 implementation of `ITiaAdapter`; it starts the locally configured worker executable, passes only the locally configured worker configuration path, and maps validated protocol responses to `ProjectContext`.

The client validates canonical existing paths, requires an `.exe` worker path, serializes requests, and terminates the worker when a request is cancelled or reaches its configured deadline. It validates response correlation IDs before accepting project context. The initial bridge exposes only read-only project-context requests; `CreateBlockAsync` fails closed and never sends a write command to the worker.

Siemens assemblies are not copied into the worker output. At startup, the worker initializes an adapter-local assembly resolver that loads only `Siemens.Engineering*.dll` from the installed V18 PublicAPI directory. The default is `C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18`; a deployment owner may set `TIA_V18_PUBLIC_API_DIRECTORY` for a non-default local installation. This setting is not accepted from MCP input or worker protocol messages.

The initial verified capability reads the context of a locally configured project. It uses only the reflected V18 members `TiaPortal(TiaPortalMode.WithoutUserInterface)`, `Projects.Open(FileInfo)`, `Project.Name`, `Project.Path`, `Project.LastModified`, `Project.Size`, `Project.Version`, `Project.Close()`, and `TiaPortal.Dispose()`.

The worker also supports a read-only paged `get_block_catalog` request. Its verified V18 traversal is `Project.Devices` and `Project.DeviceGroups` to each `Device`/`DeviceItem`; `GetService<SoftwareContainer>()`; `SoftwareContainer.Software` when it is `PlcSoftware`; `PlcSoftware.BlockGroup`; `PlcBlockGroup.Blocks`; and nested `PlcBlockGroup.Groups`. It maps `PlcSoftware.Name` and `PlcBlock.Name`, `Namespace`, `Number`, and `ProgrammingLanguage` to framework-neutral records, deterministically sorts the catalog, then returns only the requested page. A continuation supplies the preceding page's snapshot hash; the adapter compares it to the current snapshot and returns a structured stale-snapshot result instead of a mixed page. The response carries total count and an optional next start index; at most 500 blocks cross the worker boundary. The adapter then closes the project. The MCP host may expose this capability only through its separate default-off `EnableBlockCatalogRead` setting; the Siemens worker remains unaware of MCP identity and request policy.
