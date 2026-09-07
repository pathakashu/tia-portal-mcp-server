using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectSearch;

public sealed record ProjectSearchQuery(
    string Text,
    ProjectArtifactType? ArtifactType = null);