using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;
using EngineerPc.Tia.Abstractions;

namespace EngineerPc.Mcp.Host;

public sealed class PlanningOnlyTiaAdapter : ITiaAdapter
{
    public Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ProjectContext?>(null);
    }

    public Task<TiaAdapterExecutionResult> CreateBlockAsync(
        CreateBlockOperation operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TiaAdapterExecutionResult(
            null,
            ["Execution is unavailable until a TIA Portal V18 adapter is configured."]));
    }
}