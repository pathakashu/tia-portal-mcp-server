using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Tia.Mock.Tests;

public sealed class MockTiaAdapterTests
{
    [Fact]
    public async Task CreateBlockAsync_WithCurrentSnapshot_CreatesBlockAndUpdatesSnapshot()
    {
        var adapter = new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]);

        var result = await adapter.CreateBlockAsync(CreateOperation("snapshot-1", "FB_Motor"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.UpdatedProjectContext);
        Assert.NotEqual("snapshot-1", result.UpdatedProjectContext.SnapshotHash);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task CreateBlockAsync_WithDuplicateBlockName_ReturnsError()
    {
        var adapter = new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]);
        var firstResult = await adapter.CreateBlockAsync(
            CreateOperation("snapshot-1", "FB_Motor"),
            CancellationToken.None);

        var result = await adapter.CreateBlockAsync(
            CreateOperation(firstResult.UpdatedProjectContext!.SnapshotHash, "FB_Motor"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Block 'FB_Motor' already exists.", result.Errors);
    }

    [Fact]
    public async Task CreateBlockAsync_WithStaleSnapshot_ReturnsError()
    {
        var adapter = new MockTiaAdapter([new ProjectContext("project-1", "snapshot-1")]);
        await adapter.CreateBlockAsync(CreateOperation("snapshot-1", "FB_Motor"), CancellationToken.None);

        var result = await adapter.CreateBlockAsync(
            CreateOperation("snapshot-1", "FB_Valve"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Project snapshot is stale.", result.Errors);
    }

    private static CreateBlockOperation CreateOperation(string snapshotHash, string name) => new(
        Guid.NewGuid(),
        new ProjectContext("project-1", snapshotHash),
        Guid.NewGuid().ToString("N"),
        name,
        BlockType.FunctionBlock,
        ProgrammingLanguage.Scl,
        new BlockInterface([]));
}