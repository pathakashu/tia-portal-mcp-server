namespace EngineerPc.ProjectModel.Tests;

public sealed class ProjectSnapshotFactoryTests
{
    private static readonly DateTimeOffset CapturedAtUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    [Fact]
    public void Create_WithEquivalentArtifactsInDifferentOrder_ReturnsSameHash()
    {
        var motorBlock = CreateArtifact("block-motor", "FB_Motor", "block-source-1", new Dictionary<string, string>
        {
            ["language"] = "SCL",
            ["folder"] = "Program blocks"
        });
        var safetyBlock = CreateArtifact("block-safety", "FC_Safety", "block-source-2", new Dictionary<string, string>
        {
            ["folder"] = "Program blocks",
            ["language"] = "SCL"
        });

        var first = ProjectSnapshotFactory.Create("project-1", "V18", CapturedAtUtc, [motorBlock, safetyBlock]);
        var second = ProjectSnapshotFactory.Create("project-1", "V18", CapturedAtUtc, [safetyBlock, motorBlock]);

        Assert.Equal(first.SnapshotHash, second.SnapshotHash);
    }

    [Fact]
    public void Create_WithChangedArtifactSourceHash_ReturnsDifferentHash()
    {
        var first = ProjectSnapshotFactory.Create(
            "project-1",
            "V18",
            CapturedAtUtc,
            [CreateArtifact("block-motor", "FB_Motor", "block-source-1", EmptyMetadata)]);
        var second = ProjectSnapshotFactory.Create(
            "project-1",
            "V18",
            CapturedAtUtc,
            [CreateArtifact("block-motor", "FB_Motor", "block-source-2", EmptyMetadata)]);

        Assert.NotEqual(first.SnapshotHash, second.SnapshotHash);
    }

    [Fact]
    public void Create_WithDuplicateArtifactIds_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProjectSnapshotFactory.Create(
            "project-1",
            "V18",
            CapturedAtUtc,
            [
                CreateArtifact("block-motor", "FB_Motor", "block-source-1", EmptyMetadata),
                CreateArtifact("block-motor", "FB_Valve", "block-source-2", EmptyMetadata)
            ]));

        Assert.Equal("Artifact ID 'block-motor' is duplicated. (Parameter 'artifacts')", exception.Message);
    }

    private static ProjectArtifact CreateArtifact(
        string artifactId,
        string name,
        string sourceHash,
        IReadOnlyDictionary<string, string> metadata) => new(
            artifactId,
            name,
            $"/Program blocks/{name}",
            ProjectArtifactType.Block,
            sourceHash,
            metadata);
}