namespace EngineerPc.Engineering.Ir;

public static class EngineeringIrValidator
{
    public static IReadOnlyList<string> Validate(CreateBlockOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var errors = new List<string>();

        if (operation.OperationId == Guid.Empty)
        {
            errors.Add("Operation ID is required.");
        }

        if (string.IsNullOrWhiteSpace(operation.ProjectContext.ProjectId))
        {
            errors.Add("Project ID is required.");
        }

        if (string.IsNullOrWhiteSpace(operation.ProjectContext.SnapshotHash))
        {
            errors.Add("Project snapshot hash is required.");
        }

        if (string.IsNullOrWhiteSpace(operation.IdempotencyKey))
        {
            errors.Add("Idempotency key is required.");
        }

        if (string.IsNullOrWhiteSpace(operation.Name))
        {
            errors.Add("Block name is required.");
        }

        if (operation.Interface is null)
        {
            errors.Add("Block interface is required.");
        }

        return errors;
    }
}