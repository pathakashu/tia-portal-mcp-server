using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Validation;

namespace EngineerPc.Engineering.Engine;

public sealed class EngineeringOperationPlanner : IEngineeringOperationPlanner
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IEngineeringOperationValidator validator;

    public EngineeringOperationPlanner(IEngineeringOperationValidator validator)
    {
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public PlanningResult Plan(CreateBlockOperation operation)
    {
        var validation = validator.Validate(operation);
        if (!validation.IsValid)
        {
            return new PlanningResult(null, validation.Errors);
        }

        var serializedOperation = JsonSerializer.Serialize(operation, SerializerOptions);
        var operationHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(serializedOperation)));
        var preview = $"Create {operation.BlockType} '{operation.Name}' using {operation.Language}.";

        return new PlanningResult(
            new EngineeringOperationPlan(operation.OperationId, operationHash, preview),
            []);
    }
}