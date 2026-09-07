using EngineerPc.Tia.V19.Protocol;

namespace EngineerPc.Tia.V19.Protocol.Tests;

public sealed class TiaV19ProjectContextProtocolTests
{
    [Fact]
    public void ProjectCatalog_ResolvesOnlyConfiguredAbsoluteProjectPaths()
    {
        var project = new TiaV19ProjectDefinition("project-1", Path.GetFullPath("project-1.ap19"));
        var catalog = new TiaV19ProjectCatalog([project]);

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
        Assert.Throws<ArgumentException>(() => new TiaV19ProjectCatalog([
            new TiaV19ProjectDefinition("project-1", Path.GetFullPath("first.ap19")),
            new TiaV19ProjectDefinition("project-1", Path.GetFullPath("second.ap19"))
        ]));
        Assert.Throws<ArgumentException>(() => new TiaV19ProjectCatalog([
            new TiaV19ProjectDefinition("project-1", "relative.ap19")
        ]));
    }

    [Fact]
    public void ProjectSnapshot_IsDeterministicAndChangesWithProjectMetadata()
    {
        var timestamp = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        var first = TiaV19ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap19", timestamp, 128, "19.0");
        var second = TiaV19ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap19", timestamp, 128, "19.0");
        var changed = TiaV19ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap19", timestamp, 129, "19.0");

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void ProjectSnapshot_AcceptsEmptyVersion()
    {
        // Siemens.Engineering.Project.Version is a non-null string that is empty for
        // some real V19 projects; the snapshot hash must still be computable.
        var timestamp = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

        var snapshot = TiaV19ProjectSnapshot.Calculate("project-1", "Main", "C:\\Projects\\Main.ap19", timestamp, 128, string.Empty);

        Assert.False(string.IsNullOrEmpty(snapshot));
    }

    [Fact]
    public void WorkerRequest_ContainsOnlyProtocolMetadataConfiguredProjectIdAndCatalogPageParameters()
    {
        var request = new TiaV19WorkerRequest("1.0", "request-1", "get_block_catalog", "project-1", 100, 25);

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
        var response = new TiaV19BlockCatalogResponse(
            "request-1",
            "project-1",
            "snapshot-1",
            [new TiaV19BlockDefinition("PLC_1", "FB_Motor", "", 1, "Scl")],
            3,
            1,
            TiaV19BlockCatalogErrorCode.None,
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