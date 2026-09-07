using EngineerPc.Tia.V18.Protocol;

namespace EngineerPc.Tia.V18.Protocol.Tests;

public sealed class TiaV18ProjectContextProtocolTests
{
    [Fact]
    public void ProjectCatalog_ResolvesOnlyConfiguredAbsoluteProjectPaths()
    {
        var project = new TiaV18ProjectDefinition("project-1", Path.GetFullPath("project-1.ap18"));
        var catalog = new TiaV18ProjectCatalog([project]);

        var isConfigured = catalog.TryGetProject("project-1", out var resolvedProject);
        var isUnknown = catalog.TryGetProject("project-2", out var unknownProject);

        Assert.True(isConfigured);
        Assert.Equal(project, resolvedProject);
        Assert.False(isUnknown);
        Assert.Null(unknownProject);
    }

    [Fact]
    public void ProjectCatalog_RejectsDuplicateProjectIdsAndRelativePaths()
    {
        Assert.Throws<ArgumentException>(() => new TiaV18ProjectCatalog([
            new TiaV18ProjectDefinition("project-1", Path.GetFullPath("first.ap18")),
            new TiaV18ProjectDefinition("project-1", Path.GetFullPath("second.ap18"))
        ]));
        Assert.Throws<ArgumentException>(() => new TiaV18ProjectCatalog([
            new TiaV18ProjectDefinition("project-1", "relative.ap18")
        ]));
    }

    [Fact]
    public void ProjectSnapshot_IsDeterministicAndChangesWithProjectMetadata()
    {
        var timestamp = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        var first = TiaV18ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap18", timestamp, 128, "18.0");
        var second = TiaV18ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap18", timestamp, 128, "18.0");
        var changed = TiaV18ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap18", timestamp, 129, "18.0");

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void WorkerRequest_ContainsOnlyProtocolMetadataConfiguredProjectIdAndCatalogPageParameters()
    {
        var request = new TiaV18WorkerRequest("1.0", "request-1", "get_block_catalog", "project-1", 100, 25);

        Assert.Equal("1.0", request.ProtocolVersion);
        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("get_block_catalog", request.Method);
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal(100, request.BlockCatalogStartIndex);
        Assert.Equal(25, request.BlockCatalogMaximumBlockCount);
    }

    [Fact]
    public void BlockCatalogResponse_ContainsOnlySnapshotAndReadOnlyBlockMetadata()
    {
        var response = new TiaV18BlockCatalogResponse(
            "request-1",
            "project-1",
            "snapshot-1",
            [new TiaV18BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl")],
            3,
            1,
            TiaV18BlockCatalogErrorCode.None,
            null);

        Assert.True(response.IsSuccess);
        var block = Assert.Single(response.Blocks);
        Assert.Equal("PLC_1", block.ControllerName);
        Assert.Equal("FB_Motor", block.Name);
        Assert.Equal(1, block.Number);
        Assert.Equal("Scl", block.ProgrammingLanguage);
        Assert.Equal(3, response.TotalBlockCount);
        Assert.Equal(1, response.NextStartIndex);
    }
}