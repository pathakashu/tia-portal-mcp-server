using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectSearch.Tests;

public sealed class DeterministicProjectSearchProviderTests
{
    [Fact]
    public void Search_WithExactName_RanksExactArtifactFirst()
    {
        var provider = new DeterministicProjectSearchProvider();

        var results = provider.Search(CreateSnapshot(), new ProjectSearchQuery("FB_Motor"));

        Assert.Equal("block-motor", results[0].ArtifactId);
    }

    [Fact]
    public void Search_WithMetadataTextAndTypeFilter_ReturnsMatchingArtifactOfRequestedType()
    {
        var provider = new DeterministicProjectSearchProvider();

        var results = provider.Search(
            CreateSnapshot(),
            new ProjectSearchQuery("safety", ProjectArtifactType.Block));

        var result = Assert.Single(results);
        Assert.Equal("block-safety", result.ArtifactId);
    }

    [Fact]
    public void Search_WithBlankText_ReturnsNoArtifacts()
    {
        var provider = new DeterministicProjectSearchProvider();

        var results = provider.Search(CreateSnapshot(), new ProjectSearchQuery("  "));

        Assert.Empty(results);
    }

    private static ProjectSnapshot CreateSnapshot() => ProjectSnapshotFactory.Create(
        "project-1",
        "V19",
        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        [
            CreateArtifact("block-motor", "FB_Motor", ProjectArtifactType.Block, new Dictionary<string, string>
            {
                ["language"] = "SCL"
            }),
            CreateArtifact("block-safety", "FC_Safety", ProjectArtifactType.Block, new Dictionary<string, string>
            {
                ["comment"] = "Safety interlock"
            }),
            CreateArtifact("tag-run", "Motor_Run", ProjectArtifactType.Tag, new Dictionary<string, string>())
        ]);

    private static ProjectArtifact CreateArtifact(
        string id,
        string name,
        ProjectArtifactType type,
        IReadOnlyDictionary<string, string> metadata) => new(
            id,
            name,
            $"/Program blocks/{name}",
            type,
            $"source-{id}",
            metadata);
}