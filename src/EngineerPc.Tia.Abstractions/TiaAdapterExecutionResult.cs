using EngineerPc.Contracts;

namespace EngineerPc.Tia.Abstractions;

public sealed record TiaAdapterExecutionResult(
    ProjectContext? UpdatedProjectContext,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccess => UpdatedProjectContext is not null && Errors.Count == 0;
}