using EngineerPc.Contracts;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Tia.Abstractions;

public interface ITiaAdapter
{
    Task<ProjectContext?> GetProjectContextAsync(string projectId, CancellationToken cancellationToken);

    /// <param name="sclSourceText">
    /// The deterministically rendered SCL source for the block, when the operation uses the
    /// constrained Scl/Function/FunctionBlock surface; null otherwise. Adapters that write to a
    /// real controller require this; the mock and planning-only adapters ignore it.
    /// </param>
    Task<TiaAdapterExecutionResult> CreateBlockAsync(
        CreateBlockOperation operation,
        CancellationToken cancellationToken,
        string? sclSourceText = null);
}