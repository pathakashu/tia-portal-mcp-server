using EngineerPc.Contracts;

namespace EngineerPc.Engineering.Engine;

public interface IProjectContextReadService
{
    Task<ProjectContextReadResult> GetProjectContextAsync(
        string projectId,
        AuthenticatedIdentity identity,
        Guid operationId,
        CancellationToken cancellationToken);
}

public sealed record ProjectContextReadResult(
    ProjectContext? ProjectContext,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccess => ProjectContext is not null && Errors.Count == 0;
}