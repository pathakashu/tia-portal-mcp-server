namespace EngineerPc.Mcp;

public sealed record GetBlockCatalogRequest(
	string ProjectId,
	int? StartIndex = null,
	int? MaxBlocks = null,
	string? ExpectedSnapshotHash = null);