using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Tia.Abstractions;

public interface ITiaAdapter
{
    Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken);

    Task<TiaAdapterExecutionResult> CreateBlockAsync(
        CreateBlockOperation operation,
        CancellationToken cancellationToken);
}