using System.Text.RegularExpressions;
using EngineerPc.Engineering.Ir;

namespace EngineerPc.Engineering.Validation;

public sealed partial class CreateBlockOperationValidator : IEngineeringOperationValidator
{
    public ValidationResult Validate(CreateBlockOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var errors = EngineeringIrValidator.Validate(operation).ToList();
        if (!IsIdentifier(operation.Name))
        {
            errors.Add("Block name must start with a letter or underscore and contain only letters, digits, or underscores.");
        }

        if (operation.Interface?.Inputs is not { } inputs)
        {
            return new ValidationResult(errors);
        }

        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in inputs)
        {
            if (!IsIdentifier(parameter.Name))
            {
                errors.Add($"Input parameter name '{parameter.Name}' is invalid.");
            }

            if (!parameterNames.Add(parameter.Name))
            {
                errors.Add($"Input parameter name '{parameter.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(parameter.DataType))
            {
                errors.Add($"Input parameter '{parameter.Name}' must declare a data type.");
            }
        }

        foreach (var parameter in operation.Interface.Outputs ?? [])
        {
            if (!IsIdentifier(parameter.Name))
            {
                errors.Add($"Output parameter name '{parameter.Name}' is invalid.");
            }

            if (!parameterNames.Add(parameter.Name))
            {
                errors.Add($"Block interface parameter name '{parameter.Name}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(parameter.DataType))
            {
                errors.Add($"Output parameter '{parameter.Name}' must declare a data type.");
            }
        }

        return new ValidationResult(errors);
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IdentifierPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}