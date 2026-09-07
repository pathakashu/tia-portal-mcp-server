using EngineerPc.ProjectModel;

namespace EngineerPc.ProjectSearch;

public interface IProjectSearchProvider
{
    IReadOnlyList<ProjectArtifact> Search(ProjectSnapshot snapshot, ProjectSearchQuery query);
}