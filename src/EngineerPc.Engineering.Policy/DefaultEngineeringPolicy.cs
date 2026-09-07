using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Policy;

public sealed class DefaultEngineeringPolicy : IEngineeringPolicy
{
    public PolicyDecision Evaluate(EngineeringOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return operation switch
        {
            CreateBlockOperation => new PolicyDecision(true, OperationRisk.Medium, true, null),
            _ => new PolicyDecision(
                false,
                OperationRisk.Critical,
                false,
                $"Operation type '{operation.OperationType}' is not permitted by the default policy.")
        };
    }
}