using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectGraph.Tests;

public sealed class ProjectGraphFactoryTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    [Fact]
    public void Create_WithCanonicalEdges_ReturnsStableOutgoingRelationships()
    {
        var snapshot = CreateSnapshot();

        var graph = ProjectGraphFactory.Create(snapshot,
        [
            new ProjectGraphEdge("block-motor", ProjectGraphRelationship.Writes, "tag-run"),
            new ProjectGraphEdge("block-motor", ProjectGraphRelationship.Reads, "tag-start"),
            new ProjectGraphEdge("block-motor", ProjectGraphRelationship.Uses, "db-motor")
        ]);

        var outgoingEdges = graph.GetOutgoingEdges("block-motor");

        Assert.Collection(
            outgoingEdges,
            edge => Assert.Equal(ProjectGraphRelationship.Reads, edge.Relationship),
            edge => Assert.Equal(ProjectGraphRelationship.Writes, edge.Relationship),
            edge => Assert.Equal(ProjectGraphRelationship.Uses, edge.Relationship));
    }

    [Fact]
    public void Create_WithUnknownEdgeEndpoint_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProjectGraphFactory.Create(
            CreateSnapshot(),
            [new ProjectGraphEdge("block-motor", ProjectGraphRelationship.Calls, "block-missing")]));

        Assert.Equal(
            "Graph edge 'block-motor' -> 'block-missing' references an artifact outside the snapshot. (Parameter 'edges')",
            exception.Message);
    }

    private static ProjectSnapshot CreateSnapshot() => ProjectSnapshotFactory.Create(
        "project-1",
        "V19",
        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero),
        [
            CreateArtifact("block-motor", "FB_Motor", ProjectArtifactType.Block),
            CreateArtifact("db-motor", "DB_Motor", ProjectArtifactType.DataBlock),
            CreateArtifact("tag-start", "Motor_Start", ProjectArtifactType.Tag),
            CreateArtifact("tag-run", "Motor_Run", ProjectArtifactType.Tag)
        ]);

    private static ProjectArtifact CreateArtifact(string id, string name, ProjectArtifactType type) => new(
        id,
        name,
        $"/Program blocks/{name}",
        type,
        $"source-{id}",
        EmptyMetadata);
}